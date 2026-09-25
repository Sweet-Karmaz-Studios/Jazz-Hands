using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Media.Interop;
using JazzHands.Media.Subtitles;

namespace JazzHands.Engine.Handlers;

/// <summary>What the subtitle commands share: finding tracks and cues, and reading files and streams.</summary>
internal static class SubtitleHelp
{
    /// <summary>A subtitle track, with the sequence it is on.</summary>
    internal static (Sequence Sequence, Track Track) Track(Project project, string trackId, bool editing = true)
    {
        (Sequence sequence, Track track) = HandlerHelp.Track(project, trackId);
        if (track.Kind != TrackKind.Subtitle)
        {
            throw new CommandException("not-subtitles", $"Track '{track.Name}' carries {track.Kind.ToString().ToLowerInvariant()}, not subtitles. 'jazz track add subtitle' makes a subtitle track.");
        }

        if (editing)
        {
            HandlerHelp.RequireUnlocked(track);
        }

        return (sequence, track);
    }

    /// <summary>A cue on an unlocked subtitle track.</summary>
    internal static ClipLocation Cue(Project project, string cueId)
    {
        ClipLocation found = HandlerHelp.Clip(project, cueId);
        if (found.Clip.Cue is null || found.Track.Kind != TrackKind.Subtitle)
        {
            throw new CommandException("not-a-cue", $"Clip '{found.Clip.Name}' is on {found.Track.Kind.ToString().ToLowerInvariant()} track '{found.Track.Name}' and is not a subtitle cue.");
        }

        HandlerHelp.RequireUnlocked(found.Track);
        return found;
    }

    /// <summary>A track with its cues in time order again after an edit.</summary>
    internal static Track Sorted(Track track, IEnumerable<Clip> clips) =>
        track with { Clips = new EquatableArray<Clip>([.. clips.OrderBy(clip => clip.Start)]) };

    /// <summary>A language code as stored: lower case, or null for undetermined.</summary>
    internal static string? Language(string? code)
    {
        string? trimmed = code?.Trim().ToLowerInvariant();
        if (trimmed is null or "" or "und")
        {
            return null;
        }

        if (trimmed.Length is < 2 or > 3 || !trimmed.All(char.IsAsciiLetterLower))
        {
            throw new CommandException("invalid-value", $"'{code}' is not a language code. Use two or three letters, such as eng or fra; und for none.");
        }

        return trimmed;
    }

    /// <summary>Refuses a cue with no length or starting before the timeline.</summary>
    internal static void CheckTimes(Flicks start, Flicks duration)
    {
        if (start.IsNegative)
        {
            throw new CommandException("time-out-of-range", "A cue cannot start before the timeline does.");
        }

        if (duration.Value <= 0)
        {
            throw new CommandException("invalid-duration", "A cue has to show for some time.");
        }
    }

    /// <summary>
    /// Reads subtitles from a file or a media item's stream: the document, the format's name, and
    /// the stream's language and title when it was a stream.
    /// </summary>
    internal static (SubtitleDocument Document, string Format, string? Language, string? Title) Read(
        Project project,
        string? file,
        string? mediaId,
        int? stream,
        string projectPath)
    {
        if (file is { Length: > 0 })
        {
            string path = HandlerHelp.Resolve(projectPath, file);
            if (!File.Exists(path))
            {
                throw new CommandException("file-not-found", $"There is no file at '{path}'.");
            }

            string text = File.ReadAllText(path);
            SubtitleFormat format = SubtitleFiles.FormatOf(path) ?? SubtitleFiles.Sniff(text);
            try
            {
                return (SubtitleFiles.Parse(text, format), format.ToString().ToLowerInvariant(), null, null);
            }
            catch (SubtitleFormatException error)
            {
                throw new CommandException("cannot-read", error.Message);
            }
        }

        if (mediaId is not { Length: > 0 })
        {
            throw new CommandException("missing-value", "Give a subtitle file, or --media and optionally --stream.");
        }

        MediaItem item = project.MediaItem(mediaId)
            ?? throw new CommandException("media-not-found", $"No media item with id '{mediaId}'.", "/media");
        MediaStream[] streams = [.. (item.Info?.Streams ?? default).Where(candidate => candidate.Kind == MediaStreamKind.Subtitle)];
        MediaStream chosen = stream is { } index
            ? streams.FirstOrDefault(candidate => candidate.Index == index)
                ?? throw new CommandException("stream-not-found", $"'{item.Name}' has no subtitle stream {index}. Its subtitle streams are {(streams.Length == 0 ? "none" : string.Join(", ", streams.Select(candidate => candidate.Index)))}.")
            : streams.FirstOrDefault()
                ?? throw new CommandException("no-subtitles", $"'{item.Name}' has no subtitle streams.");

        string full = HandlerHelp.Resolve(projectPath, item.RelativePath);
        try
        {
            return (SubtitleExtractor.Extract(full, chosen.Index), chosen.Codec, chosen.Language, chosen.Title);
        }
        catch (Exception error) when (error is SubtitleFormatException or FfmpegException or IOException)
        {
            throw new CommandException("cannot-read", error.Message);
        }
    }
}

