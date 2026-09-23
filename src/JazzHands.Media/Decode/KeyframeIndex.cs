using System.Collections.Immutable;
using System.Text.Json;
using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>
/// Where every keyframe in one video stream is.
/// </summary>
/// <remarks>
/// Seeking lands on a keyframe and decodes forward, so how long a seek takes is decided by how
/// far the target is from the keyframe before it. Knowing that in advance is what lets the
/// frame server choose between decoding forward from where a decoder already is and seeking, and
/// it is what smart cut needs to find the GOPs a cut point falls inside.
///
/// Building one reads every packet in the file, which for a two hour recording is the whole file
/// once. That is why it is cached by content hash and why <see cref="Build"/> takes a
/// cancellation token. Nothing is decoded: the scan reads packet headers and their flags.
///
/// Times are packet presentation times, not decode times. A container's own seek index is keyed
/// on decode time, which for a stream with B-frames is not where the keyframe appears on screen;
/// the seeker learned that the hard way in Phase 03.
/// </remarks>
public sealed class KeyframeIndex
{
    /// <summary>The version written into a cached index, so an older one is ignored rather than trusted.</summary>
    private const int Version = 2;

    private readonly ImmutableArray<Flicks> _times;

    private KeyframeIndex(ImmutableArray<Flicks> times, Flicks duration, bool isComplete)
    {
        _times = times;
        Duration = duration;
        IsComplete = isComplete;
    }

    /// <summary>Every keyframe's presentation time, ascending.</summary>
    public IReadOnlyList<Flicks> Times => _times;

    /// <summary>How many keyframes the stream has.</summary>
    public int Count => _times.Length;

    /// <summary>The stream's duration as the scan saw it: the last packet's time plus its duration.</summary>
    public Flicks Duration { get; }

    /// <summary>
    /// False when the scan was cancelled part way, so the index describes a prefix of the file.
    /// An incomplete index is still useful and is never cached.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>True when the stream is all keyframes, as ProRes, DNx and image sequences are.</summary>
    /// <remarks>
    /// Worth knowing because a seek in such a stream never has to decode forward at all.
    /// </remarks>
    public bool IsAllKeyframes { get; private init; }

    /// <summary>
    /// True when pictures shown just before a keyframe are decoded after it, as in an open group of
    /// pictures (x265 by default, HEVC CRA frames).
    /// </summary>
    /// <remarks>
    /// Those leading pictures refer to the keyframe after them, so a stream copy that ends a stretch
    /// at a keyframe cannot keep them: the stretch comes out a few frames short. Smart cut (Phase 23)
    /// re-encodes them; until then the export planner says so.
    /// </remarks>
    public bool HasLeadingPictures { get; private init; }

    /// <summary>Reads a stream's keyframe positions.</summary>
    /// <param name="path">The file, or an image sequence pattern.</param>
    /// <param name="streamIndex">The video stream to scan.</param>
    /// <param name="cancellationToken">Stops the scan; what was found so far comes back incomplete.</param>
    /// <param name="demuxerOptions">
    /// Options for the demuxer, which an image sequence needs so its run starts at the right
    /// number. A path containing a printf pattern is not one file, so its existence is not
    /// checked.
    /// </param>
    public static KeyframeIndex Build(
        string path,
        int streamIndex,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? demuxerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        bool pattern = path.Contains('%', StringComparison.Ordinal);

        // Its own demuxer: the scan runs to end of file, and doing that through a decoder's
        // demuxer would leave the decoder somewhere it did not ask to be.
        using var demuxer = new Demuxer(path, demuxerOptions, fileMustExist: !pattern);
        return Build(demuxer, streamIndex, cancellationToken);
    }

    /// <summary>Reads a stream's keyframe positions through an existing demuxer, which it rewinds.</summary>
    public static unsafe KeyframeIndex Build(
        Demuxer demuxer,
        int streamIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        Rational timeBase = demuxer.GetTimeBase(streamIndex);
        var times = ImmutableArray.CreateBuilder<Flicks>();
        Flicks end = Flicks.Zero;
        long packets = 0;
        bool complete = true;
        bool leading = false;
        Flicks? lastKeyframe = null;

        demuxer.Rewind();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                complete = false;
                break;
            }

            AVPacket* packet = demuxer.ReadPacket(streamIndex);
            if (packet is null)
            {
                break;
            }

            packets++;

            long stamp = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
            if (stamp == ffmpeg.AV_NOPTS_VALUE)
            {
                continue;
            }

            Flicks at = Flicks.FromTimebase(stamp, timeBase);

            if (packet->duration > 0)
            {
                Flicks packetEnd = at + Flicks.FromTimebase(packet->duration, timeBase);
                if (packetEnd > end)
                {
                    end = packetEnd;
                }
            }
            else if (at > end)
            {
                end = at;
            }

