using System.Collections.Concurrent;
using System.Collections.Frozen;
using JazzHands.Audio;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using Serilog;

namespace JazzHands.Engine.Audio;

/// <summary>What a read does when a block is not in the cache.</summary>
public enum AudioReadMode
{
    /// <summary>Decode it there and then. For export, tests and anything else that may wait.</summary>
    Blocking,

    /// <summary>Play silence and count a miss. For the audio thread, which may not wait.</summary>
    Realtime,
}

/// <summary>
/// Serves the graph's sample reads from the block cache, decoding into it as needed.
/// </summary>
/// <remarks>
/// This is the engine's side of <see cref="IAudioSampleSource"/>: the graph names a media item
/// and a stream, and this resolves the name to a file and a content hash, finds the blocks, and
/// copies the samples out.
///
/// It runs in one of two modes. Blocking decodes a missing block on the spot, which is right for
/// export and for tests. Realtime never decodes on the reading thread: a missing block is silence
/// and a count, and <see cref="Prefetch"/>, called from a decode thread that runs ahead of the
/// playhead, is what fills the cache in time.
///
/// Decoders are thread affine, so the readers belong to whichever thread first decodes, and any
/// other thread that tries to is refused rather than allowed to corrupt one. Reads that only copy
/// from the cache are safe on any thread, which is what lets the audio thread read what a
/// prefetch thread wrote.
/// </remarks>
public sealed class AudioSampleServer : IAudioSampleSource, IDisposable
{
    /// <summary>More open streams than this and the least recently used one is closed.</summary>
    private const int MaxReaders = 32;

    private static readonly FrozenDictionary<string, ResolvedMedia> NoMedia =
        FrozenDictionary<string, ResolvedMedia>.Empty;

    private readonly ILogger _log = Log.ForContext<AudioSampleServer>();
    private readonly AudioBlockCache _cache;
    private readonly Dictionary<(string Hash, int Stream), ReaderSlot> _readers = [];
    private readonly ConcurrentDictionary<(string Hash, int Stream), long> _ends = new();
    private readonly ConcurrentDictionary<(string Hash, int Stream), bool> _failed = new();
    private readonly StretchRenderer _stretcher = new();
    private readonly ConcurrentDictionary<(string Media, string Plan), string> _stretchKeys = new();
    private FrozenDictionary<string, ResolvedMedia> _media = NoMedia;
    private int _ownerThread = -1;
    private long _readerClock;
    private long _misses;
    private bool _disposed;