/// <summary>Imports subtitles from a file or an embedded stream onto a subtitle track.</summary>
public sealed class ImportSubtitlesHandler : ICommandHandler<ImportSubtitlesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ImportSubtitlesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (SubtitleDocument document, _, string? streamLanguage, string? title) = SubtitleHelp.Read(project, command.File, command.MediaId, command.Stream, context.ProjectPath);
        string? language = SubtitleHelp.Language(command.Language ?? streamLanguage);
        Flicks offset = command.Offset ?? Flicks.Zero;

        var clips = SubtitleTracks.Clips(document, offset);
        if (clips.IsEmpty)
        {
            throw new CommandException("nothing-to-import", "There are no cues to import: none has a length and starts on the timeline.");
        }

        Sequence sequence;
        Track track;
        if (command.TrackId is { Length: > 0 } trackId)
        {
            (sequence, track) = SubtitleHelp.Track(project, trackId);
            if (command.Language is not null)
            {
                track = track with { Language = language };
            }
        }
        else
        {
            sequence = HandlerHelp.Sequence(project, command.SequenceId);
            string name = title is { Length: > 0 }
                ? title
                : command.File is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file) : HandlerHelp.TrackName(sequence, TrackKind.Subtitle);
            int order = sequence.Tracks.IsEmpty ? 0 : sequence.Tracks.Max(existing => existing.Order) + 1;
            track = new Track(HandlerHelp.IdOr(command.NewTrackId), TrackKind.Subtitle, name, order, Language: language, SubtitleStyle: document.Style);
            project = project.ReplaceSequence(sequence.AddTrack(track));
        }

        context.Changed(track.Id);
        foreach (Clip clip in clips)
        {
            context.Changed(clip.Id);
        }

        return project.ReplaceTrack(SubtitleHelp.Sorted(track, track.Clips.Concat(clips)));
    }
}

/// <summary>Adds one cue.</summary>
public sealed class AddCueHandler : ICommandHandler<AddCueCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddCueCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = SubtitleHelp.Track(project, command.TrackId);
        SubtitleHelp.CheckTimes(command.At, command.Duration);
        string id = HandlerHelp.IdOr(command.CueId);
        if (project.FindClip(id) is not null)
        {
            throw new CommandException("duplicate-id", $"'{id}' is already a clip in this project.");
        }

        Clip cue = SubtitleTracks.Clip(id, command.At, command.Duration, new Cue(command.Text, command.Align));
        context.Changed(id);
        context.Changed(track.Id);
        return project.ReplaceTrack(SubtitleHelp.Sorted(track, track.Clips.Add(cue)));
    }
}

/// <summary>Changes a cue's text.</summary>
public sealed class SetCueTextHandler : ICommandHandler<SetCueTextCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetCueTextCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = SubtitleHelp.Cue(project, command.CueId);
        Cue cue = found.Clip.Cue!;
        if (cue.Text == command.Text)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Cue = cue with { Text = command.Text }, Name = SubtitleTracks.Label(command.Text) }));
    }
}