            if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0)
            {
                times.Add(at);
                lastKeyframe = at;
            }
            else if (lastKeyframe is { } keyframe && at < keyframe)
            {
                leading = true;
            }
        }

        demuxer.Rewind();

        // Keyframes come out in decode order, and a B-pyramid can present one before a packet
        // that was read earlier. Sorting costs nothing next to the scan and makes every lookup a
        // binary search.
        ImmutableArray<Flicks> sorted = [.. times.Order()];

        Log.ForContext<KeyframeIndex>().Debug(
            "Indexed {Keyframes} keyframes in {Packets} packets of stream {Stream} of {File}",
            sorted.Length,
            packets,
            streamIndex,
            demuxer.Path);

        return new KeyframeIndex(sorted, end, complete)
        {
            IsAllKeyframes = complete && packets > 0 && sorted.Length == packets,
            HasLeadingPictures = leading,
        };
    }

    /// <summary>The cached index for a hash and stream, or null when there is none or it is stale.</summary>
    public static KeyframeIndex? Load(CacheManager cache, string hash, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(cache);

        string? json = cache.GetKeyframes(hash, streamIndex);
        if (json is null)
        {
            return null;
        }

        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(json);

            if (stored is null || stored.Version != Version)
            {
                return null;
            }

            return new KeyframeIndex(
                [.. stored.Times.Select(value => new Flicks(value))],
                new Flicks(stored.Duration),
                isComplete: true)
            {
                IsAllKeyframes = stored.AllKeyframes,
                HasLeadingPictures = stored.LeadingPictures,
            };
        }
        catch (JsonException error)
        {
            // A cache entry an older build wrote, or one that was truncated. Rebuilding is cheap
            // enough that refusing to read it is better than guessing at it.
            Log.ForContext<KeyframeIndex>().Debug(error, "Ignoring an unreadable keyframe index for {Hash}", hash);
            return null;
        }
    }

    /// <summary>Stores this index against a hash and stream. An incomplete index is not stored.</summary>
    public bool Save(CacheManager cache, string hash, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(cache);

        if (!IsComplete)
        {
            return false;
        }

        var stored = new Stored(
            Version,
            [.. _times.Select(time => time.Value)],
            Duration.Value,
            IsAllKeyframes,
            HasLeadingPictures);

        cache.PutKeyframes(hash, streamIndex, JsonSerializer.Serialize(stored));
        return true;
    }

    /// <summary>
    /// The last keyframe at or before <paramref name="time"/>, which is where a seek to it lands.
    /// </summary>
    /// <returns>Zero when the index is empty or the time is before the first keyframe.</returns>
    public Flicks AtOrBefore(Flicks time)
    {
        int at = IndexAtOrBefore(time);
        return at < 0 ? Flicks.Zero : _times[at];
    }

    /// <summary>The first keyframe strictly after <paramref name="time"/>, or null past the last one.</summary>
    public Flicks? After(Flicks time)
    {
        int at = IndexAtOrBefore(time);

        // IndexAtOrBefore returns the one at or before, so the next is one along, except when the
        // time landed exactly on a keyframe, where it is still one along.
        int next = at + 1;
        return next >= 0 && next < _times.Length ? _times[next] : null;
    }

    /// <summary>
    /// The position of the keyframe at or before a time, or -1 when the time is before the first.
    /// </summary>
    public int IndexAtOrBefore(Flicks time)
    {
        if (_times.IsEmpty)
        {
            return -1;
        }

        int found = _times.AsSpan().BinarySearch(time);
        if (found >= 0)
        {
            return found;
        }

        // BinarySearch gives the complement of the first larger element, so the one before it is
        // the keyframe this time sits inside.
        return ~found - 1;
    }

    /// <summary>
    /// The group of pictures a time falls inside: from its keyframe to the next one, or to the
    /// end of the stream for the last group.
    /// </summary>
    public TimeRange GopContaining(Flicks time)
    {
        int at = IndexAtOrBefore(time);

        if (at < 0)
        {
            // Before the first keyframe. The file starts here whatever the index says.
            Flicks first = _times.IsEmpty ? Duration : _times[0];
            return new TimeRange(Flicks.Zero, first);
        }

        Flicks start = _times[at];
        Flicks end = at + 1 < _times.Length ? _times[at + 1] : Duration;

        return new TimeRange(start, end > start ? end - start : Flicks.Zero);
    }

    /// <summary>
    /// How far past its keyframe a time sits, which is what a seek to it would have to decode.
    /// </summary>
    public Flicks DistanceFromKeyframe(Flicks time)
    {
        Flicks keyframe = AtOrBefore(time);
        return time > keyframe ? time - keyframe : Flicks.Zero;
    }

    /// <summary>What goes in the cache. A record so a version bump is a compile error away from being handled.</summary>
    private sealed record Stored(int Version, long[] Times, long Duration, bool AllKeyframes, bool LeadingPictures);
}
