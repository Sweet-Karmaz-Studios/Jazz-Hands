using System.Globalization;
using JazzHands.Audio.Analysis;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Audio;

namespace JazzHands.Engine.Handlers;

/// <summary>What the beat handlers share: reading a clip's sound and finding its beats on the sequence.</summary>
internal static class BeatHelp
{
    /// <summary>The rate sound is read at for analysis.</summary>
    internal const int Rate = 48000;

    /// <summary>A downbeat marker's colour.</summary>
    internal const string DownbeatColor = "#E8704F";

    /// <summary>Any other beat marker's colour.</summary>
    internal const string BeatColor = "#4FD1C5";

    /// <summary>The beats of a music clip, on the sequence, with what was found.</summary>
    internal static (ClipLocation Clip, BeatAnalysis Analysis, BeatInfo[] Beats) Analyze(Project project, string clipId, string projectPath)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        Clip clip = found.Clip;
        if (clip.Reverse || clip.IsRemapped || clip.IsHold)
        {
            throw new CommandException("not-plain", "Beats are found in a clip playing forwards at one speed: not reversed, remapped or frozen.");
        }

        if (clip.MediaId is not { } mediaId)
        {
            throw new CommandException("no-sound", "Only a clip of a file with sound has beats to find.");
        }

        MediaItem item = MediaServices.Require(project, mediaId);
        MediaStream stream = (found.Track.Kind == TrackKind.Audio
            ? item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio)
            : item.Info?.AudioStreams.FirstOrDefault())
            ?? throw new CommandException("no-sound", $"'{item.Name}' has no sound to find beats in.");

        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.");
        }

        float[] samples = MonoReader.Read(path, stream.Index, clip.SourceIn, clip.SourceDuration, Rate);
        BeatAnalysis analysis = BeatDetector.Detect(samples, Rate);

        // Source seconds from the clip's in point, on the sequence at the clip's speed.
        double speed = clip.EffectiveSpeed.ToDouble();
        BeatInfo[] beats =
        [
            .. analysis.Beats
                .Select(beat => new BeatInfo(clip.Start + Flicks.FromSeconds(beat.Seconds / speed), beat.Bar, beat.BeatInBar, beat.Downbeat))
                .Where(beat => beat.At < clip.End),
        ];

        return (found, analysis, beats);
    }
}

/// <summary>Finds a clip's beats and marks them.</summary>
public sealed class DetectBeatsHandler : ICommandHandler<DetectBeatsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DetectBeatsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, BeatAnalysis analysis, BeatInfo[] beats) = BeatHelp.Analyze(project, command.ClipId, context.ProjectPath);
        if (beats.Length == 0)
        {
            throw new CommandException("no-beats", "No beats were found: the clip is silent, too short, or has no steady attacks.");
        }

        Clip clip = found.Clip;
        Sequence sequence = found.Sequence;
        IEnumerable<Marker> kept = command.Keep
            ? sequence.Markers
            : sequence.Markers.Where(marker => marker.Kind != MarkerKind.Beat || marker.Time < clip.Start || marker.Time >= clip.End);

        string note = string.Create(CultureInfo.InvariantCulture, $"{analysis.Bpm:0.0} BPM, confidence {analysis.Confidence:0.00}");
        IEnumerable<Marker> added = beats.Select((beat, index) => new Marker(
            Id.New(),
            beat.At,
            Flicks.Zero,
            string.Create(CultureInfo.InvariantCulture, $"{beat.Bar}.{beat.Beat}"),
            beat.Downbeat ? BeatHelp.DownbeatColor : BeatHelp.BeatColor,
            index == 0 ? note : string.Empty,
            Kind: MarkerKind.Beat,
            Downbeat: beat.Downbeat));

        context.Changed(sequence.Id);
        context.Changed(clip.Id);
        return project.ReplaceSequence(sequence with { Markers = [.. kept.Concat(added).OrderBy(marker => marker.Time)] });
    }
}