/// <summary>Changes when a cue shows.</summary>
public sealed class SetCueTimeHandler : ICommandHandler<SetCueTimeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetCueTimeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = SubtitleHelp.Cue(project, command.CueId);
        if (command.Duration is not null && command.End is not null)
        {
            throw new CommandException("invalid-value", "Give --dur or --end, not both.");
        }

        Flicks start = command.At ?? found.Clip.Start;
        Flicks duration = command.Duration
            ?? (command.End is { } end ? end - start : found.Clip.Duration);
        SubtitleHelp.CheckTimes(start, duration);

        Clip moved = found.Clip with { Range = new TimeRange(start, duration) };
        if (moved == found.Clip)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(SubtitleHelp.Sorted(found.Track, found.Track.Clips.Select(clip => clip.Id == moved.Id ? moved : clip)));
    }
}

/// <summary>Moves a cue to another place on the frame.</summary>
public sealed class SetCueAlignHandler : ICommandHandler<SetCueAlignCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetCueAlignCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!Enum.IsDefined(command.Align))
        {
            throw new CommandException("invalid-value", $"{(int)command.Align} is not a place on the frame.");
        }

        ClipLocation found = SubtitleHelp.Cue(project, command.CueId);
        if (found.Clip.Cue!.Align == command.Align)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Cue = found.Clip.Cue with { Align = command.Align } }));
    }
}

/// <summary>Breaks long cues into lines and shorter cues.</summary>
public sealed class SplitLongCuesHandler : ICommandHandler<SplitLongCuesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SplitLongCuesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = SubtitleHelp.Track(project, command.TrackId);
        SubtitleStyle style = track.SubtitleStyle ?? SubtitleStyle.Default;
        int maxChars = command.MaxChars ?? style.MaxChars;
        int maxLines = command.MaxLines ?? style.MaxLines;
        if (maxChars < 1 || maxLines < 1)
        {
            throw new CommandException("value-out-of-range", "A line needs at least one character, and a cue at least one line.");
        }

        var clips = new List<Clip>();
        foreach (Clip clip in track.Clips)
        {
            if (clip.Cue is not { } cue)
            {
                clips.Add(clip);
                continue;
            }

            var pieces = CueText.Split(cue.Text, maxChars, maxLines);
            if (pieces.Length == 1)
            {
                Clip wrapped = clip with { Cue = cue with { Text = pieces[0].Markup } };
                clips.Add(wrapped);
                if (wrapped != clip)
                {
                    context.Changed(clip.Id);
                }

                continue;
            }

            // The first piece keeps the cue's id; the rest are new cues after it.
            Flicks at = clip.Start;
            for (int index = 0; index < pieces.Length; index++)
            {
                Flicks length = index == pieces.Length - 1
                    ? clip.End - at
                    : new Flicks((long)Math.Round(clip.Duration.Value * pieces[index].Share));
                Clip piece = index == 0
                    ? clip with { Range = new TimeRange(at, length), Cue = cue with { Text = pieces[index].Markup, Raw = null }, Name = SubtitleTracks.Label(pieces[index].Markup) }
                    : SubtitleTracks.Clip(Id.New(), at, length, cue with { Text = pieces[index].Markup, Raw = null, Name = null });
                clips.Add(piece);
                context.Changed(piece.Id);
                at += length;
            }
        }

        if (context.ChangedIds.IsEmpty)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(SubtitleHelp.Sorted(track, clips));
    }
}

/// <summary>Moves a track's cues.</summary>
public sealed class ShiftCuesHandler : ICommandHandler<ShiftCuesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ShiftCuesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = SubtitleHelp.Track(project, command.TrackId);
        Flicks from = command.From ?? Flicks.MinValue;
        Clip[] moving = [.. track.Clips.Where(clip => clip.Cue is not null && clip.Start >= from)];
        if (moving.Length == 0 || command.By.IsZero)
        {
            return project;
        }

        if (moving.Min(clip => clip.Start) + command.By < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", $"That would move the first cue to {Timecode.FormatClock(moving.Min(clip => clip.Start) + command.By)}, before the timeline starts.");
        }

        var moved = new HashSet<string>(moving.Select(clip => clip.Id), StringComparer.Ordinal);
        foreach (string id in moved)
        {
            context.Changed(id);
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(SubtitleHelp.Sorted(
            track,
            track.Clips.Select(clip => moved.Contains(clip.Id) ? clip with { Range = new TimeRange(clip.Start + command.By, clip.Duration) } : clip)));
    }
}

