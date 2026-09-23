using System.Globalization;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>What a decoder is being kept alive for.</summary>
/// <remarks>
/// The two have different appetites. One follows the playhead and wants frames buffered ahead of
/// it; the other lands wherever the last click was and wants to hold as little as possible. Both
/// exist per media stream, so scrubbing does not disturb playback.
/// </remarks>
public enum DecoderRole
{
    /// <summary>Follows the playhead forward. Deeper frame pool, decode-ahead.</summary>
    Playhead,

    /// <summary>Lands on arbitrary times. Shallow pool, seeks often.</summary>
    Seek,
}

/// <summary>
/// A decoder borrowed from the pool. Disposing returns it rather than closing it.
/// </summary>
/// <remarks>
/// The whole point of the pool is that decoders outlive the operations that use them. Spike S2
/// measured forty short-lived decoders costing 105 MB of memory that one long-lived decoder does
/// not cost at all, so a lease must never close what it borrowed.
/// </remarks>
public sealed class DecoderLease : IDisposable
{
    private readonly DecoderPool _pool;
    private readonly DecoderPool.Entry _entry;
    private bool _returned;

    internal DecoderLease(DecoderPool pool, DecoderPool.Entry entry)
    {
        _pool = pool;
        _entry = entry;
    }

    /// <summary>The media this decoder is open on.</summary>
    public string Hash => _entry.Hash;

    /// <summary>The stream it decodes.</summary>
    public int StreamIndex => _entry.StreamIndex;

    /// <summary>What it is being kept for.</summary>
    public DecoderRole Role => _entry.Role;

    /// <summary>Whether it ended up on hardware or software.</summary>
    public DecodePath Path => _entry.Decoder.Path;

    /// <summary>The stream's frame rate.</summary>
    public Rational FrameRate => _entry.Decoder.FrameRate;

    /// <summary>Coded width.</summary>
    public int Width => _entry.Decoder.Width;

    /// <summary>Coded height.</summary>
    public int Height => _entry.Decoder.Height;

    /// <summary>
    /// The frames, conformed if the media asked for it. Seek with <see cref="IVideoSource.Flush"/>
    /// and then read.
    /// </summary>
    public IVideoSource Frames => _entry.Source;

    /// <summary>The raw seeker, for callers that want the seek counters or the nearest-keyframe mode.</summary>
    public Seeker Seeker => _entry.Seeker;

    /// <summary>Returns the decoder to the pool.</summary>
    public void Dispose()
    {
        if (_returned)
        {
            return;
        }

        _returned = true;
        _pool.Return(_entry);
    }
}

/// <summary>
/// Keeps decoders open across the operations that use them, within a memory budget.
/// </summary>
/// <remarks>
/// Decoder churn, not decoding, is what grows memory: spike S2 measured one decoder running for
/// ten minutes costing nothing and forty short-lived ones costing 105 MB. So a scrub does not
/// open a decoder, it borrows one, and the pool decides when to let one go.
///
/// **Thread affine.** A demuxer and a decoder belong to the thread that created them, so a pool
/// does too, and says so rather than corrupting a decode. The engine gives each decode thread its
/// own pool. Bookkeeping is not locked, because there is nothing to lock against.
/// </remarks>
public sealed class DecoderPool : IDisposable
{
    /// <summary>What the frame pools of every open decoder may add up to. 1.5 GB, as the hw-decode skill sets.</summary>
    public const long DefaultBudgetBytes = 1_610_612_736;

    /// <summary>Surfaces a playhead decoder keeps in flight, for decode-ahead.</summary>
    private const int PlayheadPoolDepth = 8;

    /// <summary>Surfaces a seek decoder keeps. It lands and reads one frame, so it needs almost none.</summary>
    private const int SeekPoolDepth = 2;

    private readonly ILogger _log = Log.ForContext<DecoderPool>();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly HardwareDeviceContext? _hardware;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private long _ticks;
    private bool _disposed;

    /// <summary>Creates a pool.</summary>
    /// <param name="hardware">The shared Direct3D device, or null to decode in software. Not owned.</param>
    /// <param name="budgetBytes">What the open decoders' frame pools may add up to.</param>
    public DecoderPool(HardwareDeviceContext? hardware = null, long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);

