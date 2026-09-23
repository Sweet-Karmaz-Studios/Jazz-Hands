using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Adds a track to a sequence.</summary>
public sealed class AddTrackHandler : ICommandHandler<AddTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        string id = HandlerHelp.IdOr(command.TrackId);
        HandlerHelp.RequireUnused(project, id);

        var track = new Track(
            id,
            command.Kind,
            command.Name is { Length: > 0 } ? command.Name : HandlerHelp.TrackName(sequence, command.Kind),
            command.Order ?? sequence.NextTrackOrder());

        Sequence updated = sequence.AddTrack(track);

        // An explicit order can collide with a track that is already there, so the stack is
        // renumbered rather than left with two tracks claiming the same place.
        updated = TrackOrder.Renumber(updated, id, command.Order, context);

        context.Changed(id);
        context.Changed(sequence.Id);
        return project.ReplaceSequence(updated);
    }
}

/// <summary>Removes a track and everything on it.</summary>
public sealed class RemoveTrackHandler : ICommandHandler<RemoveTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        HandlerHelp.RequireUnlocked(track);

        context.Changed(track.Id);
        context.Changed(sequence.Id);
        context.Changed(track.Clips.Select(clip => clip.Id));

        Sequence updated = sequence.RemoveTrack(track.Id);
        return project.ReplaceSequence(TrackOrder.Close(updated));
    }
}

/// <summary>Changes a track's display name.</summary>
public sealed class RenameTrackHandler : ICommandHandler<RenameTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RenameTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new CommandException("invalid-name", "A track needs a name.");
        }

        if (string.Equals(track.Name, command.Name, StringComparison.Ordinal))
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Name = command.Name });
    }
}

/// <summary>Moves a track up or down the stack.</summary>
public sealed class MoveTrackHandler : ICommandHandler<MoveTrackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MoveTrackCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        HandlerHelp.RequireUnlocked(track);

        if (command.ToOrder < 0 || command.ToOrder >= sequence.Tracks.Length)
        {
            throw new CommandException(
                "order-out-of-range",
                $"'{sequence.Name}' has {sequence.Tracks.Length} tracks, so the order is 0 to {sequence.Tracks.Length - 1}.");
        }

        Sequence updated = TrackOrder.Renumber(sequence, track.Id, command.ToOrder, context);

        return updated == sequence ? project : project.ReplaceSequence(updated);
    }
}

/// <summary>Mutes or unmutes a track.</summary>
public sealed class SetTrackMuteHandler : ICommandHandler<SetTrackMuteCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackMuteCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);

        if (track.Muted == command.Muted)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Muted = command.Muted });
    }
}

/// <summary>Solos or unsolos a track.</summary>
public sealed class SetTrackSoloHandler : ICommandHandler<SetTrackSoloCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackSoloCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);

        if (track.Solo == command.Solo)
        {
            return project;
        }

        // Soloing changes what every other track on the sequence does, so they all changed as
        // far as anything redrawing is concerned.
        context.Changed(sequence.Tracks.Select(other => other.Id));
        return project.ReplaceTrack(track with { Solo = command.Solo });
    }
}

/// <summary>Locks or unlocks a track.</summary>
public sealed class SetTrackLockHandler : ICommandHandler<SetTrackLockCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackLockCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);

        if (track.Locked == command.Locked)
        {
            return project;
        }

        // Deliberately not guarded by RequireUnlocked: unlocking a locked track is the one edit
        // a locked track has to accept.
        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Locked = command.Locked });
    }
}

/// <summary>Sets how tall a track is drawn.</summary>
public sealed class SetTrackHeightHandler : ICommandHandler<SetTrackHeightCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackHeightCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);

        if (command.Height is < 16 or > 512 || double.IsNaN(command.Height))
        {
            throw new CommandException("invalid-height", "A track is between 16 and 512 pixels tall.");
        }

        if (track.Height.Equals(command.Height))
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Height = command.Height });
    }
}

/// <summary>Sets a track's colour on the timeline.</summary>
public sealed class SetTrackColorHandler : ICommandHandler<SetTrackColorCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackColorCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);
        string color = CommandValues.ParseColor(command.Color);

        if (string.Equals(track.Color, color, StringComparison.Ordinal))
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Color = color });
    }
}

/// <summary>
/// Keeps track orders contiguous and unique.
/// </summary>
/// <remarks>
/// The model lets two tracks claim the same order, because a hand-edited file can say anything,
/// and every reader sorts by it. Leaving a duplicate would make the stacking order depend on
/// which track happened to be first in the array, so the stack is renumbered whenever it changes.
/// </remarks>
internal static class TrackOrder
{
    /// <summary>Puts one track at a position and renumbers the rest around it.</summary>
    internal static Sequence Renumber(Sequence sequence, string trackId, int? toOrder, HandlerContext context)
    {
        List<Track> ordered = [.. sequence.Tracks.OrderBy(track => track.Order)];

        int from = ordered.FindIndex(track => string.Equals(track.Id, trackId, StringComparison.Ordinal));
        if (from < 0)
        {
            return sequence;
        }

        if (toOrder is { } target)
        {
            int to = Math.Clamp(target, 0, ordered.Count - 1);
            Track moving = ordered[from];
            ordered.RemoveAt(from);
            ordered.Insert(to, moving);
        }

        return Apply(sequence, ordered, context);
    }

    /// <summary>Closes the gap left by a removed track.</summary>
    internal static Sequence Close(Sequence sequence) =>
        Apply(sequence, [.. sequence.Tracks.OrderBy(track => track.Order)], context: null);

    private static Sequence Apply(Sequence sequence, List<Track> ordered, HandlerContext? context)
    {
        var tracks = ImmutableArray.CreateBuilder<Track>(ordered.Count);
        bool changed = false;

        for (int index = 0; index < ordered.Count; index++)
        {
            Track track = ordered[index];

            if (track.Order != index)
            {
                changed = true;
                context?.Changed(track.Id);
                track = track with { Order = index };
            }

            tracks.Add(track);
        }

        return changed || !SameOrder(sequence.Tracks, tracks)
            ? sequence with { Tracks = new EquatableArray<Track>(tracks.ToImmutable()) }
            : sequence;
    }

    private static bool SameOrder(EquatableArray<Track> original, ImmutableArray<Track>.Builder updated)
    {
        if (original.Length != updated.Count)
        {
            return false;
        }

        for (int index = 0; index < updated.Count; index++)
        {
            if (!string.Equals(original[index].Id, updated[index].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
