using System.Globalization;
using JazzHands.Audio.Analysis;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Audio;

namespace JazzHands.Engine.Handlers;

/// <summary>Works out how far a clip moves to line its sound up with another clip's.</summary>
internal static class ClipSync
{
    private const int Rate = 48_000;

    /// <summary>The least confidence <c>audio.sync</c> moves a clip on without <c>--force</c>.</summary>
    public const double Sure = 0.3;

    /// <summary>The offset for a clip to line up with another, from their sound where they are.</summary>
    public static SyncOffsetInfo Find(Project project, string clipId, string toClipId, string projectPath)
    {
        if (string.Equals(clipId, toClipId, StringComparison.Ordinal))
        {
            throw new CommandException("invalid-value", "A clip lines up with another clip, not with itself.", "to");
        }

        (Clip moving, Flicks firstStart, float[] first) = Sound(project, clipId, projectPath, "clip");
        (_, Flicks secondStart, float[] second) = Sound(project, toClipId, projectPath, "to");

        SyncMatch match = SyncFinder.Find(first, second, Rate)
            ?? throw new CommandException("silent", "One of the two is silent or too short to line up by its sound.");

        // The first has the sound later by the lag, each counted from where its sound starts on the
        // timeline; the clip moves by the difference, with its linked clips.
        Flicks offset = secondStart - firstStart - Flicks.FromSeconds(match.LagSeconds);
        return new SyncOffsetInfo(clipId, toClipId, offset, moving.Start + offset, match.Confidence, match.Ambiguous);
    }

    /// <summary>
    /// A clip's sound as it plays on the timeline, mono: its own stream on a sound track, or the
    /// sound linked to it for a picture, and where on the timeline that sound starts.
    /// </summary>
    private static (Clip Clip, Flicks Start, float[] Samples) Sound(Project project, string clipId, string projectPath, string option)
    {
        ClipLocation found = project.FindClip(clipId) ?? throw new CommandException("clip-not-found", $"There is no clip '{clipId}'.", option);
        Clip clip = found.Clip;
        Clip sound = found.Track.IsAudio
            ? clip
            : TimelineQueries.LinkedClips(found.Sequence, clipId).FirstOrDefault(linked => found.Sequence.Tracks.Any(track => track.IsAudio && track.Clip(linked.Id) is not null))
                ?? throw new CommandException("no-sound", $"'{clip.Name}' is a picture with no sound linked to it.", option);

        if (sound.Reverse || sound.IsRemapped || sound.IsHold || sound.EffectiveSpeed != Rational.One)
        {
            throw new CommandException("not-plain", $"'{sound.Name}' plays at another speed; sound lines up only when both play forwards at normal speed.", option);
        }

        MediaItem item = MediaServices.Require(project, sound.MediaId ?? throw new CommandException("no-sound", $"'{sound.Name}' has no sound.", option));
        MediaStream stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == sound.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio)
            ?? item.Info?.AudioStreams.FirstOrDefault()
            ?? throw new CommandException("no-sound", $"'{item.Name}' has no sound.", option);
        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.", option);
        }

        return (clip, sound.Start, MonoReader.Read(path, stream.Index, sound.SourceIn, sound.SourceDuration, Rate));
    }
}

/// <summary>Answers how far a clip moves to line up with another.</summary>
public sealed class SyncOffsetHandler : IQueryHandler<SyncOffsetQuery, SyncOffsetInfo>
{
    /// <inheritdoc />
    public SyncOffsetInfo Handle(Project project, SyncOffsetQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        return ClipSync.Find(project, query.ClipId, query.ToClipId, context.Session?.ProjectPath ?? string.Empty);
    }
}

/// <summary>Moves a clip so its sound lines up with another's.</summary>
public sealed class SyncAudioHandler : ICommandHandler<SyncAudioCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SyncAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        SyncOffsetInfo found = ClipSync.Find(project, command.ClipId, command.ToClipId, context.ProjectPath);
        if (!command.Force && (found.Confidence < ClipSync.Sure || found.Ambiguous))
        {
            throw new CommandException(
                "unsure-match",
                found.Ambiguous
                    ? "Another offset matches nearly as well (a steady rhythm does that), so the clip was not moved. Give --force to move it by the best one."
                    : string.Create(CultureInfo.InvariantCulture, $"The sound matched only {found.Confidence:0.00} of the way, so the clip was not moved. Give --force to move it anyway."));
        }

        if (found.Start.IsNegative)
        {
            throw new CommandException("time-out-of-range", $"Lining it up would start it {Timecode.FormatClock(-found.Start)} before the sequence does. Move the other clip later first.");
        }

        return context.Run(project, new MoveClipCommand(command.ClipId, found.Start));
    }
}
