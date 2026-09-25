using JazzHands.Audio;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Audio;

/// <summary>What <see cref="AudioMeasure"/> is asked to hear: some clips, a track, or the mix.</summary>
/// <param name="Sequence">The sequence they are on.</param>
/// <param name="ClipIds">The clips heard alone, or empty.</param>
/// <param name="TrackId">The track heard alone, or null.</param>
internal sealed record AudioSubject(Sequence Sequence, IReadOnlyList<string> ClipIds, string? TrackId)
{
    /// <summary>True for the whole mix.</summary>
    public bool IsMix => ClipIds.Count == 0 && TrackId is null;

    /// <summary>What it is, for a measurement: the clips' ids, the track's id, or "mix".</summary>
    public string Name => IsMix ? "mix" : TrackId ?? string.Join(",", ClipIds);

    /// <summary>
    /// What a clip, track or mix option names. A video clip stands for the sound linked to it;
    /// exactly one of the three must be given.
    /// </summary>
    /// <exception cref="CommandException">When none or more than one is given, or it has no sound.</exception>
    public static AudioSubject Resolve(Project project, string? clipId, string? trackId, bool mix, string? sequenceId)
    {
        int given = (clipId is null ? 0 : 1) + (trackId is null ? 0 : 1) + (mix ? 1 : 0);
        if (given != 1)
        {
            throw new CommandException("invalid-value", "Give one of --clip, --track or --mix.");
        }

        if (clipId is not null)
        {
            ClipLocation found = project.FindClip(clipId) ?? throw new CommandException("clip-not-found", $"There is no clip '{clipId}'.", "clip");
            string[] sound = found.Track.IsAudio
                ? [clipId]
                : [.. TimelineQueries.LinkedClips(found.Sequence, clipId)
                    .Where(linked => found.Sequence.Tracks.Any(track => track.IsAudio && track.Clip(linked.Id) is not null))
                    .Select(linked => linked.Id)];

            return sound.Length > 0
                ? new AudioSubject(found.Sequence, sound, null)
                : throw new CommandException("no-sound", $"'{found.Clip.Name}' is a picture with no sound linked to it.", "clip");
        }

        if (trackId is not null)
        {
            Sequence sequence = project.Sequences.FirstOrDefault(candidate => candidate.Tracks.Any(track => track.Id == trackId))
                ?? throw new CommandException("track-not-found", $"There is no track '{trackId}'.", "track");
            return sequence.Tracks.First(track => track.Id == trackId).IsAudio
                ? new AudioSubject(sequence, [], trackId)
                : throw new CommandException("no-sound", "That track is not a sound track.", "track");
        }

        Sequence chosen = (sequenceId is null ? project.ActiveSequence : project.Sequence(sequenceId))
            ?? throw new CommandException("sequence-not-found", $"There is no sequence '{sequenceId}'.", "sequence");
        return new AudioSubject(chosen, [], null);
    }
}

/// <summary>A measurement: the highest sample, the RMS and integrated loudness, over a stretch.</summary>
/// <param name="From">Where on the sequence it started.</param>
/// <param name="To">Where it stopped.</param>
/// <param name="Peak">The highest sample, linear.</param>
/// <param name="MeanSquare">The mean of the squared samples over every channel.</param>
/// <param name="Lufs">Integrated loudness, or <see cref="Loudness.Silent"/>.</param>
internal sealed record AudioLevels(Flicks From, Flicks To, float Peak, double MeanSquare, float Lufs)
{
    /// <summary>The level a mode reads, in dBFS or LUFS; null for silence.</summary>
    public double? Level(NormalizeMode mode) => mode switch
    {
        NormalizeMode.Peak => Peak > 0 ? 20.0 * Math.Log10(Peak) : null,
        NormalizeMode.Rms => MeanSquare > 0 ? 10.0 * Math.Log10(MeanSquare) : null,
        _ => float.IsFinite(Lufs) ? Lufs : null,
    };
}

