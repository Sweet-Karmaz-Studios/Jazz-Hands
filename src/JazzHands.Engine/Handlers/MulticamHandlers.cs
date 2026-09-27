using System.Globalization;
using JazzHands.Audio.Analysis;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the multicam handlers share: lining recordings up, and finding a multicam clip.</summary>
internal static class MulticamHelp
{
    /// <summary>How much of each recording is listened to for a sync by sound.</summary>
    internal static readonly Flicks Listen = Flicks.FromSeconds(120);

    /// <summary>Where each recording starts in the multicam's time, and how sure each match is.</summary>
    internal static MulticamSyncInfo Sync(Project project, IReadOnlyList<string> mediaIds, MulticamSync sync, string projectPath, CancellationToken cancellationToken)
    {
        if (mediaIds.Count < 2)
        {
            throw new CommandException("invalid-value", "A multicam needs two recordings or more.", "mediaIds");
        }

        if (mediaIds.Distinct(StringComparer.Ordinal).Count() != mediaIds.Count)
        {
            throw new CommandException("invalid-value", "A recording is named twice.", "mediaIds");
        }

        MediaItem[] items = [.. mediaIds.Select(id => MediaServices.Require(project, id))];
        var starts = new double[items.Length];
        var confidence = new double[items.Length];
        Array.Fill(confidence, 1.0);

        switch (sync)
        {
            case MulticamSync.In:
                // The in points line up: a recording starting later in its file starts earlier.
                for (int index = 0; index < items.Length; index++)
                {
                    starts[index] = -items[index].DefaultIn.ToSeconds();
                }

                break;

            case MulticamSync.Marker:
                for (int index = 0; index < items.Length; index++)
                {
                    Marker first = items[index].Markers.OrderBy(marker => marker.Time).FirstOrDefault()
                        ?? throw new CommandException("no-marker", $"'{items[index].Name}' has no marker. Mark the same moment (a clap) on each file, or sync by sound.");
                    starts[index] = -first.Time.ToSeconds();
                }

                break;

            case MulticamSync.Timecode:
                for (int index = 0; index < items.Length; index++)
                {
                    starts[index] = StartTimecode(items[index], projectPath).ToSeconds();
                }

                break;

            default:
                float[] reference = Sound(items[0], projectPath, cancellationToken);
                for (int index = 1; index < items.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SyncMatch? match = SyncFinder.Find(Sound(items[index], projectPath, cancellationToken), reference, ClipSync.Rate);
                    if (match is null)
                    {
                        confidence[index] = 0;
                        continue;
                    }

                    // This one has the sound later by the lag, so it starts that much earlier.
                    starts[index] = -match.LagSeconds;
                    confidence[index] = match.Ambiguous ? Math.Min(match.Confidence, ClipSync.Sure / 2) : match.Confidence;
                }

                break;
        }

        double earliest = starts.Min();
        AngleSyncInfo[] angles = [.. items.Select((item, index) => new AngleSyncInfo(item.Id, item.Name, Flicks.FromSeconds(starts[index] - earliest), Math.Round(confidence[index], 3)))];
        string? weakest = sync == MulticamSync.Audio ? angles.Skip(1).MinBy(angle => angle.Confidence)?.MediaId : null;
        return new MulticamSyncInfo([.. angles], weakest);
    }

    /// <summary>A recording's sound, mono, from its start, for matching.</summary>
    private static float[] Sound(MediaItem item, string projectPath, CancellationToken cancellationToken)
    {
        MediaStream stream = item.Info?.AudioStreams.FirstOrDefault()
            ?? throw new CommandException("no-sound", $"'{item.Name}' has no sound to sync by. Sync by timecode, in points or markers instead.");
        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.");
        }

