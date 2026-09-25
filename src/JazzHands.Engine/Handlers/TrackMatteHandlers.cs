using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the track matte handlers share: checking the matte asked for.</summary>
internal static class TrackMatteHelp
{
    /// <summary>The matte a command asks for, checked against the sequence; null to take it away.</summary>
    internal static TrackMatte? Setting(Sequence sequence, Track owner, string? source, TrackMatteMode mode, bool off)
    {
        if (off)
        {
            return null;
        }

        if (!Enum.IsDefined(mode))
        {
            throw new CommandException("invalid-value", $"{(int)mode} is not a matte mode.", "mode");
        }

        if (source is not { Length: > 0 })
        {
            throw new CommandException("invalid-value", "Name the track whose picture is the matte with --source, or pass --off.", "source");
        }

        if (sequence.Track(source) is not { } matte)
        {
            throw new CommandException("track-not-found", $"No track with id '{source}' in this sequence: a matte comes from the same sequence.", "source");
        }

        if (matte.Kind != TrackKind.Video)
        {
            throw new CommandException("not-picture", $"'{matte.Name}' is not a video track, so it has no picture to be a matte.", "source");
        }

        if (matte.Id == owner.Id)
        {
            throw new CommandException("matte-self", "A track cannot be its own clips' matte.", "source");
        }

        return new TrackMatte(matte.Id, mode);
    }
}

/// <summary>Sets or takes away a clip's track matte.</summary>
public sealed class SetClipMatteHandler : ICommandHandler<SetClipMatteCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipMatteCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        if (found.Track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-picture", "Only a clip on a video track has a picture to cut.");
        }

        TrackMatte? matte = TrackMatteHelp.Setting(found.Sequence, found.Track, command.Source, command.Mode, command.Off);
        if (matte == found.Clip.Matte)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Matte = matte }));
    }
}

/// <summary>Sets or takes away a track's track matte.</summary>
public sealed class SetTrackMatteHandler : ICommandHandler<SetTrackMatteCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackMatteCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        if (track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-picture", $"'{track.Name}' is not a video track, so it has no picture to cut.");
        }

        TrackMatte? matte = TrackMatteHelp.Setting(sequence, track, command.Source, command.Mode, command.Off);
        if (matte == track.Matte)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Matte = matte });
    }
}