/// <summary>What beat detection finds in a clip.</summary>
public sealed class AnalyzeBeatsHandler : IQueryHandler<AnalyzeBeatsQuery, BeatAnalysisInfo>
{
    /// <inheritdoc />
    public BeatAnalysisInfo Handle(Project project, AnalyzeBeatsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        (_, BeatAnalysis analysis, BeatInfo[] beats) = BeatHelp.Analyze(project, query.ClipId, context.Session?.ProjectPath ?? string.Empty);
        return new BeatAnalysisInfo(query.ClipId, Math.Round(analysis.Bpm, 2), Math.Round(analysis.Confidence, 3), beats);
    }
}

/// <summary>Lays clips along the beat markers.</summary>
public sealed class CutToBeatsHandler : ICommandHandler<CutToBeatsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CutToBeatsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        if (track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-picture", $"'{track.Name}' is not a video track.", "track");
        }

        if (command.Every < 1)
        {
            throw new CommandException("invalid-value", "Cut on every one beat or more.", "every");
        }

        Flicks from = command.From ?? Flicks.Zero;
        Flicks to = command.To ?? Flicks.MaxValue;
        Flicks[] cuts =
        [
            .. sequence.Markers
                .Where(marker => marker.Kind == MarkerKind.Beat && (!command.Downbeats || marker.Downbeat) && marker.Time >= from && marker.Time <= to)
                .Select(marker => marker.Time)
                .Distinct()
                .Order()
                .Where((_, index) => index % command.Every == 0),
        ];
        if (cuts.Length < 2)
        {
            throw new CommandException("no-beats", "There are fewer than two beat markers to cut between there. Mark the music's beats with audio.beats first.");
        }

        List<(string MediaId, Flicks SourceIn, int? Stream, string Name)> sources = Sources(project, command);
        if (sources.Count == 0)
        {
            throw new CommandException("nothing-chosen", "Name the clips to lay with --clips, or a media folder with --bin.");
        }

        var overwrite = new OverwriteClipHandler();
        int count = Math.Min(sources.Count, cuts.Length - 1);
        for (int index = 0; index < count; index++)
        {
            (string mediaId, Flicks sourceIn, int? stream, string name) = sources[index];
            MediaItem item = MediaServices.Require(project, mediaId);
            Flicks length = cuts[index + 1] - cuts[index];
            if (item.Kind != MediaKind.Still)
            {
                length = Flicks.Min(length, item.Duration - sourceIn);
            }

            if (length <= Flicks.Zero)
            {
                continue;
            }

            project = overwrite.Handle(
                project,
                new OverwriteClipCommand(track.Id, cuts[index], MediaId: mediaId, SourceIn: sourceIn, Duration: length, Name: name, SourceStreamIndex: stream, WithAudio: command.WithAudio),
                context);
        }

        return project;
    }

    /// <summary>What to lay, in order: the named clips' media from where each starts, or a bin's media from the start.</summary>
    private static List<(string MediaId, Flicks SourceIn, int? Stream, string Name)> Sources(Project project, CutToBeatsCommand command)
    {
        var sources = new List<(string, Flicks, int?, string)>();
        if (!command.Clips.IsEmpty)
        {
            foreach (string id in command.Clips)
            {
                Clip clip = HandlerHelp.Clip(project, id).Clip;
                if (clip.MediaId is not { } mediaId)
                {
                    throw new CommandException("not-media", $"'{clip.Name}' is not a clip of a file, so it has nothing to lay.", "clips");
                }

                sources.Add((mediaId, clip.SourceIn, clip.SourceStreamIndex, clip.Name));
            }

            return sources;
        }

        if (command.Bin is { Length: > 0 } bin)
        {
            foreach (MediaItem item in project.Media.Where(item => string.Equals(item.Folder, bin, StringComparison.OrdinalIgnoreCase) && (item.Kind != MediaKind.Movie || item.Info?.VideoStreams.Any() == true)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add((item.Id, Flicks.Zero, null, item.Name));
            }
        }

        return sources;
    }
}
