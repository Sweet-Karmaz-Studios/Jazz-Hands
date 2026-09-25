using JazzHands.Core.Subtitles;

namespace JazzHands.Core.Commands;

/// <summary>A subtitle track's cues as a SubRip, WebVTT or ASS file.</summary>
/// <remarks>
/// Changes nothing in the project. With <c>--out</c> the file is written (UTF-8) and its path
/// reported; without, the text comes back. The format is <c>--format</c>, or the output's
/// extension, or SubRip. Cues come out at their sequence times, in the track's style for ASS.
/// </remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="Format">srt, vtt or ass.</param>
/// <param name="Output">Where to write the file.</param>
[Query("subtitle.export", Description = "Write a subtitle track as SRT, VTT or ASS")]
public sealed record ExportSubtitlesQuery(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("format", "srt, vtt or ass")] SubtitleFormat? Format = null,
    [property: Option("out", "The file to write")] string? Output = null) : IQuery<SubtitleExport>;
