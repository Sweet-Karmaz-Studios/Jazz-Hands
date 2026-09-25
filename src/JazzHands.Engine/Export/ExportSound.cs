using System.Globalization;
using JazzHands.Audio;
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
/// </remarks>
internal sealed class ExportSound : IDisposable
{
    private const int Chunk = 4096;

    private readonly AudioSampleServer _server;
    private readonly AudioGraph _graph;
    private readonly AudioBuffer _buffer;
    private readonly float[][] _planes;
    private readonly (long Start, long Length)[] _stretches;
    private long _written;

    public ExportSound(Project project, Sequence sequence, string projectPath, IReadOnlyList<TimeRange> ranges, int sampleRate, int channels)
    {
        _server = new AudioSampleServer(new AudioBlockCache(), sampleRate, AudioReadMode.Blocking);
        _server.Update(project, projectPath);
        _graph = new AudioGraph(_server, sampleRate, channels);

        // The mix is made at the export's rate and channel count rather than the sequence's and
        // converted after: each source is folded down (ITU, for 5.1 into stereo) or spread on its
        // way into the mix, and the master limiter then sees what is actually written, so the
        // fold cannot push a peak past it.
        ProjectSettings settings = project.SettingsFor(sequence) with { SampleRate = sampleRate, ChannelCount = channels };
        _graph.Publish(AudioGraphBuilder.Build(project, sequence with { Settings = settings }));
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

    /// <summary>A linear gain on everything <see cref="Read"/> returns: 1 leaves the mix as it is.</summary>
    public float Gain { get; set; } = 1.0f;

    /// <summary>
    /// Plays the whole export through a meter, for loudness normalisation: its integrated
    /// loudness (EBU R128) and its highest sample.
    /// </summary>
    public static (float Integrated, float Peak) Measure(Project project, Sequence sequence, string projectPath, IReadOnlyList<TimeRange> ranges, int sampleRate, int channels, CancellationToken cancellationToken)
    {
        using var mix = new ExportSound(project, sequence, projectPath, ranges, sampleRate, channels);
        var meter = new Loudness(sampleRate, channels);
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
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
        }

        return (meter.Integrated, peak);
    }

    /// <summary>
    /// The gain that brings a mix to a loudness target, held so its highest sample stays at or under
    /// -1 dBFS, with a sentence saying what was done.
    /// </summary>
    /// <remarks>
    /// One gain for the whole export, as the streaming services apply theirs: normalising changes
    /// how loud the mix is, never its dynamics. Where the ceiling stops the gain short of the target,
    /// the mix needs a limiter, which is a choice about how it sounds and so is left to the person.
    /// </remarks>
    public static float GainFor(double target, float integrated, float peak, out string note)
    {
        if (float.IsNegativeInfinity(integrated) || peak <= 0.0f)
        {
            note = "The mix is silent, so the loudness was left alone.";
            return 1.0f;
        }

        double wanted = target - integrated;
        double ceiling = -1.0 - (20.0 * Math.Log10(peak));
        double applied = Math.Min(wanted, ceiling);
        note = applied < wanted - 0.05
            ? string.Create(CultureInfo.InvariantCulture, $"The mix measured {integrated:0.0} LUFS. Raised by {applied:+0.0;-0.0} dB, short of {target:0.#} LUFS, to keep its peaks under -1 dBFS; a limiter on the mix would let it go further.")
            : string.Create(CultureInfo.InvariantCulture, $"The mix measured {integrated:0.0} LUFS and was changed by {applied:+0.0;-0.0} dB to {target:0.#} LUFS.");
        return (float)Math.Pow(10.0, applied / 20.0);
    }

    /// <summary>
    /// Mixes the next samples into <see cref="Planes"/>, at most <paramref name="wanted"/> and
    /// never across a cut.
    /// </summary>
    /// <returns>How many were mixed; zero at the end.</returns>
    public int Read(int wanted)
    {
        wanted = (int)Math.Min(Math.Min(wanted, Chunk), Total - _written);
        if (wanted <= 0)
        {
            return 0;
        }

        (long start, int count) = Next(wanted);
        _graph.Pull(start, _buffer, 0, count);

        for (int channel = 0; channel < _planes.Length; channel++)
        {
            Span<float> plane = _buffer.Plane(channel, 0, count);
            if (Gain != 1.0f)
            {
                foreach (ref float sample in plane)
                {
                    sample *= Gain;
                }
            }

            plane.CopyTo(_planes[channel]);
        }

        _written += count;
        return count;
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Where the next output sample is on the sequence, and how many follow it in the same stretch.</summary>
    private (long Start, int Count) Next(int wanted)
    {
        long offset = _written;
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
