using System.Globalization;
using System.Text;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Speech;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Models;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>A clip whose sound is heard: the clip, its file, and the sound stream.</summary>
internal sealed record SoundingClip(Clip Clip, Track Track, MediaItem Item, int Stream);

/// <summary>What the speech commands share (Phase 39): which sound a clip means, its words, and cutting stretches of it out.</summary>
internal static class SpeechHelp
{
    /// <summary>A pause at least this long is shown in the transcript's text.</summary>
    internal static readonly Flicks ShownPause = Flicks.OneSecond;

    /// <summary>How much of a long pause a clean-up keeps unless told.</summary>
    internal static readonly Flicks DefaultKeep = Flicks.FromMilliseconds(250);

    /// <summary>The service, from the session or made for this one command.</summary>
    internal static TranscriptionService Service(IServiceProvider? services) =>
        services?.GetService<TranscriptionService>() ?? new TranscriptionService(services?.GetService<Media.Import.CacheManager>());

    /// <summary>The first sound stream of a media item, or null for a file without sound.</summary>
    internal static int? FirstSound(MediaItem item) =>
        item.Info?.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Audio)?.Index;

    /// <summary>
    /// The sound a clip means: its own when it plays a sound stream; for a picture clip, the sound
    /// clip linked to it from the same file, or else its file's first sound stream. Null for a
    /// clip with no file or a file with no sound.
    /// </summary>
    internal static SoundingClip? SoundOf(Project project, ClipLocation found)
    {
        Clip clip = found.Clip;
        if (clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { } item)
        {
            return null;
        }

        MediaStream? own = item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex);
        if (own?.Kind == MediaStreamKind.Audio || (own is null && found.Track.Kind == TrackKind.Audio))
        {
            return new SoundingClip(clip, found.Track, item, clip.SourceStreamIndex);
        }

        if (clip.LinkGroupId is { } group)
        {
            foreach (Track track in found.Sequence.Tracks.Where(track => track.Kind == TrackKind.Audio))
            {
                if (track.Clips.FirstOrDefault(candidate => string.Equals(candidate.LinkGroupId, group, StringComparison.Ordinal)
                    && string.Equals(candidate.MediaId, mediaId, StringComparison.Ordinal)) is { } linked)
                {
                    return new SoundingClip(linked, track, item, linked.SourceStreamIndex);
                }
            }
        }

        return FirstSound(item) is { } first ? new SoundingClip(clip, found.Track, item, first) : null;
    }

    /// <summary>
    /// The clips of a sequence whose sound is heard, in timeline order: the file clips on sound
    /// tracks that are not muted, and picture clips of files with sound that have no sound clip
    /// linked to them.
    /// </summary>
    internal static List<SoundingClip> Sounding(Project project, Sequence sequence)
    {
        var sounding = new List<SoundingClip>();
        var linkedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (Track track in sequence.Tracks.Where(track => track.Kind == TrackKind.Audio && !track.Muted))
        {
            foreach (Clip clip in track.Clips.Where(clip => clip.Enabled && clip.MediaId is not null))
            {
                if (SoundOf(project, new ClipLocation(sequence, track, clip)) is { } sound)
                {
                    sounding.Add(sound);
                    if (clip.LinkGroupId is { } group)
                    {
                        linkedGroups.Add(group);
                    }
                }
            }
        }

        foreach (Track track in sequence.Tracks.Where(track => track.Kind == TrackKind.Video))
        {
            foreach (Clip clip in track.Clips.Where(clip => clip.Enabled && clip.MediaId is not null))
            {
                if ((clip.LinkGroupId is null || !linkedGroups.Contains(clip.LinkGroupId))
                    && SoundOf(project, new ClipLocation(sequence, track, clip)) is { } sound
                    && sound.Clip.Id == clip.Id)
                {
                    sounding.Add(sound);
                }
            }
        }

        return [.. sounding.OrderBy(sound => sound.Clip.Start)];
    }

    /// <summary>The transcript of a clip's sound, or null when it has not been transcribed.</summary>
    internal static Transcript? TranscriptOf(IServiceProvider? services, SoundingClip sound) =>
        Service(services).Cached(sound.Item.Hash, sound.Stream);

    /// <summary>A clip and its words, refusing a clip with no sound or no transcript.</summary>
    internal static (ClipLocation Found, SoundingClip Sound, IReadOnlyList<TimelineWord> Words) Words(Project project, string clipId, IServiceProvider? services, IReadOnlyCollection<string>? fillers = null)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        SoundingClip sound = SoundOf(project, found)
            ?? throw new CommandException("no-sound", $"'{found.Clip.Name}' plays no sound, so nothing is said in it.");
        Transcript transcript = TranscriptOf(services, sound)
            ?? throw new CommandException("no-transcript", $"'{sound.Item.Name}' has not been transcribed. `speech.transcribe {found.Clip.Id}` hears it first.");
        return (found, sound, ClipWords.Of(sound.Clip, transcript, fillers));
    }

    /// <summary>
    /// Takes stretches of the timeline out around a clip and closes them up: on the clip's track,
    /// its linked clips' tracks and the unlocked subtitle tracks, with every sync-locked track. The
    /// latest first, so the earlier times still stand.
    /// </summary>
    internal static Project Cut(Project project, ClipLocation found, IEnumerable<(Flicks From, Flicks To)> stretches, HandlerContext context)
    {
        HandlerHelp.RequireUnlocked(found.Track);
        SequenceEdit.RefuseQuickTrim(found.Sequence);
        Sequence sequence = found.Sequence;
        Clip clip = found.Clip;
        var tracks = new List<string> { found.Track.Id };
        foreach (Track track in sequence.Tracks.Where(track => !track.Locked && track.Id != found.Track.Id))
        {
            bool linked = clip.LinkGroupId is { } group && track.Clips.Any(candidate => string.Equals(candidate.LinkGroupId, group, StringComparison.Ordinal));
            if (linked || track.Kind == TrackKind.Subtitle)
            {
                tracks.Add(track.Id);
            }
        }

        Sequence after = sequence;
        foreach ((Flicks from, Flicks to) in stretches.Where(stretch => stretch.To > stretch.From).OrderByDescending(stretch => stretch.From))
        {
            after = HandlerContext.Require(EditOps.ExtractRange(after, TimeRange.FromBounds(from, to), tracks));
        }

        return after == sequence ? project : SequenceEdit.Commit(project, sequence, after, context);
    }

    /// <summary>The filler words a command names, or the default ones.</summary>
    internal static IReadOnlyCollection<string> Fillers(EquatableArray<string> words) =>
        words.IsEmpty ? TranscribedWord.DefaultFillers : [.. words.Select(word => word.Trim().ToLowerInvariant()).Where(word => word.Length > 0)];

    /// <summary>What a clean-up of a clip would cut.</summary>
    internal static (ClipLocation Found, IReadOnlyList<SpeechCut> Cuts) CleanUp(
        Project project, string clipId, EquatableArray<string> words, Flicks? pauses, Flicks? keep, bool fillers, IServiceProvider? services)
    {
        if (pauses is { } shortest && shortest <= Flicks.Zero)
        {
            throw new CommandException("invalid-value", "A long pause has to be longer than nothing.", "pauses");
        }

        if (keep is { } kept && kept < Flicks.Zero)
        {
            throw new CommandException("invalid-value", "How much of a pause to keep cannot be negative.", "keep");
        }

        (ClipLocation found, SoundingClip sound, IReadOnlyList<TimelineWord> heard) = Words(project, clipId, services, Fillers(words));
        Rational rate = project.SettingsFor(found.Sequence).FrameRate;
        return (found, ClipWords.CleanUp(heard, sound.Clip, rate, fillers, pauses, keep ?? DefaultKeep));
    }

    /// <summary>The words of some sounding clips as a reader reads them: a paragraph per clip, a line per sentence, each word after its index.</summary>
    internal static string Text(IReadOnlyList<(SoundingClip Sound, IReadOnlyList<TimelineWord> Words)> clips, Rational rate)
    {
        var text = new StringBuilder();
        foreach ((SoundingClip sound, IReadOnlyList<TimelineWord> words) in clips)
        {
            text.Append(CultureInfo.InvariantCulture, $"Clip {sound.Clip.Id} \"{sound.Clip.Name}\" on {sound.Track.Name}, {Timecode.Format(sound.Clip.Start, rate)} to {Timecode.Format(sound.Clip.End, rate)}:").Append('\n');
            bool lineStarted = false;
            for (int index = 0; index < words.Count; index++)
            {
                TimelineWord word = words[index];
                if (index > 0 && word.Start - words[index - 1].End >= ShownPause)
                {
                    if (lineStarted)
                    {
                        text.Append('\n');
                    }

                    text.Append(CultureInfo.InvariantCulture, $"(pause {(word.Start - words[index - 1].End).ToSeconds():0.0} s)").Append('\n');
                    lineStarted = false;
                }

                if (!lineStarted)
                {
                    text.Append(Timecode.Format(word.Start, rate)).Append("  ");
                    lineStarted = true;
                }
                else
                {
                    text.Append(' ');
                }

                text.Append(CultureInfo.InvariantCulture, $"[{word.Index}]{word.Text.Trim()}");
                string said = word.Text.TrimEnd();
                if (said.EndsWith('.') || said.EndsWith('?') || said.EndsWith('!'))
                {
                    text.Append('\n');
                    lineStarted = false;
                }
            }

            if (lineStarted)
            {
                text.Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>Transcribes a media item, a clip's file, or a sequence's sounding clips.</summary>
public sealed class TranscribeHandler : ICommandHandler<TranscribeCommand>, IPreparingHandler<TranscribeCommand>
{
    /// <summary>Hears the speech before the command is queued, so edits go on meanwhile: the media items heard.</summary>
    public object? Prepare(Project project, TranscribeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return Hear(project, command, context);
    }

    /// <inheritdoc />
    public Project Handle(Project project, TranscribeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        context.Changed(context.Prepared as List<string> ?? Hear(project, command, context));
        return project;
    }

    /// <summary>Every sound the command names, transcribed or read from the cache; the media items' ids.</summary>
    private static List<string> Hear(Project project, TranscribeCommand command, HandlerContext context)
    {
        var heard = new List<string>();

        string? language = command.Language.Trim().ToLowerInvariant() is "auto" or "" ? null : command.Language.Trim().ToLowerInvariant();
        var targets = new List<(MediaItem Item, int Stream)>();
        if (command.TargetId is { Length: > 0 } id && project.MediaItem(id) is { } media)
        {
            int stream = command.Stream ?? SpeechHelp.FirstSound(media)
                ?? throw new CommandException("no-sound", $"'{media.Name}' has no sound to transcribe.");
            if (media.Info?.Streams.FirstOrDefault(candidate => candidate.Index == stream) is { Kind: not MediaStreamKind.Audio })
            {
                throw new CommandException("not-sound", $"Stream {stream} of '{media.Name}' is not sound.", "stream");
            }

            targets.Add((media, stream));
        }
        else if (command.TargetId is { Length: > 0 } clipId && project.FindClip(clipId) is { } found)
        {
            SoundingClip sound = SpeechHelp.SoundOf(project, found)
                ?? throw new CommandException("no-sound", $"'{found.Clip.Name}' plays no sound, so there is nothing to transcribe.");
            targets.Add((sound.Item, sound.Stream));
        }
        else
        {
            Sequence sequence = HandlerHelp.Sequence(project, command.TargetId);
            targets.AddRange(SpeechHelp.Sounding(project, sequence).Select(sound => (sound.Item, sound.Stream)).Distinct());
            if (targets.Count == 0)
            {
                throw new CommandException("no-sound", $"Nothing in '{sequence.Name}' plays sound from a file.");
            }
        }

        TranscriptionService service = SpeechHelp.Service(context.Services);
        foreach ((MediaItem item, int stream) in targets.Distinct())
        {
            string path = HandlerHelp.Resolve(context.ProjectPath, item.RelativePath);
            if (!command.Again && service.Cached(item.Hash, stream, ModelStore.Whisper.Name, language) is not null)
            {
                heard.Add(item.Id);
                continue;
            }

            if (!File.Exists(path))
            {
                throw new CommandException("media-missing", $"'{item.Name}' is not at {path}, so it cannot be heard. Relink it first.");
            }

            try
            {
                service.TranscribeAsync(item, path, stream, language, command.Again, cancellationToken: context.Cancellation).GetAwaiter().GetResult();
            }
            catch (FileNotFoundException) when (!ModelStore.IsPresent(ModelStore.Whisper))
            {
                ModelFile model = ModelStore.Whisper;
                throw new CommandException("model-missing", string.Create(CultureInfo.InvariantCulture, $"The speech model is not downloaded. `model.download {model.Name}` fetches it ({model.Bytes / 1e9:0.0} GB); ask first."), model.Name);
            }
            catch (Media.Interop.FfmpegException exception)
            {
                throw new CommandException("analysis-failed", $"The sound of '{item.Name}' could not be read: {exception.Message}");
            }

            heard.Add(item.Id);
        }

        return heard;
    }
}

/// <summary>Answers <c>speech.transcript</c>.</summary>
public sealed class TranscriptHandler : IQueryHandler<TranscriptQuery, TranscriptInfo>
{
    /// <inheritdoc />
    public TranscriptInfo Handle(Project project, TranscriptQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        List<SoundingClip> clips;
        Sequence sequence;
        if (query.TargetId is { Length: > 0 } id && project.FindClip(id) is { } found)
        {
            sequence = found.Sequence;
            clips = SpeechHelp.SoundOf(project, found) is { } sound
                ? [sound]
                : throw new CommandException("no-sound", $"'{found.Clip.Name}' plays no sound, so nothing is said in it.");
        }
        else
        {
            sequence = HandlerHelp.Sequence(project, query.TargetId);
            clips = SpeechHelp.Sounding(project, sequence);
        }

        Rational rate = project.SettingsFor(sequence).FrameRate;
        var heard = new List<(SoundingClip Sound, IReadOnlyList<TimelineWord> Words)>();
        var untranscribed = new List<string>();
        string language = string.Empty;
        foreach (SoundingClip sound in clips)
        {
            if (SpeechHelp.TranscriptOf(context.Services, sound) is { } transcript)
            {
                heard.Add((sound, ClipWords.Of(sound.Clip, transcript)));
                language = language.Length == 0 ? transcript.Language : language;
            }
            else
            {
                untranscribed.Add(sound.Clip.Id);
            }
        }

        WordInfo[] words = [.. heard
            .SelectMany(clip => clip.Words)
            .OrderBy(word => word.Start)
            .Select(word => new WordInfo(word.Index, word.ClipId, word.Text.Trim(), word.Start, word.End, Timecode.Format(word.Start, rate), Math.Round(word.Confidence, 3), word.Filler))];
        return new TranscriptInfo(words, SpeechHelp.Text(heard, rate), language, [.. untranscribed]);
    }
}

/// <summary>Cuts words out of a clip.</summary>
public sealed class RemoveWordsHandler : ICommandHandler<RemoveWordsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveWordsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, SoundingClip sound, IReadOnlyList<TimelineWord> words) = SpeechHelp.Words(project, command.ClipId, context.Services);
        int to = command.To ?? command.From;
        if (words.Count == 0)
        {
            throw new CommandException("no-words", $"Nothing is said in '{found.Clip.Name}'.");
        }

        if (command.From < 0 || command.From >= words.Count)
        {
            throw new CommandException("word-out-of-range", $"The clip has words 0 to {words.Count - 1}.", "from");
        }

        if (to < command.From || to >= words.Count)
        {
            throw new CommandException("word-out-of-range", $"The last word has to be from {command.From} to {words.Count - 1}.", "to");
        }

        ClipLocation where = project.FindClip(sound.Clip.Id)!;
        (Flicks from, Flicks until) = ClipWords.Stretch(words, command.From, to, sound.Clip, project.SettingsFor(found.Sequence).FrameRate);
        return SpeechHelp.Cut(project, where, [(from, until)], context);
    }
}

