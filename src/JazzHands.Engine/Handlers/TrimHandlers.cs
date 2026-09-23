using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Lookups and the shared relayout for the Quick Trim commands.</summary>
internal static class TrimHelp
{
    /// <summary>The Quick Trim sequence and its file, or a coded refusal.</summary>
    internal static (Sequence Sequence, MediaItem Media) Require(Project project, string? sequenceId)
    {
        Sequence sequence = HandlerHelp.Sequence(project, sequenceId);

        if (sequence.QuickTrim is not { } trim)
        {
            throw new CommandException(
                "not-a-quick-trim",
                $"'{sequence.Name}' is not a Quick Trim. Start one with 'jazz trim start <media>'.");
        }

        MediaItem media = project.MediaItem(trim.MediaId)
            ?? throw new CommandException(
                "missing-media-reference",
                $"The file '{sequence.Name}' trims (media '{trim.MediaId}') is no longer in the project.");

        return (sequence, media);
    }

    /// <summary>The frame rate the sequence cuts on.</summary>
    internal static Rational Rate(Project project, Sequence sequence) => project.SettingsFor(sequence).FrameRate;

    /// <summary>Lays the sequence out with these stretches, reporting what changed.</summary>
    internal static Project Apply(Project project, Sequence sequence, MediaItem media, ImmutableArray<TimeRange> segments, HandlerContext context)
    {
        if (segments.SequenceEqual(QuickTrimOps.Segments(sequence)))
        {
            return project;
        }

        foreach (Track track in sequence.Tracks)
        {
            HandlerHelp.RequireUnlocked(track);
        }

        Sequence laid = QuickTrimOps.Layout(sequence, media, segments, Id.New);

        context.Changed(sequence.Id);
        foreach (Track before in sequence.Tracks)
        {
            Track after = laid.Track(before.Id)!;
            if (before.Clips == after.Clips)
            {
                continue;
            }

            context.Changed(before.Id);
            var kept = after.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
            var had = before.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
            context.Changed(had.Where(id => !kept.Contains(id)));
            context.Changed(kept.Where(id => !had.Contains(id)));
        }

        return project.ReplaceSequence(laid);
    }

    /// <summary>A range from an in and an out, or a coded refusal when it is empty or backwards.</summary>
    internal static TimeRange Range(Flicks start, Flicks end) => end > start
        ? TimeRange.FromBounds(start, end)
        : throw new CommandException("empty-range", "The out point has to come after the in point.");
}

/// <summary>Makes a Quick Trim sequence for a file and shows it.</summary>
public sealed class StartTrimHandler : ICommandHandler<StartTrimCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, StartTrimCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem media = project.MediaItem(command.MediaId)
            ?? throw new CommandException("missing-media-reference", $"No media with id '{command.MediaId}' in this project.");

        if (QuickTrimOps.VideoStream(media) is null)
        {
            throw new CommandException(
                "trim-needs-video",
                $"'{media.Name}' has no picture. Quick Trim cuts movies; put a sound file on a timeline instead.");
        }

        string id = HandlerHelp.IdOr(command.SequenceId);
        HandlerHelp.RequireUnused(project, id);

        string name = command.Name is { Length: > 0 } given ? given : media.Name;
        Sequence sequence = QuickTrimOps.Create(id, name, media, project.Settings, Id.New);

        context.Changed(id);
        context.Changed(project.Id);
        foreach (Track track in sequence.Tracks)
        {
            context.Changed(track.Id);
            context.Changed(track.Clips.Select(clip => clip.Id));
        }

        return project.AddSequence(sequence) with { ActiveSequenceId = id };
    }
}

/// <summary>Keeps exactly the stretches given.</summary>
public sealed class SetTrimSegmentsHandler : ICommandHandler<SetTrimSegmentsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrimSegmentsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, MediaItem media) = TrimHelp.Require(project, command.SequenceId);
        ImmutableArray<TimeRange> segments = QuickTrimOps.Normalize(command.Keep, QuickTrimOps.Length(media), TrimHelp.Rate(project, sequence));

        if (segments.IsEmpty && !command.Keep.IsEmpty)
        {
            throw new CommandException(
                "outside-media",
                $"None of those stretches is inside '{media.Name}', which runs {Timecode.FormatClock(QuickTrimOps.Length(media))}.");
        }

        return TrimHelp.Apply(project, sequence, media, segments, context);
    }
}

/// <summary>Keeps one more stretch.</summary>
public sealed class AddTrimSegmentHandler : ICommandHandler<AddTrimSegmentCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddTrimSegmentCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, MediaItem media) = TrimHelp.Require(project, command.SequenceId);
        TimeRange range = TrimHelp.Range(command.In, command.Out);

        if (range.Start >= QuickTrimOps.Length(media))
        {
            throw new CommandException(
                "outside-media",
                $"'{media.Name}' runs {Timecode.FormatClock(QuickTrimOps.Length(media))}; there is nothing at {Timecode.FormatClock(range.Start)} to keep.");
        }

        ImmutableArray<TimeRange> segments = QuickTrimOps.Keep(
            QuickTrimOps.Segments(sequence), range, QuickTrimOps.Length(media), TrimHelp.Rate(project, sequence));

        return TrimHelp.Apply(project, sequence, media, segments, context);
    }
}

/// <summary>Cuts a range out of the kept stretches.</summary>
public sealed class RemoveTrimRangeHandler : ICommandHandler<RemoveTrimRangeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveTrimRangeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, MediaItem media) = TrimHelp.Require(project, command.SequenceId);
        ImmutableArray<TimeRange> segments = QuickTrimOps.Cut(
            QuickTrimOps.Segments(sequence), TrimHelp.Range(command.In, command.Out), QuickTrimOps.Length(media), TrimHelp.Rate(project, sequence));

        return TrimHelp.Apply(project, sequence, media, segments, context);
    }
}
