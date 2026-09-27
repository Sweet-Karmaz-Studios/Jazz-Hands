using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>A word said, at its timeline time.</summary>
/// <param name="Index">Its index among its clip's words: what <c>clip.remove-words</c> takes.</param>
/// <param name="ClipId">The clip it is in.</param>
/// <param name="Text">The word as written, with its punctuation.</param>
/// <param name="Start">When it begins on the timeline.</param>
/// <param name="End">When it ends on the timeline.</param>
/// <param name="Timecode">Its start as timecode.</param>
/// <param name="Confidence">How sure the model was, 0 to 1.</param>
/// <param name="Filler">True for a filler word.</param>
public sealed record WordInfo(int Index, string ClipId, string Text, Flicks Start, Flicks End, string Timecode, double Confidence, bool Filler);

/// <summary>What was said in a clip or sequence.</summary>
/// <param name="Words">Every word, in timeline order.</param>
/// <param name="Text">The same as a reader reads it: a paragraph per clip and sentence, each word after its index in brackets, long pauses shown.</param>
/// <param name="Language">The language it was heard as.</param>
/// <param name="Untranscribed">Sounding clips with no transcript yet (<c>speech.transcribe</c> makes them).</param>
public sealed record TranscriptInfo(WordInfo[] Words, string Text, string Language, string[] Untranscribed);

/// <summary>A stretch a clean-up would cut.</summary>
/// <param name="From">Where it starts on the timeline, on a frame.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Timecode">Its start as timecode.</param>
/// <param name="Reason"><c>filler</c> or <c>pause</c>.</param>
/// <param name="Words">The words it cuts.</param>
public sealed record SpeechCutInfo(Flicks From, Flicks To, string Timecode, string Reason, string Words);

/// <summary>A machine learning model.</summary>
/// <param name="Name">Its name, for <c>model.download</c>.</param>
/// <param name="Purpose">What it is for.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="License">Its licence.</param>
/// <param name="Present">True when it is downloaded.</param>
/// <param name="Path">Where it is, or would be.</param>
public sealed record ModelInfo(string Name, string Purpose, long Bytes, string License, bool Present, string Path);

/// <summary>A subtitle cue that is hard to read.</summary>
/// <param name="CueId">The cue.</param>
/// <param name="Time">When it starts.</param>
/// <param name="Timecode">The same, as timecode.</param>
/// <param name="Code">What is wrong: <c>line-too-long</c>, <c>too-many-lines</c>, <c>reads-too-fast</c>, <c>too-short</c>, <c>too-long</c> or <c>gap-too-small</c>.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public sealed record CueProblemInfo(string CueId, Flicks Time, string Timecode, string Code, string Message);
