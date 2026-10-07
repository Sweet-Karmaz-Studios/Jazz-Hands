using System.Globalization;
using JazzHands.Audio;
using JazzHands.Audio.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Audio;
using JazzHands.Media.Encode;

namespace JazzHands.Engine.Export;

/// <summary>
/// The sequence's mix for an export: the same graph playback plays, pulled in order through the
/// plan's stretches and handed to the encoder.
/// </summary>
/// <remarks>
/// The stretches play back to back, so output sample n is somewhere in one of them, and the pull
/// never crosses from one to the next inside a block: a cut is a cut in the sound as well. Reads
/// block on decode, which is what export wants. Thread affine to the thread that writes, because
/// the sample server's decoders belong to whichever thread first decodes.
/// <para>
/// Loudness normalisation is a gain on the whole mix and, where that gain would take a peak past
/// the ceiling, a true peak limiter after it (<see cref="Limit"/>). The limiter is late by its
/// lookahead, so the mix is read that far ahead of what is written, and silence follows the end,
/// which keeps the sound where it was against the picture and lets the last samples out.
/// </para>
/// </remarks>
internal sealed class ExportSound : IDisposable
{
    private const int Chunk = 4096;

    private readonly AudioSampleServer _server;
    private readonly AudioGraph _graph;

    // Plugins render offline for an export (Phase 46), and stop with it.
    private readonly AudioEffectHost _effects = new() { Offline = true };
    private readonly AudioBuffer _buffer;
    private readonly float[][] _planes;
    private readonly (long Start, long Length)[] _stretches;
    private long _written;
    private long _mixed;
    private TruePeakLimiter? _limiter;

    public ExportSound(Project project, Sequence sequence, string projectPath, IReadOnlyList<TimeRange> ranges, int sampleRate, int channels, IReadOnlySet<string>? toMaster = null)
    {
        _server = new AudioSampleServer(new AudioBlockCache(), sampleRate, AudioReadMode.Blocking);
        _server.Update(project, projectPath);
        _graph = new AudioGraph(_server, sampleRate, channels);
        SampleRate = sampleRate;

        // The mix is made at the export's rate and channel count rather than the sequence's and
        // converted after: each source is folded down (ITU, for 5.1 into stereo) or spread on its
        // way into the mix, and the master limiter then sees what is actually written, so the
        // fold cannot push a peak past it.
        ProjectSettings settings = project.SettingsFor(sequence) with { SampleRate = sampleRate, ChannelCount = channels };
        _graph.Publish(AudioGraphBuilder.Build(project, sequence with { Settings = settings }, _effects, toMaster));
        _buffer = new AudioBuffer(channels, Chunk);
        _planes = [.. Enumerable.Range(0, channels).Select(_ => new float[Chunk])];

        _stretches = [.. ranges.Select(range =>
        {
            long start = range.Start.ToTimebase(1, sampleRate, RoundingMode.Nearest);
            long end = range.End.ToTimebase(1, sampleRate, RoundingMode.Nearest);
            return (start, Math.Max(0, end - start));
        })];

        Total = _stretches.Sum(stretch => stretch.Length);
    }

    /// <summary>Samples in the whole export.</summary>
    public long Total { get; }

    /// <summary>Encodes the mix up to an output sample.</summary>
    public void WriteUpTo(long target, AudioEncoder encoder, Muxer muxer, int stream)
    {
        target = Math.Min(target, Total);

        while (_written < target)
        {
            int count = Read((int)Math.Min(Chunk, target - _written));
            encoder.Write(_planes, 0, count, muxer, stream);
        }
    }

    /// <summary>The samples <see cref="Read"/> filled, one array per channel.</summary>
    public float[][] Planes => _planes;

    /// <summary>The ceiling loudness normalisation holds true peaks under, in dBTP.</summary>
    public const float CeilingDb = -1.0f;

    /// <summary>A linear gain on everything <see cref="Read"/> returns: 1 leaves the mix as it is.</summary>
    public float Gain { get; set; } = 1.0f;

    /// <summary>
    /// True to hold the mix's true peaks under <see cref="CeilingDb"/> with a limiter after the
    /// gain. Set before the first read.
    /// </summary>
    public bool Limit
    {
        get => _limiter is not null;
        init => _limiter = value ? new TruePeakLimiter(SampleRate, _planes.Length) { CeilingDb = CeilingDb } : null;
    }