/// <summary>Cuts the filler words and long pauses out of a clip.</summary>
public sealed class RemoveFillersHandler : ICommandHandler<RemoveFillersCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveFillersCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, IReadOnlyList<SpeechCut> cuts) = SpeechHelp.CleanUp(project, command.ClipId, command.Words, command.Pauses, command.Keep, command.Fillers, context.Services);
        if (cuts.Count == 0)
        {
            return project;
        }

        SoundingClip sound = SpeechHelp.SoundOf(project, found)!;
        return SpeechHelp.Cut(project, project.FindClip(sound.Clip.Id)!, cuts.Select(cut => (cut.From, cut.To)), context);
    }
}

/// <summary>Answers <c>clip.find-fillers</c>.</summary>
public sealed class FindFillersHandler : IQueryHandler<FindFillersQuery, SpeechCutInfo[]>
{
    /// <inheritdoc />
    public SpeechCutInfo[] Handle(Project project, FindFillersQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        (ClipLocation found, IReadOnlyList<SpeechCut> cuts) = SpeechHelp.CleanUp(project, query.ClipId, query.Words, query.Pauses, query.Keep, query.Fillers, context.Services);
        Rational rate = project.SettingsFor(found.Sequence).FrameRate;
        return [.. cuts.Select(cut => new SpeechCutInfo(cut.From, cut.To, Timecode.Format(cut.From, rate), cut.Reason, cut.Words))];
    }
}