        return Media.Audio.MonoReader.Read(path, stream.Index, Flicks.Zero, Flicks.Min(item.Duration, Listen), ClipSync.Rate, cancellationToken);
    }

    /// <summary>The timecode a recording starts at, from its file's tags.</summary>
    private static Flicks StartTimecode(MediaItem item, string projectPath)
    {
        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        Media.Probe.MediaProbe probe = new Media.Probe.Prober().Probe(path, detectFrameRateMode: false);
        string? tag = probe.Streams.Select(stream => stream.Tags.GetValueOrDefault("timecode")).FirstOrDefault(value => value is not null)
            ?? probe.Tags.GetValueOrDefault("timecode");
        Rational rate = item.Info?.VideoStreams.FirstOrDefault()?.FrameRate is { IsZero: false } known ? known : new Rational(30, 1);
        if (tag is null || !Timecode.TryParse(tag.Replace(';', ':'), rate, out Flicks start))
        {
            throw new CommandException("no-timecode", $"'{item.Name}' has no start timecode. Sync by sound, in points or markers instead.");
        }

        return start;
    }

    /// <summary>A multicam clip and its multicam, refusing anything else.</summary>
    internal static (ClipLocation Found, Sequence Nested, Multicam Multicam) Clip(Project project, string clipId)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        if (found.Clip.SequenceId is not { } id || project.Sequence(id) is not { Multicam: { } multicam } nested)
        {
            throw new CommandException("not-multicam", $"'{found.Clip.Name}' is not a multicam clip.");
        }

        return (found, nested, multicam);
    }

    /// <summary>An angle number from a command, from 1, checked.</summary>
    internal static int Angle(Multicam multicam, int angle)
    {
        if (angle < 1 || angle > multicam.Angles.Length)
        {
            throw new CommandException("invalid-value", $"The multicam has angles 1 to {multicam.Angles.Length}.", "angle");
        }

        return angle - 1;
    }
}

/// <summary>Lines recordings up.</summary>
public sealed class SyncMulticamHandler : IQueryHandler<SyncMulticamQuery, MulticamSyncInfo>
{
    /// <inheritdoc />
    public MulticamSyncInfo Handle(Project project, SyncMulticamQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        return MulticamHelp.Sync(project, [.. query.MediaIds], query.Sync, context.Session?.ProjectPath ?? string.Empty, CancellationToken.None);
    }
}

/// <summary>Makes a multicam clip.</summary>
public sealed class CreateMulticamHandler : ICommandHandler<CreateMulticamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CreateMulticamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MulticamSyncInfo sync = MulticamHelp.Sync(project, [.. command.MediaIds], command.Sync, context.ProjectPath, context.Cancellation);
        if (!command.Force && sync.Angles.Skip(1).FirstOrDefault(angle => angle.Confidence < ClipSync.Sure) is { } unsure)
        {
            throw new CommandException(
                "unsure-match",
                string.Create(CultureInfo.InvariantCulture, $"'{unsure.Name}' matched by sound with a confidence of only {unsure.Confidence:0.00}. Check it with multicam.sync, or sync another way, or pass --force."));
        }

        Sequence host = project.ActiveSequence ?? throw new CommandException("sequence-not-found", "The project has no sequence to put the multicam in.");
        Track track = command.TrackId is { } trackId
            ? HandlerHelp.Track(project, trackId).Track
            : host.Tracks.Where(candidate => candidate.Kind == TrackKind.Video && !candidate.Locked).OrderBy(candidate => candidate.Order).FirstOrDefault()
                ?? throw new CommandException("no-track", "The sequence has no unlocked picture track for the multicam clip.");
        HandlerHelp.RequireUnlocked(track);
        if (track.Kind != TrackKind.Video)
        {
            throw new CommandException("wrong-track", $"'{track.Name}' is not a picture track.");
        }

        // The multicam sequence: per recording, its picture on a track and its sound on tracks of its own.
        string sequenceId = HandlerHelp.IdOr(command.SequenceId);
        HandlerHelp.RequireUnused(project, sequenceId);
        var tracks = new List<Track>();
        var angles = new List<MulticamAngle>();
        // Each recording starts on a frame of the multicam, the nearest to where it matched, so its
        // cuts are on frames: within half a frame of the sound, never more.
        Rational rate = project.SettingsFor(host).FrameRate;
        foreach (AngleSyncInfo angle in sync.Angles)
        {
            MediaItem item = project.MediaItem(angle.MediaId)!;
            var range = new TimeRange(angle.Start.SnapToFrame(rate, RoundingMode.Nearest), item.Duration);
            string? pictureId = null;
            if (item.Info?.VideoStreams.FirstOrDefault() is { } video || item.IsImages)
            {
                pictureId = Id.New();
                tracks.Add(new Track(pictureId, TrackKind.Video, item.Name, tracks.Count)
                    .AddClip(new Clip(Id.New(), range, Flicks.Zero, MediaId: item.Id, SourceStreamIndex: item.Info?.VideoStreams.FirstOrDefault()?.Index ?? 0, Name: item.Name)));
            }

            var sounds = new List<string>();
            foreach (MediaStream stream in item.Info?.AudioStreams ?? [])
            {
                string soundId = Id.New();
                sounds.Add(soundId);
                tracks.Add(new Track(soundId, TrackKind.Audio, stream.Title is { Length: > 0 } title ? $"{item.Name} {title}" : item.Name, tracks.Count)
                    .AddClip(new Clip(Id.New(), range, Flicks.Zero, MediaId: item.Id, SourceStreamIndex: stream.Index, Name: item.Name)));
            }

            angles.Add(new MulticamAngle(item.Name, pictureId, [.. sounds]));
        }

        int number = project.Sequences.Count(sequence => sequence.Multicam is not null) + 1;
        string name = command.Name is { Length: > 0 } given ? given : string.Create(CultureInfo.InvariantCulture, $"Multicam {number}");
        var nested = new Sequence(sequenceId, name, [.. tracks], Settings: project.SettingsFor(host), Multicam: new Multicam([.. angles]));

        string clipId = HandlerHelp.IdOr(command.ClipId);
        HandlerHelp.RequireUnused(project, clipId);
        Flicks at = command.At ?? track.Duration;
        var clip = new Clip(clipId, new TimeRange(at, nested.Duration), Flicks.Zero, SequenceId: nested.Id, Name: name);
        if (at < Flicks.Zero || EditOps.Overlaps(track, clip))
        {
            throw new CommandException("would-overlap", $"'{track.Name}' has something there already. Give --at a free place, or leave it out for the end.");
        }

        context.Changed(nested.Id);
        context.Changed(clip.Id);
        context.Changed(track.Id);
        return project.AddSequence(nested).ReplaceTrack(track.AddClip(clip));
    }
}

