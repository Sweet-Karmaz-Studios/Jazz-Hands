using System.Text;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Titles;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Shows what a subtitle file or stream holds.</summary>
public sealed class ReadSubtitlesHandler : IQueryHandler<ReadSubtitlesQuery, SubtitleFileInfo>
{
    /// <inheritdoc />
    public SubtitleFileInfo Handle(Project project, ReadSubtitlesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        (SubtitleDocument document, string format, string? language, _) = SubtitleHelp.Read(
            project,
            query.File,
            query.MediaId,
            query.Stream,
            context.Session?.ProjectPath ?? string.Empty);

        return new SubtitleFileInfo(
            format,
            [.. document.Cues.Select(cue => new FileCueInfo(cue.Start, cue.End, cue.Text, cue.Align))],
            document.Style,
            language,
            [.. document.AllWarnings]);
    }
}

/// <summary>A subtitle track's cues.</summary>
public sealed class ListCuesHandler : IQueryHandler<ListCuesQuery, CueInfo[]>
{
    /// <inheritdoc />
    public CueInfo[] Handle(Project project, ListCuesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        (_, Track track) = SubtitleHelp.Track(project, query.TrackId, editing: false);
        return [.. track.Clips
            .Where(clip => clip.Cue is not null)
            .Select(clip => new CueInfo(clip.Id, clip.Start, clip.End, clip.Cue!.Text, TitleMarkup.PlainText(clip.Cue.Text), clip.Cue.Align))];
    }
}

/// <summary>Writes a subtitle track out as a file.</summary>
public sealed class ExportSubtitlesHandler : IQueryHandler<ExportSubtitlesQuery, SubtitleExport>
{
    /// <inheritdoc />
    public SubtitleExport Handle(Project project, ExportSubtitlesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = SubtitleHelp.Track(project, query.TrackId, editing: false);
        SubtitleFormat format = query.Format
            ?? (query.Output is { Length: > 0 } output ? SubtitleFiles.FormatOf(output) : null)
            ?? SubtitleFormat.Srt;
        SubtitleDocument document = SubtitleTracks.Document(track);
        string text = SubtitleFiles.Write(document, format);
        string name = format.ToString().ToLowerInvariant();

        if (query.Output is not { Length: > 0 } target)
        {
            return new SubtitleExport(name, document.Cues.Length, null, text);
        }

        string path = HandlerHelp.Resolve(context.Session?.ProjectPath ?? string.Empty, target);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CommandException("cannot-write", $"Could not write '{path}': {error.Message}");
        }

        return new SubtitleExport(name, document.Cues.Length, path, null);
    }
}
