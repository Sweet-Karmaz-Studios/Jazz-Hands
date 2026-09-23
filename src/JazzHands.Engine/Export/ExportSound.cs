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
        _graph.Publish(AudioGraphBuilder.Build(project, sequence));
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
            _buffer.Plane(channel, 0, count).CopyTo(_planes[channel]);
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