/// <summary>Cuts a multicam clip to another angle.</summary>
public sealed class SwitchAngleHandler : ICommandHandler<SwitchAngleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SwitchAngleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, Sequence nested, Multicam multicam) = MulticamHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        if (command.PictureOnly && command.SoundOnly)
        {
            throw new CommandException("invalid-value", "Switch the picture only or the sound only, not both only.", "video-only");
        }

        if (command.At < found.Clip.Start || command.At >= found.Clip.End)
        {
            throw new CommandException("time-out-of-range", "The switch has to be inside the multicam clip.", "at");
        }

        int angle = MulticamHelp.Angle(multicam, command.Angle);
        MulticamAngle chosen = multicam.Angles[angle];
        Flicks at = found.Clip.SourceTimeAt(command.At).SnapToFrame(project.SettingsFor(nested).FrameRate);
        int? picture = command.SoundOnly || chosen.PictureTrackId is null ? null : angle;
        int? sound = command.PictureOnly || chosen.SoundTrackIds.IsEmpty ? null : angle;
        if (picture is null && sound is null)
        {
            throw new CommandException("invalid-value", $"Angle {command.Angle} has no {(command.SoundOnly ? "sound" : "picture")} to switch to.", "angle");
        }

        Multicam switched = multicam.WithSwitch(new AngleSwitch(at, picture, sound));
        if (switched == multicam)
        {
            return project;
        }

        context.Changed(nested.Id);
        context.Changed(found.Clip.Id);
        return project.ReplaceSequence(nested with { Multicam = switched });
    }
}

/// <summary>Keeps a multicam's sound on one angle, or lets it follow.</summary>
public sealed class SetMulticamSoundHandler : ICommandHandler<SetMulticamSoundCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMulticamSoundCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, Sequence nested, Multicam multicam) = MulticamHelp.Clip(project, command.ClipId);
        int? fixedSound = command.Angle == 0 ? null : MulticamHelp.Angle(multicam, command.Angle);
        if (fixedSound == multicam.FixedSound)
        {
            return project;
        }

        context.Changed(nested.Id);
        context.Changed(found.Clip.Id);
        return project.ReplaceSequence(nested with { Multicam = multicam with { FixedSound = fixedSound } });
    }
}

/// <summary>Shows a multicam's angles in the program monitor.</summary>
public sealed class ViewMulticamHandler : ICommandHandler<ViewMulticamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ViewMulticamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.ClipId is { } clipId)
        {
            _ = MulticamHelp.Clip(project, clipId);
        }

        return PlaybackHelp.Drive(project, context, playback => playback.MulticamGrid = command.ClipId);
    }
}

