using JazzHands.Audio;
using JazzHands.Core.Time;
using JazzHands.Media.Audio;
using JazzHands.Media.Decode;

namespace JazzHands.Engine.Audio;

/// <summary>
/// Decodes one audio stream into cache blocks on a fixed grid.
/// </summary>
/// <remarks>
/// A decoder hands out frames of whatever size the codec likes (1024 samples for AAC, 1152 for
/// MP3, a varying number after a rate conversion) starting wherever a seek landed. The cache
/// wants blocks of exactly <see cref="AudioBlockCache.BlockFrames"/> starting on multiples of it,
/// so that finding sample n is one division. This is the join: it accumulates frames into the
/// block they belong to and hands each block to the cache as it fills.
///
/// A seek goes a little before the block asked for and throws the difference away. The first
/// frame a decoder produces after a seek is wrong: AAC and MP3 overlap each frame with the one
/// before it, which the decoder has not seen, and the rate converter starts with an empty
/// filter. Decoding a pre-roll first and discarding it is what lets a block decoded after a seek
/// be identical to the same block decoded straight through. The one exception is a stream that
/// starts after the point asked for, where there is nothing earlier to discard.
///
/// Reading on from where the last fill stopped does not seek, which is what makes playback a
/// straight decode rather than a seek per block.
///
/// Thread affine, like the decoder inside it.
/// </remarks>
internal sealed class AudioBlockReader : IDisposable
{
    /// <summary>
    /// How far ahead of the decoder a request can be and still be reached by decoding on rather
    /// than seeking. About two seconds, which decodes in a few milliseconds.
    /// </summary>
    private const int MaxDecodeOnBlocks = 48;

    /// <summary>
    /// How far before a seek target decoding starts. Four blocks, about 170 ms at 48 kHz: several
    /// AAC or MP3 frames, and more than Opus asks for.
    /// </summary>
    private const int PreRollFrames = 4 * AudioBlockCache.BlockFrames;

    private const int BlockFrames = AudioBlockCache.BlockFrames;

    private readonly Demuxer _demuxer;
    private readonly AudioDecoder _decoder;
    private readonly AudioBlockCache _cache;
    private readonly string _hash;
    private readonly int _stream;
    private readonly float[][] _pending;
    private bool _positioned;
    private long _seekTarget;
    private long _pendingBlock;
    private int _pendingFill;
    private bool _disposed;

    /// <summary>Opens a stream for block reading.</summary>
    /// <param name="path">The file.</param>
    /// <param name="hash">The media's content hash, for the cache keys.</param>
    /// <param name="stream">The audio stream.</param>
    /// <param name="sampleRate">The mix rate to convert to.</param>
    /// <param name="cache">Where blocks go.</param>
    public AudioBlockReader(string path, string hash, int stream, int sampleRate, AudioBlockCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _demuxer = new Demuxer(path);

        try
        {
            _decoder = new AudioDecoder(_demuxer, stream, sampleRate);
        }
        catch
        {
            _demuxer.Dispose();
            throw;
        }

        _cache = cache;
        _hash = hash;
        _stream = stream;
        SampleRate = sampleRate;
        Channels = Math.Min(_decoder.Channels, Dsp.MaxChannels);
        _pending = new float[Channels][];

        for (int channel = 0; channel < Channels; channel++)
        {
            _pending[channel] = new float[BlockFrames];
        }
    }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>The stream's channel count.</summary>
    public int Channels { get; }

    /// <summary>The first block past the end of the stream, once the end has been seen.</summary>
    public long? EndBlock { get; private set; }

    /// <summary>Seeks made, for tests that check playback decodes on rather than seeking per block.</summary>
    public int Seeks { get; private set; }

    /// <summary>The key a block of this stream is cached under.</summary>
    public AudioBlockKey KeyFor(long block) => new(_hash, _stream, SampleRate, block);

