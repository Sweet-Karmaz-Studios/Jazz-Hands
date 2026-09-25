using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>A subtitle cue on a track.</summary>
/// <param name="Id">The cue (a clip id).</param>
/// <param name="Start">When it appears, on the sequence.</param>
/// <param name="End">When it goes.</param>
/// <param name="Text">What it says, as markup.</param>
/// <param name="Plain">The same with the formatting taken out.</param>
/// <param name="Align">Where it sits.</param>
public sealed record CueInfo(string Id, Flicks Start, Flicks End, string Text, string Plain, SubtitleAlign Align);

/// <summary>A cue in a file, before it is imported.</summary>
/// <param name="Start">When it appears, in the file's time.</param>
/// <param name="End">When it goes.</param>
/// <param name="Text">What it says, as markup.</param>
/// <param name="Align">Where it sits.</param>
public sealed record FileCueInfo(Flicks Start, Flicks End, string Text, SubtitleAlign Align);

/// <summary>What a subtitle file or stream holds.</summary>
/// <param name="Format">srt, vtt, ass, or the stream's codec.</param>
/// <param name="Cues">Its cues, in time order.</param>
/// <param name="Style">The style it declared, when it declared one.</param>
/// <param name="Language">The stream's language, for an embedded stream.</param>
/// <param name="Warnings">What will not come across.</param>
public sealed record SubtitleFileInfo(string Format, FileCueInfo[] Cues, SubtitleStyle? Style, string? Language, string[] Warnings);

/// <summary>A subtitle track written out.</summary>
/// <param name="Format">srt, vtt or ass.</param>
/// <param name="Cues">How many cues.</param>
/// <param name="Path">Where the file went, when it was written.</param>
/// <param name="Text">The file's text, when it was not written.</param>
public sealed record SubtitleExport(string Format, int Cues, string? Path, string? Text);

/// <summary>A chapter of a sequence.</summary>
/// <param name="Id">The chapter (a marker id).</param>
/// <param name="Start">Where it starts.</param>
/// <param name="End">Where it ends: the next chapter's start, or the end of the sequence.</param>
/// <param name="Name">Its title.</param>
public sealed record ChapterSummary(string Id, Flicks Start, Flicks End, string Name);