    /// <summary>Creates a server.</summary>
    /// <param name="cache">Where blocks are kept. May be shared with other servers at the same rate.</param>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="mode">What a read does about a block that is not there.</param>
    public AudioSampleServer(AudioBlockCache cache, int sampleRate, AudioReadMode mode)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _cache = cache;
        SampleRate = sampleRate;
        Mode = mode;
    }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>Blocking or realtime.</summary>
    public AudioReadMode Mode { get; }

    /// <summary>The cache.</summary>
    public AudioBlockCache Cache => _cache;

    /// <summary>
    /// How many blocks a blocking read decodes past the one it needed. About a second, so a
    /// sequential pull decodes in runs rather than a block at a time.
    /// </summary>
    public int ReadAheadBlocks { get; set; } = 24;

    /// <summary>Reads that found some of their range undecoded and played silence for it.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Seeks made by all the readers, for checking that playback decodes on.</summary>
    public int Seeks => _readers.Values.Sum(slot => slot.Reader.Seeks);

    /// <summary>
    /// Tells the server which files the media identifiers mean. Call whenever the project
    /// changes; the lookup is replaced whole, so a read in progress sees the old one or the new.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">Where it lives, for relative media paths. Empty for an unsaved project.</param>
    public void Update(Project project, string projectPath = "")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(projectPath);

        var media = new Dictionary<string, ResolvedMedia>(StringComparer.Ordinal);
        foreach (MediaItem item in project.Media)
        {
            string path = projectPath.Length == 0 ? item.RelativePath : ProjectPaths.Resolve(projectPath, item.RelativePath);

            // The hash is the cache key, so a file replaced under the same name is decoded afresh.
            // A project made by hand may not have one yet; the path stands in for it.
            string key = item.Hash.Length > 0 ? item.Hash : "path:" + path;
            media[item.Id] = new ResolvedMedia(key, path);
        }

        Volatile.Write(ref _media, media.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public bool Read(AudioSourceRef source, long startSample, AudioBuffer destination, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int channels = Math.Min(source.Channels, destination.Channels);

        if (!Volatile.Read(ref _media).TryGetValue(source.MediaId, out ResolvedMedia media)
            || _failed.ContainsKey((media.Key, source.StreamIndex)))
        {
            // Nothing to wait for: the media is not in the project or cannot be decoded.
            Silence(destination, channels, offset, frames);
            return true;
        }

        if (source.Stretch is { } plan)
        {
            return ReadStretched(media, source, plan, startSample, destination, offset, frames);
        }

        bool ready = true;
        long position = startSample;
        long end = startSample + frames;

        while (position < end)
        {
            long block = Dsp.FloorDiv(position, AudioBlockCache.BlockFrames);
            int within = (int)(position - (block * AudioBlockCache.BlockFrames));
            int count = (int)Math.Min(AudioBlockCache.BlockFrames - within, end - position);
            int at = offset + (int)(position - startSample);

            if (block < 0 || IsPastEnd(media.Key, source.StreamIndex, block))
            {
                Silence(destination, channels, at, count);
            }
            else if (FindBlock(media, source.StreamIndex, block) is { } found)
            {
                Copy(found, destination, channels, within, at, count);
            }
            else if (IsPastEnd(media.Key, source.StreamIndex, block) || _failed.ContainsKey((media.Key, source.StreamIndex)))
            {
                Silence(destination, channels, at, count);
            }
            else
            {
                Silence(destination, channels, at, count);
                ready = false;
            }

            position += count;
        }

        if (!ready)
        {
            Interlocked.Increment(ref _misses);
        }

        return ready;
    }

    /// <summary>
    /// Decodes whatever part of a range is not in the cache yet. On the thread that owns the
    /// readers.
    /// </summary>
    public void Prefetch(AudioSourceRef source, long startSample, long frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frames <= 0 || !Volatile.Read(ref _media).TryGetValue(source.MediaId, out ResolvedMedia media))
        {
            return;
        }

        if (source.Stretch is { } stretch)
        {
            string stretchKey = StretchKey(media, stretch);
            long rendered = long.MinValue;
            for (long block = Dsp.FloorDiv(startSample, AudioBlockCache.BlockFrames); block <= Dsp.FloorDiv(startSample + frames - 1, AudioBlockCache.BlockFrames); block++)
            {
                long chunk = Dsp.FloorDiv(block, ChunkBlocks);
                if (chunk != rendered && !_cache.Contains(new AudioBlockKey(stretchKey, source.StreamIndex, SampleRate, block)))
                {
                    RenderChunk(media, source, stretch, chunk);
                    rendered = chunk;
                }
            }

            return;
        }

        long first = Math.Max(0, Dsp.FloorDiv(startSample, AudioBlockCache.BlockFrames));
        long last = Dsp.FloorDiv(startSample + frames - 1, AudioBlockCache.BlockFrames);

        for (long block = first; block <= last; block++)
        {
            if (IsPastEnd(media.Key, source.StreamIndex, block))
            {
                return;
            }

            AudioBlockKey key = new(media.Key, source.StreamIndex, SampleRate, block);
            if (!_cache.Contains(key))
            {
                Decode(media, source.StreamIndex, block, (int)Math.Min(last - block + 1, int.MaxValue));
            }
        }
    }

    /// <summary>Closes every reader. On the thread that owns them.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ReaderSlot slot in _readers.Values)
        {
            slot.Reader.Dispose();
        }

        _readers.Clear();
    }

    private static void Silence(AudioBuffer destination, int channels, int offset, int frames)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            destination.Plane(channel, offset, frames).Clear();
        }
    }

    private static void Copy(AudioBlock block, AudioBuffer destination, int channels, int from, int offset, int frames)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            Span<float> into = destination.Plane(channel, offset, frames);

            if (channel < block.Channels)
            {
                block.Plane(channel).Slice(from, frames).CopyTo(into);
            }
            else
            {
                into.Clear();
            }
        }
    }

    /// <summary>Cache blocks in one rendered chunk of stretched sound.</summary>
    private const int ChunkBlocks = StretchRenderer.ChunkFrames / AudioBlockCache.BlockFrames;

    /// <summary>
    /// A stretched sound's cache key: the file's content and the plan. Made once per pair and kept,
    /// so the audio thread only looks it up; joining the strings there would allocate.
    /// </summary>
    private string StretchKey(ResolvedMedia media, StretchPlan plan) =>
        _stretchKeys.GetOrAdd((media.Key, plan.Key), static pair => pair.Media + "|" + pair.Plan);

    /// <summary>
    /// A clip's stretched sound (Phase 36), in clip samples: from the cache, rendered on the spot
    /// when blocking, silence and a miss when real time.
    /// </summary>
    private bool ReadStretched(ResolvedMedia media, AudioSourceRef source, StretchPlan plan, long startSample, AudioBuffer destination, int offset, int frames)
    {
        int channels = Math.Min(source.Channels, destination.Channels);
        string stretchKey = StretchKey(media, plan);
        bool ready = true;
        long position = startSample;
        long end = startSample + frames;

        while (position < end)
        {
            long block = Dsp.FloorDiv(position, AudioBlockCache.BlockFrames);
            int within = (int)(position - (block * AudioBlockCache.BlockFrames));
            int count = (int)Math.Min(AudioBlockCache.BlockFrames - within, end - position);
            int at = offset + (int)(position - startSample);
            var key = new AudioBlockKey(stretchKey, source.StreamIndex, SampleRate, block);

            if (!_cache.TryGet(key, out AudioBlock? found) && Mode == AudioReadMode.Blocking && !_disposed)
            {
                RenderChunk(media, source, plan, Dsp.FloorDiv(block, ChunkBlocks));
                _cache.TryGet(key, out found);
            }

            if (found is not null)
            {
                Copy(found, destination, channels, within, at, count);
            }
            else
            {
                Silence(destination, channels, at, count);
                ready = false;
            }

            position += count;
        }

        if (!ready)
        {
            Interlocked.Increment(ref _misses);
        }

        return ready;
    }

    /// <summary>Stretches one chunk of a clip's sound and puts its blocks in the cache. On the thread that decodes.</summary>
    private void RenderChunk(ResolvedMedia media, AudioSourceRef source, StretchPlan plan, long chunk)
    {
        if (_failed.ContainsKey((media.Key, source.StreamIndex)))
        {
            return;
        }

        string stretchKey = StretchKey(media, plan);
        float[][] samples = _stretcher.Render(
            stretchKey,
            plan,
            source.Channels,
            SampleRate,
            chunk,
            (start, count, into) => ReadPlain(media, source.StreamIndex, start, count, into));

        for (int index = 0; index < ChunkBlocks; index++)
        {
            var planes = new float[samples.Length][];
            for (int channel = 0; channel < samples.Length; channel++)
            {
                planes[channel] = samples[channel].AsSpan(index * AudioBlockCache.BlockFrames, AudioBlockCache.BlockFrames).ToArray();
            }

            _cache.Add(new AudioBlockKey(stretchKey, source.StreamIndex, SampleRate, (chunk * ChunkBlocks) + index), new AudioBlock(planes));
        }
    }

    /// <summary>The file's own samples for the stretcher, decoded as needed; silence outside the file.</summary>
    private void ReadPlain(ResolvedMedia media, int stream, long start, int frames, float[][] into)
    {
        long position = start;
        long end = start + frames;

        while (position < end)
        {
            long block = Dsp.FloorDiv(position, AudioBlockCache.BlockFrames);
            int within = (int)(position - (block * AudioBlockCache.BlockFrames));
            int count = (int)Math.Min(AudioBlockCache.BlockFrames - within, end - position);
            int at = (int)(position - start);
            var key = new AudioBlockKey(media.Key, stream, SampleRate, block);

            if (block >= 0 && !IsPastEnd(media.Key, stream, block) && !_cache.Contains(key))
            {
                Decode(media, stream, block, (int)Math.Min(Dsp.FloorDiv(end - 1, AudioBlockCache.BlockFrames) - block + 1, Math.Max(1, ReadAheadBlocks)));
            }

            if (block >= 0 && _cache.TryGet(key, out AudioBlock? found))
            {
                for (int channel = 0; channel < into.Length; channel++)
                {
                    if (channel < found.Channels)
                    {
                        found.Plane(channel).Slice(within, count).CopyTo(into[channel].AsSpan(at, count));
                    }
                }
            }

            position += count;
        }
    }

    private bool IsPastEnd(string key, int stream, long block) =>
        _ends.TryGetValue((key, stream), out long end) && block >= end;

    private AudioBlock? FindBlock(ResolvedMedia media, int stream, long block)
    {
        var key = new AudioBlockKey(media.Key, stream, SampleRate, block);

        if (_cache.TryGet(key, out AudioBlock? found))
        {
            return found;
        }

        if (Mode == AudioReadMode.Realtime || _disposed)
        {
            return null;
        }

        Decode(media, stream, block, Math.Max(1, ReadAheadBlocks));
        return _cache.TryGet(key, out found) ? found : null;
    }

    /// <summary>Decodes a run of blocks on the owning thread, noting the end and any failure.</summary>
    private void Decode(ResolvedMedia media, int stream, long block, int count)
    {
        var id = (media.Key, stream);
        if (_failed.ContainsKey(id))
        {
            return;
        }

        VerifyOwner();

        try
        {
            AudioBlockReader reader = ReaderFor(media, stream);
            reader.Fill(block, count);

            if (reader.EndBlock is { } end)
            {
                _ends[id] = end;
            }
        }
        catch (Exception exception)
        {
            // A missing or broken file plays as silence rather than stopping playback or export.
            // It is logged once; validation and the media panel are where it is reported.
            _failed[id] = true;
            _log.Warning(exception, "Audio stream {Stream} of {Path} cannot be decoded and will play as silence", stream, media.Path);
        }
    }

    private void VerifyOwner()
    {
        int thread = Environment.CurrentManagedThreadId;
        if (_ownerThread < 0)
        {
            _ownerThread = thread;
        }
        else if (_ownerThread != thread)
        {
            throw new InvalidOperationException(
                $"This audio sample server decodes on thread {_ownerThread} and was asked to on {thread}. "
                + "Decoders belong to one thread; read from the cache elsewhere or give the thread its own server.");
        }
    }

    private AudioBlockReader ReaderFor(ResolvedMedia media, int stream)
    {
        var id = (media.Key, stream);
        if (_readers.TryGetValue(id, out ReaderSlot? slot))
        {
            slot.LastUsed = ++_readerClock;
            return slot.Reader;
        }

        if (_readers.Count >= MaxReaders)
        {
            KeyValuePair<(string, int), ReaderSlot> oldest = _readers.MinBy(entry => entry.Value.LastUsed);
            oldest.Value.Reader.Dispose();
            _readers.Remove(oldest.Key);
        }

        var reader = new AudioBlockReader(media.Path, media.Key, stream, SampleRate, _cache);
        _readers[id] = new ReaderSlot(reader) { LastUsed = ++_readerClock };
        return reader;
    }

    private readonly record struct ResolvedMedia(string Key, string Path);

    private sealed class ReaderSlot(AudioBlockReader reader)
    {
        public AudioBlockReader Reader { get; } = reader;

        public long LastUsed { get; set; }
    }
}