    /// <summary>The deepest the limiter has gone so far, in dB; zero when it has touched nothing.</summary>
    public float DeepestReductionDb { get; private set; }

    private int SampleRate { get; }

    /// <summary>
    /// Plays the whole export through a meter, for loudness normalisation: its integrated loudness
    /// (EBU R128), its true peak in dBTP, and how far a limiter went. With a gain and the limiter,
    /// what is measured is what that export writes, sample for sample.
    /// </summary>
    public static LoudnessReading Measure(Project project, Sequence sequence, string projectPath, IReadOnlyList<TimeRange> ranges, int sampleRate, int channels, CancellationToken cancellationToken, float gain = 1.0f, bool limit = false)
    {
        using var mix = new ExportSound(project, sequence, projectPath, ranges, sampleRate, channels) { Gain = gain, Limit = limit };
        var meter = new Loudness(sampleRate, channels);
        TruePeak[] peaks = [.. Enumerable.Range(0, channels).Select(_ => new TruePeak())];
        float peak = 0.0f;

        int count;
        while ((count = mix.Read(Chunk)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            meter.Process(mix._buffer, 0, count);
            for (int channel = 0; channel < channels; channel++)
            {
                foreach (float sample in mix._buffer.Plane(channel, 0, count))
                {
                    peak = Math.Max(peak, peaks[channel].Push(sample));
                }
            }
        }

        // The detectors are a little late: let the last samples' peaks out.
        for (int channel = 0; channel < channels; channel++)
        {
            for (int index = 0; index < TruePeak.Delay + 1; index++)
            {
                peak = Math.Max(peak, peaks[channel].Push(0.0f));
            }
        }

        return new LoudnessReading(meter.Integrated, peak <= 0.0f ? float.NegativeInfinity : Dsp.GainToDb(peak), mix.DeepestReductionDb);
    }

    /// <summary>
    /// Brings a mix to a loudness target with one gain, and a true peak limiter where the gain
    /// would take a peak past <see cref="CeilingDb"/>; what to apply, and a sentence saying what was done.
    /// </summary>
    /// <remarks>
    /// One gain, as the streaming services apply theirs, so normalising changes how loud the mix
    /// is and not its dynamics, except where a peak would otherwise clip on the way: there the
    /// limiter takes it down, and that also takes some loudness away, so the mix is measured again
    /// through it and the gain found by the secant method, to within a tenth of a decibel in at
    /// most <see cref="MostPasses"/> plays. The limiter is never asked for more than
    /// <see cref="MostLimitingDb"/> over the plain gain: a mix whose peaks stand that far above its
    /// body (clicks, gunshots over silence) stops short, and the note says so, rather than being
    /// crushed. <paramref name="measure"/> plays the mix with a gain and the limiter on or off.
    /// </remarks>
    public static (float Gain, bool Limit) Normalise(double target, Func<float, bool, LoudnessReading> measure, out string note)
    {
        ArgumentNullException.ThrowIfNull(measure);

        LoudnessReading plain = measure(1.0f, false);
        if (float.IsNegativeInfinity(plain.Integrated) || float.IsNegativeInfinity(plain.TruePeakDb))
        {
            note = "The mix is silent, so the loudness was left alone.";
            return (1.0f, false);
        }

        double wanted = target - plain.Integrated;
        if (plain.TruePeakDb + wanted <= CeilingDb)
        {
            note = string.Create(CultureInfo.InvariantCulture, $"The mix measured {plain.Integrated:0.0} LUFS and was changed by {wanted:+0.0;-0.0} dB to {target:0.#} LUFS.");
            return (Linear(wanted), false);
        }

        // Its peaks would pass the ceiling: limited, and the loudness the limiter takes made up.
        double most = wanted + MostLimitingDb;
        double gain = wanted;
        LoudnessReading result = measure(Linear(gain), true);
        double previousGain = double.NaN;
        double previousError = double.NaN;
        for (int pass = 2; pass < MostPasses && Math.Abs(result.Integrated - target) > 0.1; pass++)
        {
            double error = result.Integrated - target;
            if (gain >= most && error < 0)
            {
                break;
            }

            // How much louder a decibel more gain makes it, between the last two tries: one
            // without limiting, less the harder it works. Never less than a twentieth, so a
            // flat stretch cannot send the gain off to nowhere.
            double slope = double.IsNaN(previousGain) || Math.Abs(gain - previousGain) < 1e-6 ? 1.0 : (error - previousError) / (gain - previousGain);
            slope = Math.Clamp(slope, 0.05, 1.0);
            previousGain = gain;
            previousError = error;
            gain = Math.Min(most, gain - (error / slope));
            result = measure(Linear(gain), true);
        }

        bool reached = Math.Abs(result.Integrated - target) <= 0.1;
        note = string.Create(
            CultureInfo.InvariantCulture,
            $"The mix measured {plain.Integrated:0.0} LUFS with peaks at {plain.TruePeakDb:0.0} dBTP. Raised by {gain:+0.0;-0.0} dB to {result.Integrated:0.0} LUFS{(reached ? string.Empty : $", short of {target:0.#}")}, with a true peak limiter holding its peaks under {CeilingDb:0} dBTP; it took up to {-result.DeepestReductionDb:0.0} dB off the loudest moments.")
            + (reached ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" Its peaks stand so far above the rest that going further would crush them; compress the mix first to bring it closer."));
        return (Linear(gain), true);
    }

    /// <summary>The most plays of the mix normalising takes, the first one included.</summary>
    public const int MostPasses = 6;

    /// <summary>How much more than the plain gain the limiter may be asked to make up, in dB.</summary>
    public const double MostLimitingDb = 12.0;

    private static float Linear(double db) => (float)Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// Mixes the next samples into <see cref="Planes"/>, at most <paramref name="wanted"/>; a block
    /// of the sequence is never pulled across a cut.
    /// </summary>
    /// <returns>How many were mixed; zero at the end.</returns>
    public int Read(int wanted)
    {
        int count = (int)Math.Min(Math.Min(wanted, Chunk), Total - _written);
        if (count <= 0)
        {
            return 0;
        }

        if (_limiter is { } limiter)
        {
            // The limiter hands back what went in its latency before: read that far ahead first.
            if (_mixed == 0)
            {
                Mix(limiter.Latency);
                limiter.Process(_buffer, 0, limiter.Latency);
            }

            Mix(count);
            limiter.Process(_buffer, 0, count);
            DeepestReductionDb = Math.Min(DeepestReductionDb, limiter.LastReductionDb);
        }
        else
        {
            Mix(count);
        }

        for (int channel = 0; channel < _planes.Length; channel++)
        {
            _buffer.Plane(channel, 0, count).CopyTo(_planes[channel]);
        }

        _written += count;
        return count;
    }

    /// <summary>The next mixed samples into the buffer, with the gain: from the stretches in turn, silence past the end.</summary>
    private void Mix(int count)
    {
        int filled = 0;
        while (filled < count)
        {
            if (_mixed >= Total)
            {
                for (int channel = 0; channel < _planes.Length; channel++)
                {
                    _buffer.Plane(channel, filled, count - filled).Clear();
                }

                _mixed += count - filled;
                break;
            }

            (long start, int length) = Next(_mixed, count - filled);
            _graph.Pull(start, _buffer, filled, length);
            filled += length;
            _mixed += length;
        }

        if (Gain != 1.0f)
        {
            for (int channel = 0; channel < _planes.Length; channel++)
            {
                foreach (ref float sample in _buffer.Plane(channel, 0, count))
                {
                    sample *= Gain;
                }
            }
        }
    }

    public void Dispose()
    {
        _effects.Retain(new HashSet<string>());
        _server.Dispose();
    }

    /// <summary>Where a mixed sample is on the sequence, and how many follow it in the same stretch.</summary>
    private (long Start, int Count) Next(long mixed, int wanted)
    {
        long offset = mixed;
        foreach ((long start, long length) in _stretches)
        {
            if (offset < length)
            {
                return (start + offset, (int)Math.Min(wanted, length - offset));
            }

            offset -= length;
        }

        throw new InvalidOperationException("Past the end of the export's sound.");
    }
}

/// <summary>How loud a mix measured.</summary>
/// <param name="Integrated">Its integrated loudness in LUFS, negative infinity for silence.</param>
/// <param name="TruePeakDb">Its highest true peak in dBTP, negative infinity for silence.</param>
/// <param name="DeepestReductionDb">How far a limiter went, in dB; zero without one or when it touched nothing.</param>
internal sealed record LoudnessReading(float Integrated, float TruePeakDb, float DeepestReductionDb);