/// <summary>Turns a multicam clip into ordinary cuts.</summary>
public sealed class FlattenMulticamHandler : ICommandHandler<FlattenMulticamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, FlattenMulticamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, Sequence nested, Multicam multicam) = MulticamHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip compound = found.Clip;
        if (compound.Reverse || compound.IsRemapped || compound.IsHold || compound.EffectiveSpeed != Rational.One)
        {
            throw new CommandException("not-plain", "A multicam clip at a speed, reversed or remapped cannot be flattened. Put it back to normal speed first.");
        }

        Flicks offset = compound.Start - compound.SourceIn;
        Sequence host = found.Sequence;
        Track picture = found.Track.RemoveClip(compound.Id);
        host = host.ReplaceTrack(picture);

        // Where each angle's k-th sound goes: the k-th audio track of the sequence, or a new one
        // when that is missing or has something where the multicam plays.
        int soundTracks = multicam.Angles.Max(angle => angle.SoundTrackIds.Length);
        var soundTargets = new List<string>();
        Track[] audio = [.. host.Tracks.Where(track => track.Kind == TrackKind.Audio && !track.Locked).OrderBy(track => track.Order)];
        for (int k = 0; k < soundTracks; k++)
        {
            Track? target = k < audio.Length && !audio[k].Clips.Any(clip => clip.Start < compound.End && clip.End > compound.Start) ? audio[k] : null;
            if (target is null)
            {
                target = new Track(Id.New(), TrackKind.Audio, string.Create(CultureInfo.InvariantCulture, $"{nested.Name} sound {k + 1}"), host.NextTrackOrder());
                host = host.AddTrack(target);
                context.Changed(target.Id);
            }

            soundTargets.Add(target.Id);
        }

        IReadOnlyList<(Flicks At, int Picture, int Sound)> changes = multicam.Changes();
        for (int index = 0; index < changes.Count; index++)
        {
            Flicks from = Flicks.Max(index == 0 ? compound.SourceIn : changes[index].At, compound.SourceIn);
            Flicks to = Flicks.Min(index + 1 < changes.Count ? changes[index + 1].At : compound.SourceOut, compound.SourceOut);
            if (to <= from)
            {
                continue;
            }

            string link = Id.New();
            MulticamAngle seen = multicam.Angles[changes[index].Picture];
            MulticamAngle heard = multicam.Angles[changes[index].Sound];
            if (seen.PictureTrackId is { } seenTrack)
            {
                host = Place(host, nested.Track(seenTrack), found.Track.Id, from, to, offset, link, context);
            }

            for (int k = 0; k < heard.SoundTrackIds.Length; k++)
            {
                host = Place(host, nested.Track(heard.SoundTrackIds[k]), soundTargets[k], from, to, offset, link, context);
            }
        }

        context.Changed(compound.Id);
        context.Changed(found.Track.Id);
        project = project.ReplaceSequence(host);

        // The multicam sequence goes when no clip plays it any more; undo brings it back.
        bool used = project.Sequences.Any(sequence => sequence.Tracks.Any(track => track.Clips.Any(clip => clip.SequenceId == nested.Id)));
        return used ? project : project.RemoveSequence(nested.Id);
    }

    /// <summary>Puts the stretch of a multicam track between two of its times on a track of the sequence.</summary>
    private static Sequence Place(Sequence host, Track? source, string targetId, Flicks from, Flicks to, Flicks offset, string link, HandlerContext context)
    {
        if (source is null)
        {
            return host;
        }

        foreach (Clip inner in source.Clips)
        {
            Flicks start = Flicks.Max(inner.Start, from);
            Flicks end = Flicks.Min(inner.End, to);
            if (end <= start)
            {
                continue;
            }

            Clip piece = inner with
            {
                Id = Id.New(),
                Range = TimeRange.FromBounds(start + offset, end + offset),
                SourceIn = inner.SourceTimeAt(start),
                LinkGroupId = link,
            };
            Track target = host.Track(targetId)!;
            if (EditOps.Overlaps(target, piece))
            {
                throw new CommandException("would-overlap", $"'{target.Name}' has something where the multicam's sound would go.");
            }

            host = host.ReplaceTrack(target.AddClip(piece));
            context.Changed(piece.Id);
        }

        return host;
    }
}