        _hardware = hardware;
        BudgetBytes = budgetBytes;
    }

    /// <summary>Raised when a decoder could not use hardware and opened in software instead.</summary>
    public event EventHandler<DecoderFellBackEventArgs>? FellBack;

    /// <summary>What the open decoders' frame pools may add up to.</summary>
    public long BudgetBytes { get; }

    /// <summary>What they are estimated to add up to now.</summary>
    public long EstimatedBytes { get; private set; }

    /// <summary>How many decoders are open.</summary>
    public int Count => _entries.Count;

    /// <summary>Decoders opened since the pool was created. Compare with <see cref="Count"/> when tuning.</summary>
    public long Opened { get; private set; }

    /// <summary>Decoders closed to stay inside the budget.</summary>
    public long Evicted { get; private set; }

    /// <summary>Leases that reused a decoder that was already open. The number this class exists to raise.</summary>
    public long Reused { get; private set; }

    /// <summary>
    /// Borrows a decoder for one media stream, opening one if the pool does not have it.
    /// </summary>
    /// <param name="item">The media, whose conform settings decide what the frames go through.</param>
    /// <param name="path">The resolved absolute path, or an image sequence pattern.</param>
    /// <param name="streamIndex">The video stream, or -1 for the file's best one.</param>
    /// <param name="role">What the decoder is for.</param>
    /// <param name="projectRate">The timeline frame rate a variable source is conformed to.</param>
    /// <param name="lane">
    /// Which of several users of the same stream and role this is. Two layers playing one file at
    /// different times each need their own decoder, or one decoder is dragged back and forth
    /// between them every frame. The compositor passes the track's stacking order.
    /// </param>
    public DecoderLease Rent(
        MediaItem item,
        string path,
        int streamIndex = -1,
        DecoderRole role = DecoderRole.Seek,
        Rational? projectRate = null,
        int lane = 0)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Resolved before the key is built, not after the decoder is open. Keying on -1 and
        // storing under the real index means every borrow misses and opens another decoder, which
        // is the one thing this class exists to stop.
        var key = new Key(item.Hash, ResolveStream(item, path, streamIndex), role, lane);

        if (_entries.TryGetValue(key, out Entry? existing))
        {
            if (existing.InUse)
            {
                throw new InvalidOperationException(
                    $"The {role} decoder for '{item.Name}' stream {streamIndex} is already on loan. "
                    + "One lease at a time per decoder; ask for a different role, or return the first.");
            }

            existing.InUse = true;
            existing.LastUsed = ++_ticks;
            Reused++;
            return new DecoderLease(this, existing);
        }

        Entry opened = Open(item, path, key.StreamIndex, role, projectRate ?? Rational.Fps30);
        _entries[key] = opened;
        EstimatedBytes += opened.Bytes;
        Opened++;

        opened.InUse = true;
        opened.LastUsed = ++_ticks;

        TrimToBudget();

        return new DecoderLease(this, opened);
    }

    /// <summary>Closes every decoder open on one media item, which a reprobe or a removal needs.</summary>
    public void Forget(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        VerifyThread();

        foreach (Key key in _entries
            .Where(pair => string.Equals(pair.Key.Hash, hash, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList())
        {
            Close(key, "forgotten");
        }
    }

    /// <summary>Closes every decoder that is not on loan.</summary>
    public void Clear()
    {
        VerifyThread();

        foreach (Key key in _entries.Where(pair => !pair.Value.InUse).Select(pair => pair.Key).ToList())
        {
            Close(key, "cleared");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Entry entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
        EstimatedBytes = 0;
    }

    /// <summary>Takes a decoder back. Called by the lease, never directly.</summary>
    internal void Return(Entry entry)
    {
        entry.InUse = false;

        if (_disposed)
        {
            return;
        }

        entry.LastUsed = ++_ticks;
        TrimToBudget();
    }

    /// <summary>
    /// How much memory one decoder's frame pool is worth.
    /// </summary>
    /// <remarks>
    /// Width times height times bytes per pixel times the surfaces in flight, as the hw-decode
    /// skill sets out. It is an estimate: the real allocation is the driver's business and is
    /// rounded up to its own alignment. What it has to be is proportional and cheap, because it
    /// is what eviction is decided by.
    /// </remarks>
    internal static long EstimateBytes(int width, int height, int bitDepth, int surfaces)
    {
        // NV12 or P010: one luma sample plus half a chroma sample per pixel.
        long bytesPerSample = bitDepth > 8 ? 2 : 1;
        return (long)width * height * bytesPerSample * 3 / 2 * surfaces;
    }

    private Entry Open(MediaItem item, string path, int streamIndex, DecoderRole role, Rational projectRate)
    {
        // Deinterlacing runs through libavfilter, which cannot read a Direct3D texture, so a file
        // that needs it decodes in software however good the GPU is.
        bool software = Conform.NeedsSoftwareDecode(item);
        HardwareDeviceContext? hardware = software ? null : _hardware;

        var demuxer = new Demuxer(path, SequenceOptions(item), fileMustExist: !path.Contains('%', StringComparison.Ordinal));

        try
        {
            int stream = streamIndex >= 0 ? streamIndex : demuxer.FindBestStream(StreamKind.Video);
            if (stream < 0)
            {
                throw new InvalidOperationException($"'{path}' has no video stream to decode.");
            }

            int depth = role == DecoderRole.Playhead ? PlayheadPoolDepth : SeekPoolDepth;
            var decoder = new VideoDecoder(demuxer, stream, hardware, depth);

            if (!software && hardware is not null && decoder.Path == DecodePath.Software)
            {
                FellBack?.Invoke(this, new DecoderFellBackEventArgs(
                    item.Id,
                    item.Name,
                    decoder.CodecName,
                    "The decoder did not offer a Direct3D 11 pixel format for this codec or profile."));
            }

            var seeker = new Seeker(demuxer, decoder, stream);
            IVideoSource source = Conform.Apply(seeker, item, projectRate, decoder.FrameRate);

            long bytes = EstimateBytes(decoder.Width, decoder.Height, BitDepthOf(item, stream), depth);

            _log.Debug(
                "Opened a {Role} decoder for {Media} stream {Stream} on the {Path} path, about {Megabytes:F1} MB",
                role,
                item.Name,
                stream,
                decoder.Path,
                bytes / (1024.0 * 1024));

            return new Entry(item.Hash, stream, role, demuxer, decoder, seeker, source, bytes);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Which stream a request means, before anything is opened.
    /// </summary>
    /// <remarks>
    /// The probe on the media item already says, which is the point of keeping it there. Opening
    /// the file to ask is the fallback for an item that was never imported, which is a test and
    /// not a project.
    /// </remarks>
    private static int ResolveStream(MediaItem item, string path, int streamIndex)
    {
        if (streamIndex >= 0)
        {
            return streamIndex;
        }

        if (item.Info?.VideoStreams.FirstOrDefault() is { } stream)
        {
            return stream.Index;
        }

        using var demuxer = new Demuxer(
            path,
            SequenceOptions(item),
            fileMustExist: !path.Contains('%', StringComparison.Ordinal));

        int best = demuxer.FindBestStream(StreamKind.Video);

        return best >= 0
            ? best
            : throw new InvalidOperationException($"'{path}' has no video stream to decode.");
    }

    /// <summary>Demuxer options an image sequence needs so its run starts at the right number.</summary>
    private static IReadOnlyDictionary<string, string>? SequenceOptions(MediaItem item)
    {
        if (item.Sequence is not { } sequence)
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["framerate"] = $"{sequence.FrameRate.Num}/{sequence.FrameRate.Den}",
            ["start_number"] = sequence.Start.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static int BitDepthOf(MediaItem item, int streamIndex) =>
        item.Info?.Streams.FirstOrDefault(stream => stream.Index == streamIndex)?.BitDepth is > 0 and int depth
            ? depth
            : 8;

    /// <summary>Closes the least recently used idle decoders until the budget is met.</summary>
    private void TrimToBudget()
    {
        while (EstimatedBytes > BudgetBytes)
        {
            Key? oldest = null;
            long oldestTick = long.MaxValue;

            foreach ((Key key, Entry entry) in _entries)
            {
                if (entry.InUse || entry.LastUsed >= oldestTick)
                {
                    continue;
                }

                oldest = key;
                oldestTick = entry.LastUsed;
            }

            if (oldest is not { } evict)
            {
                // Everything left is on loan. Going over budget is better than pulling a decoder
                // out from under the caller holding it, and the next return will trim.
                _log.Debug(
                    "Over the decoder budget at {Megabytes:F0} MB with every decoder on loan",
                    EstimatedBytes / (1024.0 * 1024));
                return;
            }

            Close(evict, "evicted");
            Evicted++;
        }
    }

    private void Close(Key key, string why)
    {
        if (!_entries.Remove(key, out Entry? entry))
        {
            return;
        }

        EstimatedBytes -= entry.Bytes;
        entry.Dispose();

        _log.Debug("Closed a {Role} decoder for {Hash} stream {Stream} ({Why})", key.Role, key.Hash, key.StreamIndex, why);
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"This decoder pool belongs to thread {_threadId} and was used from thread "
            + $"{Environment.CurrentManagedThreadId}. A demuxer and a decoder are thread affine, so a pool is too; "
            + "give each decode thread its own.");
    }

    private readonly record struct Key(string Hash, int StreamIndex, DecoderRole Role, int Lane);

    /// <summary>One open decoder and everything that belongs to it.</summary>
    internal sealed class Entry(
        string hash,
        int streamIndex,
        DecoderRole role,
        Demuxer demuxer,
        VideoDecoder decoder,
        Seeker seeker,
        IVideoSource source,
        long bytes) : IDisposable
    {
        internal string Hash { get; } = hash;

        internal int StreamIndex { get; } = streamIndex;

        internal DecoderRole Role { get; } = role;

        internal VideoDecoder Decoder { get; } = decoder;

        internal Seeker Seeker { get; } = seeker;

        internal IVideoSource Source { get; } = source;

        internal long Bytes { get; } = bytes;

        internal long LastUsed { get; set; }

        internal bool InUse { get; set; }

        public void Dispose()
        {
            // The conform chain owns the seeker it was built over, and the seeker does not own the
            // decoder, so the order matters and the demuxer goes last.
            Source.Dispose();

            if (!ReferenceEquals(Source, Seeker))
            {
                Seeker.Dispose();
            }

            Decoder.Dispose();
            demuxer.Dispose();
        }
    }
}

/// <summary>Says that a media item could not be decoded on the GPU after all.</summary>
/// <param name="MediaId">Which media item.</param>
/// <param name="Name">Its display name, for a message.</param>
/// <param name="Codec">The codec that was not offered a hardware format.</param>
/// <param name="Reason">Why, in a sentence somebody can act on.</param>
public sealed record DecoderFellBackEventArgs(string MediaId, string Name, string Codec, string Reason);
