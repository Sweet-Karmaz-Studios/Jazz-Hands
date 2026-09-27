using JazzHands.Audio;
using JazzHands.Media.Filters;

namespace JazzHands.Engine.Audio;

/// <summary>
/// Renders a clip's sound at its speed with its pitch kept (Phase 36), a chunk at a time.
/// </summary>
/// <remarks>
/// <para>
/// A time stretcher carries state from one sample to the next, but playback seeks and export
/// starts anywhere. So the sound is cut into chunks of <see cref="ChunkFrames"/> clip samples,
/// each stretched on its own from a little before it (<see cref="PreRoll"/>, for the stretcher to
/// settle) to a little after (<see cref="Overlap"/> plus <see cref="PostRoll"/>).
/// </para>
/// <para>
/// Two renders of the same sound agree on its pitch but not on its phase, so where one chunk
/// meets the next the new chunk is slid by up to <see cref="MaxShift"/> samples to where its
/// waveform best matches the one before's continuation, then crossfaded into it over
/// <see cref="Overlap"/> samples (the way WSOLA joins grains): a steady tone runs through the join
/// without a dip or a jump. That makes a chunk depend on the one rendered before it, so
/// rendering follows play: a chunk whose predecessor has not been rendered in this run (after a
/// seek) starts a new run, heard at most as one soft join where it meets what was cached before.
/// </para>
/// <para>
/// A chunk's tempo is the source it covers over its length: exact at one speed, the average over
/// the chunk on a speed curve, where the pitch stays put and the timing follows the curve chunk by
/// chunk. Past what the stretcher does (<see cref="StretchPlan.MinTempo"/> to
/// <see cref="StretchPlan.MaxTempo"/>) a chunk is read like tape instead. Runs on the thread that
/// decodes; allocates, so never on the audio thread.
/// </para>
/// </remarks>
internal sealed class StretchRenderer
{
    /// <summary>Clip samples in a chunk: sixteen cache blocks, about two thirds of a second at 48 kHz.</summary>
    public const int ChunkFrames = AudioBlockCache.BlockFrames * 16;

    /// <summary>Samples stretched before a chunk for the stretcher to settle.</summary>
    public const int PreRoll = 8192;

    /// <summary>Samples crossfaded where one chunk meets the next.</summary>
    public const int Overlap = 2048;

    /// <summary>Samples stretched past the overlap, so the stretcher is not flushing where it counts.</summary>
    public const int PostRoll = 4096;

    /// <summary>The furthest a chunk is slid to meet the one before in phase: ten milliseconds at 48 kHz.</summary>
    public const int MaxShift = 480;

    private const int MaxRuns = 64;

    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>Reads the file's own samples, decoding as needed; silence before and after it.</summary>
    public delegate void SourceReader(long start, int frames, float[][] into);

    /// <summary>
    /// The chunk's clip samples: <see cref="ChunkFrames"/> per channel, starting at clip sample
    /// <c>chunk * ChunkFrames</c>, joined in phase to the chunk before when this run rendered it.
    /// </summary>
    public float[][] Render(string key, StretchPlan plan, int channels, int rate, long chunk, SourceReader read)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(read);

        float[][] raw = Raw(plan, channels, rate, chunk, read);
        int shift = 0;
        float[][]? before = null;

        if (_runs.TryGetValue(key, out Run? run) && run.Chunk == chunk - 1)
        {
            before = run.Continuation;
            shift = BestShift(before, raw);
        }