    /// <summary>
    /// Decodes blocks into the cache, from one block for a number of blocks, or to the end of the
    /// stream if that comes first.
    /// </summary>
    public void Fill(long firstBlock, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(firstBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (EndBlock is { } end && firstBlock >= end)
        {
            return;
        }

        bool reachable = _positioned
            && _pendingBlock <= firstBlock
            && firstBlock - _pendingBlock <= MaxDecodeOnBlocks;

        if (!reachable)
        {
            Seek(firstBlock * BlockFrames);
        }

        long stopAt = firstBlock + count;

        while (!_positioned || _pendingBlock < stopAt)
        {
            using AudioFrame? frame = _decoder.ReadFrame();

            if (frame is null)
            {
                FinishAtEnd();
                return;
            }

            Consume(frame);
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
        _decoder.Dispose();
        _demuxer.Dispose();
    }

    private void Seek(long sample)
    {
        _decoder.SeekTo(Flicks.FromSamples(Math.Max(0, sample - PreRollFrames), SampleRate));
        _positioned = false;
        _seekTarget = sample;
        _pendingFill = 0;
        Seeks++;
    }

    private void Consume(AudioFrame frame)
    {
        long start = frame.Pts.ToSamples(SampleRate, RoundingMode.Nearest);
        int frames = frame.Frames;

        if (!_positioned)
        {
            // The first frame after a seek or an open. Everything before the block asked for is
            // pre-roll and is discarded, unless the stream starts after it and there is none.
            _pendingBlock = start <= _seekTarget
                ? Dsp.FloorDiv(_seekTarget, BlockFrames)
                : Dsp.FloorDiv(start, BlockFrames);

            ClearPending();
            _positioned = true;
        }
        else if (start - Position > MaxDecodeOnBlocks * (long)BlockFrames)
        {
            // A timestamp jump too far to fill with silence: start again where it landed.
            _pendingBlock = Dsp.FloorDiv(start, BlockFrames);
            ClearPending();
        }

        // Everything is placed relative to the one position the pending block has reached.
        // Timestamps rounded from another rate can land a sample either side of it; trust the
        // count for that, since a real gap or overlap is larger.
        long position = Position;
        if (Math.Abs(start - position) <= 1)
        {
            start = position;
        }

        if (start > position)
        {
            AppendSilence(start - position);
        }

        int skip = (int)Math.Clamp(position - start, 0, frames);
        Append(frame, skip, frames - skip);
    }

    /// <summary>The sample the next one appended lands on.</summary>
    private long Position => (_pendingBlock * BlockFrames) + _pendingFill;

    private void Append(AudioFrame frame, int from, int count)
    {
        while (count > 0)
        {
            int take = Math.Min(count, BlockFrames - _pendingFill);

            for (int channel = 0; channel < Channels; channel++)
            {
                Span<float> into = _pending[channel].AsSpan(_pendingFill, take);
                if (channel < frame.Channels)
                {
                    frame.Plane(channel).Slice(from, take).CopyTo(into);
                }
                else
                {
                    into.Clear();
                }
            }

            _pendingFill += take;
            from += take;
            count -= take;

            if (_pendingFill == BlockFrames)
            {
                Emit();
            }
        }
    }

    private void AppendSilence(long count)
    {
        while (count > 0)
        {
            int take = (int)Math.Min(count, BlockFrames - _pendingFill);
            _pendingFill += take;
            count -= take;

            if (_pendingFill == BlockFrames)
            {
                Emit();
            }
        }
    }

    /// <summary>Hands the pending block to the cache and starts the next one.</summary>
    private void Emit()
    {
        if (_pendingBlock >= 0)
        {
            AudioBlockKey key = KeyFor(_pendingBlock);
            if (!_cache.Contains(key))
            {
                var planes = new float[Channels][];
                for (int channel = 0; channel < Channels; channel++)
                {
                    planes[channel] = (float[])_pending[channel].Clone();
                }

                _cache.Add(key, new AudioBlock(planes));
            }
        }

        _pendingBlock++;
        ClearPending();
    }

    private void FinishAtEnd()
    {
        // The last block is padded with silence, which is what is there.
        if (_pendingFill > 0)
        {
            Emit();
        }

        EndBlock = _pendingBlock;

        // The decoder has drained, so the next fill has to seek whatever it asks for.
        _positioned = false;
        _pendingBlock = long.MaxValue;
    }

    private void ClearPending()
    {
        _pendingFill = 0;
        foreach (float[] plane in _pending)
        {
            Array.Clear(plane);
        }
    }
}
