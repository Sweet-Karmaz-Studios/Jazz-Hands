using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>The lookups and limits every audio command shares.</summary>
internal static class AudioHelp
{
    /// <summary>The loudest gain a command will set. Anything louder is a typo, not a mix.</summary>
    internal const double MaxGainDb = 24.0;

    /// <summary>Quieter than this is silence, and is stored as this.</summary>
    internal const double MinGainDb = -144.0;

    /// <summary>A clip that has to be on an unlocked audio track.</summary>
    internal static ClipLocation AudioClip(Project project, string clipId)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);

        if (found.Track.Kind != TrackKind.Audio)
        {
            throw new CommandException(
                "not-audio",
                $"Clip '{clipId}' is on {found.Track.Kind.ToString().ToLowerInvariant()} track '{found.Track.Name}'. "
                + "Audio commands work on the clips on audio tracks; audio.mute-stream finds a picture's sound for you.");
        }

        HandlerHelp.RequireUnlocked(found.Track);
        return found;
    }

    /// <summary>A track that has to be an audio track.</summary>
    internal static Track AudioTrack(Project project, string trackId)
    {
        (_, Track track) = HandlerHelp.Track(project, trackId);

        if (track.Kind != TrackKind.Audio)
        {
            throw new CommandException("not-audio", $"Track '{track.Name}' carries {track.Kind.ToString().ToLowerInvariant()}, not sound.");
        }

        return track;
    }

    /// <summary>Checks a gain is one a person could mean, and returns it as stored.</summary>
    internal static float Gain(double db)
    {
        if (double.IsNaN(db) || db > MaxGainDb)
        {
            throw new CommandException(
                "value-out-of-range",
                $"{db} dB is not a gain this will set. Use -144 (silence) to {MaxGainDb}.");
        }

        return (float)Math.Max(db, MinGainDb);
    }

    /// <summary>Checks a pan is between hard left and hard right.</summary>
    internal static float Pan(double pan)
    {
        if (double.IsNaN(pan) || pan < -1.0 || pan > 1.0)
        {
            throw new CommandException("value-out-of-range", $"A pan of {pan} is off the side. Use -1 (hard left) to 1 (hard right).");
        }

        return (float)pan;
    }

    /// <summary>A fade that fits the clip, or a refusal saying how long the clip is.</summary>
    internal static Fade Fade(Clip clip, Flicks duration, Interp curve)
    {
        if (duration < Flicks.Zero || duration > clip.Duration)
        {
            throw new CommandException(
                "time-out-of-range",
                $"A fade has to fit inside the clip, which runs {clip.Duration.ToSeconds():0.###} s.");
        }

        return duration.IsZero ? Core.Model.Fade.None : new Fade(duration, curve);
    }

    /// <summary>Every clip linked to this one in its sequence, this one included, with its track.</summary>
    internal static List<(Track Track, Clip Clip)> Linked(Sequence sequence, Clip clip)
    {
        var linked = new List<(Track, Clip)>();

        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip candidate in track.Clips)
            {
                bool same = string.Equals(candidate.Id, clip.Id, StringComparison.Ordinal);
                bool inGroup = clip.LinkGroupId is { } group && string.Equals(candidate.LinkGroupId, group, StringComparison.Ordinal);

                if (same || inGroup)
                {
                    linked.Add((track, candidate));
                }
            }
        }

        return linked;
    }

    /// <summary>Replaces one clip, reporting it and its track.</summary>
    internal static Project Replace(Project project, ClipLocation found, Clip updated, HandlerContext context)
    {
        if (updated == found.Clip)
        {
            return project;
        }

        context.Changed(updated.Id);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(updated));
    }
}

/// <summary>Sets an audio clip's gain.</summary>
public sealed class SetAudioGainHandler : ICommandHandler<SetAudioGainCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetAudioGainCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        float db = AudioHelp.Gain(command.Db);

        // Zero is the default, so it is stored as no value at all rather than as a zero that a
        // hand editor would have to read past.
        AnimatedValue? volume = db == 0.0f ? null : AnimatedValue.Constant(db);
        return AudioHelp.Replace(project, found, found.Clip with { Volume = volume }, context);
    }
}

