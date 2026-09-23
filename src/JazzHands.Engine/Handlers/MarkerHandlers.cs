using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Puts a marker on a sequence or on a clip.</summary>
public sealed class AddMarkerHandler : ICommandHandler<AddMarkerCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddMarkerCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string id = HandlerHelp.IdOr(command.MarkerId);

        if (MarkerLookup.Find(project, id) is not null)
        {
            throw new CommandException("duplicate-id", $"'{id}' is already a marker in this project.");
        }

        Flicks duration = command.Duration ?? Flicks.Zero;
        if (duration < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A marker cannot have a negative duration.");
        }

        var marker = new Marker(
            id,
            command.At,
            duration,
            command.Name,
            command.Color is { Length: > 0 } ? CommandValues.ParseColor(command.Color) : "#FFCC00",
            command.Note ?? string.Empty,
            command.IsChapter);

        context.Changed(id);

        if (command.ClipId is { Length: > 0 } clipId)
        {
            ClipLocation found = HandlerHelp.Clip(project, clipId);
            HandlerHelp.RequireUnlocked(found.Track);

            if (command.At < Flicks.Zero || command.At > found.Clip.Duration)
            {
                throw new CommandException(
                    "time-out-of-range",
                    "A marker on a clip sits between the clip start and its end, measured from the start.");
            }

            context.Changed(clipId);
            return project.ReplaceTrack(
                found.Track.ReplaceClip(found.Clip with { Markers = found.Clip.Markers.Add(marker) }));
        }

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        if (command.At < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A marker cannot sit before the timeline starts.");
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Markers = sequence.Markers.Add(marker) });
    }
}

/// <summary>Removes a marker, wherever it is.</summary>
public sealed class RemoveMarkerHandler : ICommandHandler<RemoveMarkerCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveMarkerCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MarkerLocation found = MarkerLookup.Require(project, command.MarkerId);

        context.Changed(command.MarkerId);

        if (found.Clip is { } clip)
        {
            context.Changed(clip.Id);
            return project.ReplaceTrack(found.Track!.ReplaceClip(
                clip with { Markers = Without(clip.Markers, command.MarkerId) }));
        }

        context.Changed(found.Sequence.Id);
        return project.ReplaceSequence(
            found.Sequence with { Markers = Without(found.Sequence.Markers, command.MarkerId) });
    }

    private static EquatableArray<Marker> Without(EquatableArray<Marker> markers, string markerId)
    {
        int index = markers.IndexOf(marker => string.Equals(marker.Id, markerId, StringComparison.Ordinal));
        return index < 0 ? markers : markers.RemoveAt(index);
    }
}

/// <summary>Changes a marker.</summary>
public sealed class SetMarkerHandler : ICommandHandler<SetMarkerCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMarkerCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MarkerLocation found = MarkerLookup.Require(project, command.MarkerId);
        Marker marker = found.Marker;

        if (command.Name is { } name)
        {
            marker = marker with { Name = name };
        }

        if (command.At is { } at)
        {
            if (at < Flicks.Zero)
            {
                throw new CommandException("time-out-of-range", "A marker cannot sit before zero.");
            }

            if (found.Clip is { } owner && at > owner.Duration)
            {
                throw new CommandException("time-out-of-range", "A marker on a clip cannot sit past its end.");
            }

            marker = marker with { Time = at };
        }

        if (command.Duration is { } duration)
        {
            if (duration < Flicks.Zero)
            {
                throw new CommandException("time-out-of-range", "A marker cannot have a negative duration.");
            }

            marker = marker with { Duration = duration };
        }

        if (command.Color is { Length: > 0 } color)
        {
            marker = marker with { Color = CommandValues.ParseColor(color) };
        }

        if (command.Note is { } note)
        {
            marker = marker with { Note = note };
        }

        if (command.IsChapter is { } chapter)
        {
            marker = marker with { IsChapter = chapter };
        }

        if (marker == found.Marker)
        {
            return project;
        }

        context.Changed(command.MarkerId);

        if (found.Clip is { } clip)
        {
            context.Changed(clip.Id);
            return project.ReplaceTrack(found.Track!.ReplaceClip(
                clip with { Markers = Replace(clip.Markers, marker) }));
        }

        context.Changed(found.Sequence.Id);
        return project.ReplaceSequence(found.Sequence with { Markers = Replace(found.Sequence.Markers, marker) });
    }

    private static EquatableArray<Marker> Replace(EquatableArray<Marker> markers, Marker marker)
    {
        int index = markers.IndexOf(existing => string.Equals(existing.Id, marker.Id, StringComparison.Ordinal));
        return index < 0 ? markers.Add(marker) : markers.SetItem(index, marker);
    }
}

/// <summary>Where a marker was found.</summary>
/// <param name="Marker">The marker.</param>
/// <param name="Sequence">The sequence it is in.</param>
/// <param name="Track">The track its clip is on, when it is on a clip.</param>
/// <param name="Clip">The clip it is on, or null when it sits on the sequence.</param>
internal sealed record MarkerLocation(Marker Marker, Sequence Sequence, Track? Track, Clip? Clip);

/// <summary>Finds a marker anywhere in the project.</summary>
/// <remarks>
/// Markers live in two places, on sequences and on clips, and a command names one by id alone.
/// Searching both is what lets <c>jazz marker remove</c> take an id and nothing else.
/// </remarks>
internal static class MarkerLookup
{
    internal static MarkerLocation? Find(Project project, string markerId)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Marker marker in sequence.Markers)
            {
                if (string.Equals(marker.Id, markerId, StringComparison.Ordinal))
                {
                    return new MarkerLocation(marker, sequence, null, null);
                }
            }

            foreach (Track track in sequence.Tracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    foreach (Marker marker in clip.Markers)
                    {
                        if (string.Equals(marker.Id, markerId, StringComparison.Ordinal))
                        {
                            return new MarkerLocation(marker, sequence, track, clip);
                        }
                    }
                }
            }
        }

        return null;
    }

    internal static MarkerLocation Require(Project project, string markerId) =>
        Find(project, markerId)
        ?? throw new CommandException("marker-not-found", $"No marker with id '{markerId}'.");
}
