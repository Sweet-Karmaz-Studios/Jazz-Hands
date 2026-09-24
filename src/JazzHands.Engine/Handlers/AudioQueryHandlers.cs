using JazzHands.Audio;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Audio;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// Meters a stretch of a sequence's mix: mixed offline through the graph playback uses, read
/// from the master's meter and every track's, as the Mixer panel reads them while playing.
/// </summary>
/// <remarks>
/// Reads block on decode, as an export's do, on the thread that asks. A deliberate copy of the
/// mix to system memory: a query has no audio device, and the meter is the point.
/// </remarks>
public sealed class MeterAudioHandler : IQueryHandler<MeterAudioQuery, AudioMeterSummary>
{
    private const int Chunk = AudioGraph.BlockSize * 8;
    private const int Channels = 2;

    /// <inheritdoc />
    public AudioMeterSummary Handle(Project project, MeterAudioQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = StillHelp.Sequence(project, query.SequenceId);
        Flicks from = query.From ?? Flicks.Zero;
        Flicks to = query.To ?? sequence.Duration;

        if (from.IsNegative)
        {
            throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(from)} is before the sequence starts.");
        }

        if (to <= from)
        {
            throw new CommandException(
                "time-out-of-range",
                query.To is null
                    ? $"'{sequence.Name}' ends at {Timecode.FormatClock(to)}, so there is nothing after {Timecode.FormatClock(from)} to meter. Give --to."
                    : $"--to ({Timecode.FormatClock(to)}) has to be after --from ({Timecode.FormatClock(from)}).");
        }

        int rate = project.SettingsFor(sequence).SampleRate;
        long start = from.ToTimebase(1, rate, RoundingMode.Nearest);
        long end = to.ToTimebase(1, rate, RoundingMode.Nearest);

        using var server = new AudioSampleServer(new AudioBlockCache(), rate, AudioReadMode.Blocking);
        server.Update(project, context.Session?.ProjectPath ?? string.Empty);
        var graph = new AudioGraph(server, rate, Channels);
        MixSnapshot snapshot = AudioGraphBuilder.Build(project, sequence);
        graph.Publish(snapshot);

        TrackMix[] tracks = [.. snapshot.Tracks];
        MeterRing?[] rings = [.. tracks.Select(track => graph.TrackMeter(track.Id))];
        var trackLevels = new Levels[tracks.Length];
        var master = new Levels();
        float truePeak = 0.0f;
        float reduction = 0.0f;
        float maxShortTerm = Loudness.Silent;
        MeterReading last = default;
        last.Momentary = last.ShortTerm = last.Integrated = Loudness.Silent;

        var buffer = new AudioBuffer(Channels, Chunk);
        for (long at = start; at < end;)
        {
            int count = (int)Math.Min(Chunk, end - at);
            graph.Pull(at, buffer, 0, count);
            at += count;

            while (graph.Meter.Readings.TryRead(out MeterReading reading))
            {
                master.Add(reading);
                truePeak = Math.Max(truePeak, reading.TruePeak);
                reduction = Math.Min(reduction, reading.ReductionDb);
                maxShortTerm = Math.Max(maxShortTerm, reading.ShortTerm);
                last = reading;
            }

            for (int index = 0; index < rings.Length; index++)
            {
                while (rings[index]?.TryRead(out MeterReading reading) == true)
                {
                    trackLevels[index].Add(reading);
                }
            }
        }

        return new AudioMeterSummary(
            from,
            to,
            Db(master.Peak),
            Db(truePeak),
            Db(master.Rms),
            Lufs(last.Momentary),
            Lufs(last.ShortTerm),
            Lufs(maxShortTerm),
            Lufs(last.Integrated),
            Math.Round(reduction, 2),
            master.Clipped,
            [.. tracks.Select((track, index) => new TrackMeterSummary(track.Id, track.Name, Db(trackLevels[index].Peak), Db(trackLevels[index].Rms), trackLevels[index].Clipped))]);
    }

    private static double? Db(float linear) => linear <= 0.0f ? null : Math.Round(20.0 * Math.Log10(linear), 2);

    private static double? Lufs(float lufs) => float.IsFinite(lufs) ? Math.Round(lufs, 2) : null;

    /// <summary>Peak and RMS over many readings.</summary>
    private struct Levels
    {
        private double _squares;
        private long _samples;

        public float Peak { get; private set; }

        public bool Clipped { get; private set; }

        public readonly float Rms => _samples == 0 ? 0.0f : (float)Math.Sqrt(_squares / _samples);

        public void Add(in MeterReading reading)
        {
            for (int channel = 0; channel < reading.Channels; channel++)
            {
                Peak = Math.Max(Peak, reading.Peak[channel]);
                _squares += (double)reading.Rms[channel] * reading.Rms[channel] * reading.Frames;
                _samples += reading.Frames;
            }

            Clipped |= reading.Clipped;
        }
    }
}