/// <summary>
/// Hears a clip, a track or a whole mix on its own and measures it, for <c>audio.measure</c> and
/// <c>audio.normalize</c>.
/// </summary>
/// <remarks>
/// The sequence is rebuilt with only what is asked for in it: clips alone on their tracks with
/// the tracks' faders, pans and effects taken out; a track alone; or every track. The master is
/// at 0 dB without its limiter, so the reading is what the thing itself sends on. Mixed offline
/// through the playback graph, reading blocking on decode, on the calling thread: a deliberate copy
/// of the mix to system memory, as a meter needs.
/// </remarks>
internal static class AudioMeasure
{
    private const int Chunk = AudioGraph.BlockSize * 8;

    /// <summary>Measures a subject over its span: the clips', the track's or the sequence's.</summary>
    public static AudioLevels Measure(Project project, AudioSubject subject, string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(subject);

        (Sequence alone, Flicks from, Flicks to) = Isolate(subject);
        ProjectSettings settings = project.SettingsFor(subject.Sequence);
        int rate = settings.SampleRate;
        int channels = Math.Clamp(settings.ChannelCount, 1, Dsp.MaxChannels);
        project = project.ReplaceSequence(alone);

        long start = from.ToTimebase(1, rate, RoundingMode.Nearest);
        long end = to.ToTimebase(1, rate, RoundingMode.Nearest);
        if (end <= start)
        {
            return new AudioLevels(from, to, 0, 0, Loudness.Silent);
        }

        using var server = new AudioSampleServer(new AudioBlockCache(), rate, AudioReadMode.Blocking);
        server.Update(project, projectPath);
        var graph = new AudioGraph(server, rate, channels);
        graph.Publish(AudioGraphBuilder.Build(project, alone));

        var loudness = new Loudness(rate, channels);
        var buffer = new AudioBuffer(channels, Chunk);
        float peak = 0;
        double squares = 0;
        for (long at = start; at < end;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(Chunk, end - at);
            graph.Pull(at, buffer, 0, count);
            loudness.Process(buffer, 0, count);
            for (int channel = 0; channel < channels; channel++)
            {
                foreach (float sample in buffer.Plane(channel, 0, count))
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                    squares += (double)sample * sample;
                }
            }

            at += count;
        }

        return new AudioLevels(from, to, peak, squares / ((double)(end - start) * channels), loudness.Integrated);
    }

    /// <summary>The sequence with only the subject in it, and the stretch to measure.</summary>
    private static (Sequence Sequence, Flicks From, Flicks To) Isolate(AudioSubject subject)
    {
        Sequence sequence = subject.Sequence with { Master = new MasterBus(Limiter: false) };

        if (subject.IsMix)
        {
            // The master's volume stays: it is what normalising the mix moves, and part of what it sends on.
            return (sequence with { Master = new MasterBus(subject.Sequence.Master?.Volume, Limiter: false) }, Flicks.Zero, subject.Sequence.Duration);
        }

        if (subject.TrackId is { } trackId)
        {
            Track track = sequence.Tracks.First(candidate => candidate.Id == trackId);
            sequence = sequence with { Tracks = EquatableArray.Create(track with { Muted = false, Solo = false }) };
            return (sequence, track.Clips.IsEmpty ? Flicks.Zero : track.Clips[0].Start, track.Duration);
        }

        var ids = new HashSet<string>(subject.ClipIds, StringComparer.Ordinal);
        Track[] tracks =
        [
            .. sequence.Tracks
                .Where(track => track.IsAudio && track.Clips.Any(clip => ids.Contains(clip.Id)))
                .Select(track => track with
                {
                    Clips = EquatableArray.Create([.. track.Clips.Where(clip => ids.Contains(clip.Id))]),
                    Transitions = EquatableArray<Transition>.Empty,
                    Effects = EquatableArray<Effect>.Empty,
                    Volume = null,
                    Pan = null,
                    Muted = false,
                    Solo = false,
                }),
        ];

        Clip[] clips = [.. tracks.SelectMany(track => track.Clips)];
        return (sequence with { Tracks = EquatableArray.Create(tracks) }, clips.Min(clip => clip.Start), clips.Max(clip => clip.End));
    }
}