        var samples = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            samples[channel] = new float[ChunkFrames];
            Array.Copy(raw[channel], PreRoll + shift, samples[channel], 0, ChunkFrames);
            if (before is not null)
            {
                for (int index = 0; index < Overlap; index++)
                {
                    float weight = (index + 0.5f) / Overlap;
                    samples[channel][index] = (before[channel][index] * (1 - weight)) + (samples[channel][index] * weight);
                }
            }
        }

        // What this chunk would have played next, for the chunk after to join.
        var continuation = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            continuation[channel] = raw[channel].AsSpan(PreRoll + shift + ChunkFrames, Overlap).ToArray();
        }

        if (_runs.Count >= MaxRuns && !_runs.ContainsKey(key))
        {
            _runs.Clear();
        }

        _runs[key] = new Run(chunk, continuation);
        return samples;
    }

    /// <summary>
    /// How far to slide a chunk's render so its start matches what came before: the offset, within
    /// <see cref="MaxShift"/>, of the largest correlation over the overlap, all channels together.
    /// </summary>
    internal static int BestShift(float[][] before, float[][] raw)
    {
        int best = 0;
        double bestScore = double.NegativeInfinity;

        for (int shift = -MaxShift; shift <= MaxShift; shift++)
        {
            double score = 0;
            int at = PreRoll + shift;
            for (int channel = 0; channel < before.Length; channel++)
            {
                ReadOnlySpan<float> a = before[channel];
                ReadOnlySpan<float> b = raw[channel].AsSpan(at, Overlap);
                for (int index = 0; index < Overlap; index++)
                {
                    score += a[index] * b[index];
                }
            }

            // Ties go to the smaller slide, so silence is not moved at all.
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Math.Abs(shift) < Math.Abs(best)))
            {
                bestScore = score;
                best = shift;
            }
        }

        return best;
    }

    /// <summary>One chunk stretched on its own, from its pre-roll to its post-roll.</summary>
    internal static float[][] Raw(StretchPlan plan, int channels, int rate, long chunk, SourceReader read)
    {
        long first = (chunk * ChunkFrames) - PreRoll;
        int length = PreRoll + ChunkFrames + Overlap + PostRoll;

        long from = plan.SourcePosition(first);
        long to = plan.SourcePosition(first + length);
        bool backwards = to < from;
        long start = Dsp.FloorDiv(Math.Min(from, to), plan.SpeedDen);
        long end = Dsp.FloorDiv(Math.Max(from, to), plan.SpeedDen);
        int inputLength = (int)Math.Max(0, end - start);
        double tempo = (double)inputLength / length;

        var output = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            output[channel] = new float[length];
        }

        if (inputLength < 64 || tempo < StretchPlan.MinTempo || tempo > StretchPlan.MaxTempo)
        {
            Tape(plan, channels, first, length, read, output);
            return output;
        }

        var input = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            input[channel] = new float[inputLength];
        }

        read(start, inputLength, input);
        if (backwards)
        {
            foreach (float[] plane in input)
            {
                Array.Reverse(plane);
            }
        }

        using var stretcher = new AudioTempoFilter(rate, channels, tempo, forClip: true);
        var buffer = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            buffer[channel] = new float[8192];
        }

        int written = 0;
        void Drain()
        {
            int got;
            while ((got = stretcher.Receive(buffer, 0, buffer[0].Length)) > 0)
            {
                int keep = Math.Min(got, length - written);
                for (int channel = 0; keep > 0 && channel < channels; channel++)
                {
                    Array.Copy(buffer[channel], 0, output[channel], written, keep);
                }

                written += Math.Max(0, keep);
            }
        }

        for (int at = 0; at < inputLength; at += 8192)
        {
            stretcher.Send(input, at, Math.Min(8192, inputLength - at));
            Drain();
        }

        stretcher.Finish();
        Drain();

        // The stretcher's own length is right to within a few hundred samples; the chunk is
        // exactly its length, the last of it silence if the stretcher came up short.
        return output;
    }

    /// <summary>A chunk read like tape, for a speed past what the stretcher does.</summary>
    private static void Tape(StretchPlan plan, int channels, long first, int length, SourceReader read, float[][] output)
    {
        long a = plan.SourcePosition(first);
        long b = plan.SourcePosition(first + length - 1);
        long start = Dsp.FloorDiv(Math.Min(a, b), plan.SpeedDen);
        int span = (int)(Dsp.FloorDiv(Math.Max(a, b), plan.SpeedDen) - start + 2);

        var window = new float[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            window[channel] = new float[span];
        }

        read(start, span, window);

        for (int index = 0; index < length; index++)
        {
            long position = plan.SourcePosition(first + index);
            long whole = Dsp.FloorDiv(position, plan.SpeedDen);
            float fraction = (float)(position - (whole * plan.SpeedDen)) / plan.SpeedDen;
            int at = (int)(whole - start);
            if (at < 0 || at + 1 >= span)
            {
                continue;
            }

            for (int channel = 0; channel < channels; channel++)
            {
                float left = window[channel][at];
                output[channel][index] = left + ((window[channel][at + 1] - left) * fraction);
            }
        }
    }

    /// <summary>Where a run of renders has got to: its last chunk, and what that chunk would play next.</summary>
    private sealed record Run(long Chunk, float[][] Continuation);
}