/// <summary>Makes captions from the transcript.</summary>
public sealed class SubtitleFromTranscriptHandler : ICommandHandler<SubtitleFromTranscriptCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SubtitleFromTranscriptCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = SubtitleHelp.Track(project, command.TrackId);
        CueRules rules = CueRules.For(track.SubtitleStyle);
        rules = rules with
        {
            MaxChars = command.MaxChars ?? rules.MaxChars,
            MaxLines = command.MaxLines ?? rules.MaxLines,
            MinGapFrames = command.MinGapFrames,
        };

        if (rules.MaxChars < 1 || rules.MaxLines < 1 || rules.MinGapFrames < 0)
        {
            throw new CommandException("value-out-of-range", "A line needs at least one character, a cue at least one line, and the gap cannot be negative.");
        }

        var words = new List<TimelineWord>();
        if (command.ClipId is { Length: > 0 } clipId)
        {
            (ClipLocation found, _, IReadOnlyList<TimelineWord> heard) = SpeechHelp.Words(project, clipId, context.Services);
            if (found.Sequence.Id != sequence.Id)
            {
                throw new CommandException("wrong-sequence", "The clip and the subtitle track are in different sequences.");
            }

            words.AddRange(heard);
        }
        else
        {
            foreach (SoundingClip sound in SpeechHelp.Sounding(project, sequence))
            {
                if (SpeechHelp.TranscriptOf(context.Services, sound) is { } transcript)
                {
                    words.AddRange(ClipWords.Of(sound.Clip, transcript));
                }
            }
        }

        if (words.Count == 0)
        {
            throw new CommandException("no-transcript", "Nothing transcribed is said there. `speech.transcribe` hears it first.");
        }

        Rational rate = project.SettingsFor(sequence).FrameRate;
        IReadOnlyList<(Flicks Start, Flicks End, string Text)> laid = CaptionLayout.Cues(
            [.. words.OrderBy(word => word.Start).Select(word => (word.Text, word.Start, word.End))],
            rules,
            rate);

        // What was on the track over the same stretch gives way.
        Flicks first = laid[0].Start;
        Flicks last = laid[^1].End;
        var kept = track.Clips.Where(clip => clip.End <= first || clip.Start >= last).ToList();
        foreach (Clip gone in track.Clips.Except(kept))
        {
            context.Changed(gone.Id);
        }

        foreach ((Flicks start, Flicks end, string text) in laid)
        {
            string markup = string.Join('\n', text.Split('\n').Select(TitleMarkup.Escape));
            Clip cue = SubtitleTracks.Clip(Id.New(), start, end - start, new Cue(markup));
            kept.Add(cue);
            context.Changed(cue.Id);
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(SubtitleHelp.Sorted(track, kept));
    }
}

