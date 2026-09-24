using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// The lookups and checks every handler would otherwise repeat.
/// </summary>
/// <remarks>
/// Each one raises a coded <see cref="CommandException"/> rather than returning null, because a
/// handler that has asked for a clip by id has nothing useful to do without it, and the message
/// it would write is the same every time. Keeping them here means "no clip with that id" reads
/// the same however the user got there.
/// </remarks>
internal static class HandlerHelp
{
    /// <summary>The sequence with the given id, or the active one when no id is given.</summary>
    internal static Sequence Sequence(Project project, string? sequenceId)
    {
        if (sequenceId is { Length: > 0 })
        {
            return project.Sequence(sequenceId)
                ?? throw new CommandException(
                    "sequence-not-found",
                    $"No sequence with id '{sequenceId}'.",
                    "/sequences");
        }

        return project.ActiveSequence
            ?? throw new CommandException("no-sequence", "The project has no sequences.", "/sequences");
    }

    /// <summary>The track with the given id, and the sequence it is on.</summary>
    internal static (Sequence Sequence, Track Track) Track(Project project, string trackId)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            if (sequence.Track(trackId) is { } track)
            {
                return (sequence, track);
            }
        }

        throw new CommandException("track-not-found", $"No track with id '{trackId}'.", "/sequences");
    }

    /// <summary>The clip with the given id, with its track and sequence.</summary>
    internal static ClipLocation Clip(Project project, string clipId) =>
        project.FindClip(clipId)
        ?? throw new CommandException("clip-not-found", $"No clip with id '{clipId}'.", "/sequences");

    /// <summary>Refuses the edit when the track is locked.</summary>
    internal static void RequireUnlocked(Track track)
    {
        if (track.Locked)
        {
            throw new CommandException(
                "track-locked",
                $"Track '{track.Name}' is locked. Unlock it with 'jazz track set-lock {track.Id} false'.");
        }
    }

    /// <summary>
    /// A path someone typed, as a full path: against the project's folder, or the working folder
    /// for a project that has never been saved.
    /// </summary>
    internal static string Resolve(HandlerContext context, string path) => Resolve(context.ProjectPath, path);

    /// <summary>The same, for a query, which knows the project path from its session.</summary>
    internal static string Resolve(string projectPath, string path) =>
        projectPath.Length == 0 ? Path.GetFullPath(path) : ProjectPaths.Resolve(projectPath, path);

    /// <summary>
    /// A media path as the project keeps it: relative to the project file, or absolute for a
    /// project that has never been saved, which saving then makes relative.
    /// </summary>
    internal static string Store(HandlerContext context, string path) =>
        context.ProjectPath.Length == 0 ? Path.GetFullPath(path) : ProjectPaths.Store(context.ProjectPath, path);

    /// <summary>An identifier the caller supplied, checked, or a fresh one.</summary>
    internal static string IdOr(string? given) => given is { Length: > 0 } ? CommandValues.ParseId(given) : Id.New();

    /// <summary>Refuses an id that is already in use, which would make two things indistinguishable.</summary>
    internal static void RequireUnused(Project project, string id)
    {
        if (project.Sequence(id) is not null
            || project.MediaItem(id) is not null
            || project.FindClip(id) is not null
            || project.Sequences.Any(sequence => sequence.Track(id) is not null)
            || Core.Effects.ParamTargets.Find(project, id) is not null
            || project.EffectPresets.Any(preset => string.Equals(preset.Id, id, StringComparison.Ordinal)))
        {
            throw new CommandException("duplicate-id", $"'{id}' is already used by something else in this project.");
        }
    }

    /// <summary>Applies the settings members a command actually gave.</summary>
    /// <remarks>
    /// Every settings command takes the same optional five, and leaving one out means "leave it
    /// alone" rather than "set it to nothing". Doing that in one place is what stops the project
    /// and the sequence versions drifting apart.
    /// </remarks>
    internal static ProjectSettings Apply(
        ProjectSettings settings,
        Rational? fps,
        FrameSize? size,
        int? sampleRate,
        int? channelCount,
        string? colorSpace)
    {
        if (fps is { } rate)
        {
            if (rate.Num <= 0)
            {
                throw new CommandException("invalid-frame-rate", "A frame rate has to be greater than zero.");
            }

            settings = settings with { FrameRate = rate };
        }

        if (size is { } frame)
        {
            if (frame.Width < 16 || frame.Height < 16)
            {
                throw new CommandException("invalid-frame-size", "A frame is at least 16 by 16.");
            }

            settings = settings with { Width = frame.Width, Height = frame.Height };
        }

        if (sampleRate is { } rateHz)
        {
            if (rateHz is not (44100 or 48000 or 96000))
            {
                throw new CommandException(
                    "invalid-sample-rate",
                    $"{rateHz} Hz is not a sample rate Jazz Hands works in. Try 44100, 48000 or 96000.");
            }

            settings = settings with { SampleRate = rateHz };
        }

        if (channelCount is { } channels)
        {
            if (channels is not (1 or 2 or 6))
            {
                throw new CommandException(
                    "invalid-channel-count",
                    $"{channels} channels is not a layout Jazz Hands works in. Try 1, 2 or 6.");
            }

            settings = settings with { ChannelCount = channels };
        }

        if (colorSpace is { Length: > 0 })
        {
            settings = settings with { ColorSpace = colorSpace };
        }

        return settings;
    }

    /// <summary>The name a new track gets when the command did not say: V1, A2 and so on.</summary>
    internal static string TrackName(Sequence sequence, TrackKind kind)
    {
        string prefix = kind switch
        {
            TrackKind.Video => "V",
            TrackKind.Audio => "A",
            TrackKind.Subtitle => "S",
            _ => "FX",
        };

        int number = sequence.Tracks.Count(track => track.Kind == kind) + 1;
        return $"{prefix}{number}";
    }
}