/// <summary>Sets an audio clip's pan.</summary>
public sealed class SetAudioPanHandler : ICommandHandler<SetAudioPanCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetAudioPanCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        float pan = AudioHelp.Pan(command.Pan);

        AnimatedValue? value = pan == 0.0f ? null : AnimatedValue.Constant(pan);
        return AudioHelp.Replace(project, found, found.Clip with { Pan = value }, context);
    }
}

/// <summary>Sets an audio clip's fade in.</summary>
public sealed class SetAudioFadeInHandler : ICommandHandler<SetAudioFadeInCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetAudioFadeInCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        Fade fade = AudioHelp.Fade(found.Clip, command.Duration, command.Curve);

        return AudioHelp.Replace(project, found, found.Clip with { FadeIn = fade.IsNone ? null : fade }, context);
    }
}

/// <summary>Sets an audio clip's fade out.</summary>
public sealed class SetAudioFadeOutHandler : ICommandHandler<SetAudioFadeOutCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetAudioFadeOutCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        Fade fade = AudioHelp.Fade(found.Clip, command.Duration, command.Curve);

        return AudioHelp.Replace(project, found, found.Clip with { FadeOut = fade.IsNone ? null : fade }, context);
    }
}

/// <summary>Mutes one audio stream of a clip by switching its linked audio clip off.</summary>
public sealed class MuteAudioStreamHandler : ICommandHandler<MuteAudioStreamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MuteAudioStreamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation given = HandlerHelp.Clip(project, command.ClipId);
        List<(Track Track, Clip Clip)> audio = AudioHelp.Linked(given.Sequence, given.Clip)
            .Where(entry => entry.Track.Kind == TrackKind.Audio)
            .ToList();

        (Track Track, Clip Clip)[] matches = command.Stream is { } stream
            ? [.. audio.Where(entry => entry.Clip.SourceStreamIndex == stream)]
            : given.Track.Kind == TrackKind.Audio
                ? [(given.Track, given.Clip)]
                : [.. audio];

        if (matches.Length == 0)
        {
            string streams = audio.Count == 0
                ? "It has no linked audio."
                : $"Its audio streams are {string.Join(", ", audio.Select(entry => entry.Clip.SourceStreamIndex))}.";

            throw new CommandException("stream-not-found", $"Clip '{command.ClipId}' has no audio for stream {command.Stream}. {streams}");
        }

        if (matches.Length > 1)
        {
            throw new CommandException(
                "ambiguous-stream",
                $"Clip '{command.ClipId}' has {matches.Length} audio streams "
                + $"({string.Join(", ", matches.Select(entry => $"{entry.Clip.SourceStreamIndex} on {entry.Track.Name}"))}). "
                + "Say which with --stream.");
        }

        (Track track, Clip clip) = matches[0];
        HandlerHelp.RequireUnlocked(track);

        return AudioHelp.Replace(project, new ClipLocation(given.Sequence, track, clip), clip with { Enabled = !command.Muted }, context);
    }
}