/// <summary>Answers <c>subtitle.check</c>.</summary>
public sealed class CheckCuesHandler : IQueryHandler<CheckCuesQuery, CueProblemInfo[]>
{
    /// <inheritdoc />
    public CueProblemInfo[] Handle(Project project, CheckCuesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        (Sequence sequence, Track track) = SubtitleHelp.Track(project, query.TrackId, editing: false);
        Rational rate = project.SettingsFor(sequence).FrameRate;
        CueRules rules = CueRules.For(track.SubtitleStyle) with { MaxCharsPerSecond = query.MaxCharsPerSecond };
        return [.. CueChecks.Check(track, rate, rules).Select(problem => new CueProblemInfo(problem.CueId, problem.Time, Timecode.Format(problem.Time, rate), problem.Code, problem.Message))];
    }
}

/// <summary>Answers <c>model.list</c>.</summary>
public sealed class ListModelsHandler : IQueryHandler<ListModelsQuery, ModelInfo[]>
{
    /// <inheritdoc />
    public ModelInfo[] Handle(Project project, ListModelsQuery query, QueryContext context) =>
        [.. ModelStore.All.Select(model => new ModelInfo(model.Name, model.Purpose, model.Bytes, model.License, ModelStore.IsPresent(model), ModelStore.PathOf(model)))];
}

/// <summary>Fetches a model.</summary>
public sealed class DownloadModelHandler : ICommandHandler<DownloadModelCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DownloadModelCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ModelFile model = ModelStore.Find(command.Name)
            ?? throw new CommandException("unknown-model", $"There is no model '{command.Name}'. `model.list` names them.");
        if (ModelStore.IsPresent(model) && !command.Force)
        {
            return project;
        }

        try
        {
            ModelStore.DownloadAsync(model, cancellationToken: context.Cancellation).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or InvalidDataException or IOException)
        {
            throw new CommandException("download-failed", $"{model.Name} could not be fetched: {exception.Message}");
        }

        return project;
    }
}