/// <summary>Finds and replaces text in a track's cues.</summary>
public sealed class ReplaceCueTextHandler : ICommandHandler<ReplaceCueTextCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ReplaceCueTextCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.Find.Length == 0)
        {
            throw new CommandException("missing-value", "Give --find, the text to look for.");
        }

        (_, Track track) = SubtitleHelp.Track(project, command.TrackId);
        var clips = new List<Clip>(track.Clips.Length);
        foreach (Clip clip in track.Clips)
        {
            if (clip.Cue is { } cue && CueText.Replace(cue.Text, command.Find, command.With, command.MatchCase) is { Count: > 0 } replaced)
            {
                clips.Add(clip with { Cue = cue with { Text = replaced.Markup }, Name = SubtitleTracks.Label(replaced.Markup) });
                context.Changed(clip.Id);
            }
            else
            {
                clips.Add(clip);
            }
        }

        if (context.ChangedIds.IsEmpty)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Clips = new EquatableArray<Clip>([.. clips]) });
    }
}

/// <summary>Changes a subtitle track's style.</summary>
public sealed class SetSubtitleStyleHandler : ICommandHandler<SetSubtitleStyleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSubtitleStyleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = SubtitleHelp.Track(project, command.TrackId);
        SubtitleStyle style = track.SubtitleStyle ?? SubtitleStyle.Default;

        style = style with
        {
            Font = command.Font is { } font ? Required(font, "font") : style.Font,
            Weight = command.Weight is { } weight ? Weight(weight) : style.Weight,
            Italic = command.Italic ?? style.Italic,
            Size = command.Size is { } size ? Fraction(size, "size", 0.005, 0.5) : style.Size,
            Color = command.Color is { } color ? Colour(color) : style.Color,
            Outline = command.Outline is { } outline ? Colour(outline) : style.Outline,
            OutlineWidth = command.OutlineWidth is { } width ? Fraction(width, "outline-width", 0, 0.05) : style.OutlineWidth,
            Box = command.Box is { } box ? Colour(box) : style.Box,
            Shadow = command.Shadow is { } shadow ? Colour(shadow) : style.Shadow,
            Margin = command.Margin is { } margin ? Fraction(margin, "margin", 0, 0.45) : style.Margin,
            MaxLines = command.MaxLines is { } lines ? Count(lines, "max-lines", 1, 10) : style.MaxLines,
            MaxChars = command.MaxChars is { } chars ? Count(chars, "max-chars", 5, 200) : style.MaxChars,
        };

        SubtitleStyle? stored = style == SubtitleStyle.Default ? null : style;
        if (stored == track.SubtitleStyle)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { SubtitleStyle = stored });
    }

    private static readonly string[] Weights = ["thin", "extra-light", "light", "regular", "medium", "semibold", "bold", "extra-bold", "black"];

    private static string Required(string value, string name) =>
        value.Trim().Length > 0 ? value.Trim() : throw new CommandException("invalid-value", $"--{name} cannot be empty.");

    private static string Weight(string value) =>
        Weights.Contains(value.Trim().ToLowerInvariant())
            ? value.Trim().ToLowerInvariant()
            : throw new CommandException("invalid-value", $"'{value}' is not a weight. Use {string.Join(", ", Weights)}.");

    private static double Fraction(double value, string name, double min, double max) =>
        double.IsFinite(value) && value >= min && value <= max
            ? value
            : throw new CommandException("value-out-of-range", string.Create(CultureInfo.InvariantCulture, $"--{name} is a fraction of the frame's height, from {min} to {max}."));

    private static int Count(int value, string name, int min, int max) =>
        value >= min && value <= max
            ? value
            : throw new CommandException("value-out-of-range", $"--{name} is from {min} to {max}.");

    /// <summary>A colour as sRGB hex with its alpha, or a name the command line knows.</summary>
    private static string Colour(string value)
    {
        string hex = value.Trim().TrimStart('#');
        return hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit)
            ? "#" + hex.ToUpperInvariant()
            : CommandValues.ParseColor(value);
    }
}

/// <summary>Sets what language a track is in.</summary>
public sealed class SetTrackLanguageHandler : ICommandHandler<SetTrackLanguageCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackLanguageCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);
        string? language = SubtitleHelp.Language(command.Language);
        if (language == track.Language)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Language = language });
    }
}
