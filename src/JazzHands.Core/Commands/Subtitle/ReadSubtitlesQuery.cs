namespace JazzHands.Core.Commands;

/// <summary>What a subtitle file, or a media item's subtitle stream, holds, without importing it.</summary>
/// <remarks>Its cues, the style it declared, and what will not come across (ASS tags that are kept but not drawn, WebVTT regions).</remarks>
/// <param name="File">A .srt, .vtt, .ass or .ssa file.</param>
/// <param name="MediaId">Or a media item whose file carries subtitle streams.</param>
/// <param name="Stream">Which of its streams; the first subtitle stream when not given.</param>
[Query("subtitle.read", Description = "Show what a subtitle file or stream holds")]
public sealed record ReadSubtitlesQuery(
    [property: Arg(0, "A .srt, .vtt, .ass or .ssa file")] string? File = null,
    [property: Option("media", "Or a media item with subtitle streams")] string? MediaId = null,
    [property: Option("stream", "Which of its streams")] int? Stream = null) : IQuery<SubtitleFileInfo>;
