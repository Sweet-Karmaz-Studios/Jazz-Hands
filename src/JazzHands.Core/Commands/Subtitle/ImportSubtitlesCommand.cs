using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Brings subtitles in from a file or from a media item's embedded stream, as cues on a subtitle track.</summary>
/// <remarks>
/// SubRip, WebVTT and ASS files, or a text subtitle stream inside a Matroska or MP4 file already in
/// the project. Without a track, a new subtitle track is made on top of the others with the file's
/// style (an ASS file's Default style, a WebVTT stylesheet) and the stream's language; with one,
/// the cues join those already there. Cues with no length are left out. <c>subtitle.read</c> shows
/// what a file holds, and anything that will not come across, before it is imported.
/// </remarks>
/// <param name="File">A subtitle file: .srt, .vtt, .ass or .ssa.</param>
/// <param name="MediaId">Or a media item whose file carries subtitle streams.</param>
/// <param name="Stream">Which of its streams; the first subtitle stream when not given.</param>
/// <param name="TrackId">A subtitle track to add the cues to; a new one when not given.</param>
/// <param name="Language">The track's language, an ISO 639-2 code such as eng; the stream's when not given.</param>
/// <param name="Offset">Moves every cue by this much, for a file timed against something else.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="NewTrackId">The identifier to give a new track.</param>
[Command("subtitle.import", Description = "Import subtitles from a file or an embedded stream")]
public sealed record ImportSubtitlesCommand(
    [property: Arg(0, "A .srt, .vtt, .ass or .ssa file")] string? File = null,
    [property: Option("media", "Or a media item with subtitle streams")] string? MediaId = null,
    [property: Option("stream", "Which of its streams")] int? Stream = null,
    [property: Option("track", "A subtitle track to add to")] string? TrackId = null,
    [property: Option("language", "The language, such as eng")] string? Language = null,
    [property: Option("offset", "Move every cue by this much")] Flicks? Offset = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("id", "The identifier for a new track")] string? NewTrackId = null) : ICommand;