/// <summary>Unlinks a clip's audio from its picture.</summary>
public sealed class DetachAudioHandler : ICommandHandler<DetachAudioCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DetachAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation given = HandlerHelp.Clip(project, command.ClipId);

        if (given.Clip.LinkGroupId is null)
        {
            throw new CommandException("not-linked", $"Clip '{command.ClipId}' is not linked to anything, so there is nothing to detach.");
        }

        List<(Track Track, Clip Clip)> linked = AudioHelp.Linked(given.Sequence, given.Clip);
        var audio = linked.Where(entry => entry.Track.Kind == TrackKind.Audio).ToList();
        var rest = linked.Where(entry => entry.Track.Kind != TrackKind.Audio).ToList();

        if (audio.Count == 0 || rest.Count == 0)
        {
            throw new CommandException(
                "nothing-to-detach",
                audio.Count == 0
                    ? $"Clip '{command.ClipId}' has no linked audio."
                    : $"Clip '{command.ClipId}' is linked only to other audio. Use clip.unlink to separate those.");
        }

        foreach ((Track track, _) in linked)
        {
            HandlerHelp.RequireUnlocked(track);
        }

        // The audio keeps a link among itself when there is more than one clip of it, so a
        // capture's game and microphone still move together. A picture left on its own is not
        // linked to anything.
        string? audioGroup = audio.Count > 1 ? Id.New() : null;
        string? restGroup = rest.Count > 1 ? given.Clip.LinkGroupId : null;

        foreach ((Track _, Clip clip) in linked)
        {
            bool isAudio = audio.Any(entry => entry.Clip.Id == clip.Id);
            Clip updated = clip with { LinkGroupId = isAudio ? audioGroup : restGroup };

            // Look the track up afresh: two of the clips may share one, and the first
            // replacement has already changed it.
            Track current = project.TrackOf(clip.Id)!;
            project = project.ReplaceTrack(current.ReplaceClip(updated));
            context.Changed(clip.Id);
            context.Changed(current.Id);
        }

        return project;
    }
}

/// <summary>Swaps what an audio clip plays for a stream of another media item.</summary>
public sealed class ReplaceAudioHandler : ICommandHandler<ReplaceAudioCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ReplaceAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        MediaItem media = project.MediaItem(command.MediaId)
            ?? throw new CommandException("missing-media-reference", $"No media with id '{command.MediaId}' in this project.");

        MediaStream[] audio = media.Info is { } info ? [.. info.AudioStreams] : [];
        int stream;

        if (command.Stream is { } asked)
        {
            if (media.Info is not null && !audio.Any(candidate => candidate.Index == asked))
            {
                throw new CommandException(
                    "stream-not-audio",
                    $"Stream {asked} of '{media.Name}' is not audio. Its audio streams are "
                    + (audio.Length == 0 ? "none." : $"{string.Join(", ", audio.Select(candidate => candidate.Index))}."));
            }

            stream = asked;
        }
        else if (media.Info is not null)
        {
            stream = audio.Length > 0
                ? audio[0].Index
                : throw new CommandException("stream-not-audio", $"'{media.Name}' has no audio.");
        }
        else
        {
            stream = 0;
        }

        if (media.Duration > Flicks.Zero && found.Clip.SourceOut > media.Duration)
        {
            throw new CommandException(
                "past-source-end",
                $"'{media.Name}' runs {media.Duration.ToSeconds():0.###} s and the clip plays up to "
                + $"{found.Clip.SourceOut.ToSeconds():0.###} s of its source. Trim or slip the clip first.");
        }

        Clip updated = found.Clip with { MediaId = media.Id, SourceStreamIndex = stream, Name = media.Name };
        return AudioHelp.Replace(project, found, updated, context);
    }
}

/// <summary>Chooses which of a source's channels an audio clip plays.</summary>
public sealed class SetAudioChannelMapHandler : ICommandHandler<SetAudioChannelMapCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetAudioChannelMapCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = AudioHelp.AudioClip(project, command.ClipId);
        AudioChannelMap? map = command.Map == AudioChannelMap.Auto ? null : command.Map;

        return AudioHelp.Replace(project, found, found.Clip with { ChannelMap = map }, context);
    }
}

/// <summary>Sets an audio track's volume.</summary>
public sealed class SetTrackVolumeHandler : ICommandHandler<SetTrackVolumeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackVolumeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Track track = AudioHelp.AudioTrack(project, command.TrackId);
        float db = AudioHelp.Gain(command.Db);
        Track updated = track with { Volume = db == 0.0f ? null : AnimatedValue.Constant(db) };

        if (updated == track)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(updated);
    }
}

/// <summary>Sets an audio track's balance.</summary>
public sealed class SetTrackPanHandler : ICommandHandler<SetTrackPanCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackPanCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Track track = AudioHelp.AudioTrack(project, command.TrackId);
        float pan = AudioHelp.Pan(command.Pan);
        Track updated = track with { Pan = pan == 0.0f ? null : AnimatedValue.Constant(pan) };

        if (updated == track)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(updated);
    }
}
