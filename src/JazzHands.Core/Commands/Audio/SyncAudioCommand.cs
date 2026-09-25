using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Moves a clip so its sound lines up with another clip's: a separate microphone with the
/// camera's sound, two cameras of one take.
/// </summary>
/// <remarks>
/// Finds the offset as <c>audio.sync-offset</c> does, from the sound of both clips where they are
/// on the timeline, and moves <c>--clip</c> (with the clips linked to it) by it, leaving
/// <c>--to</c> where it is. A video clip is heard through its linked sound. Both play forwards at
/// normal speed. Refused when the match is unsure (under 0.3) unless <c>--force</c>; ambiguous
/// matches (a click track) are refused the same way. One undo.
/// </remarks>
/// <param name="ClipId">The clip to move.</param>
/// <param name="ToClipId">The clip to line it up with.</param>
/// <param name="Force">Move it even when the match is unsure.</param>
[Command("audio.sync", Description = "Move a clip so its sound lines up with another clip's: a separate microphone with the camera, by their waveforms")]
public sealed record SyncAudioCommand(
    [property: Option("clip", "The clip to move")] string ClipId,
    [property: Option("to", "The clip to line it up with")] string ToClipId,
    [property: Option("force", "Move it even when the match is unsure")] bool Force = false) : ICommand;

/// <summary>How far to move a clip so its sound lines up with another's, and how sure that is.</summary>
/// <remarks>
/// Compares the two clips' sound where they are on the timeline: the loudness over time for the
/// rough offset, then the waveforms themselves around it, to a fraction of a millisecond. Nothing
/// changes; <c>audio.sync</c> moves the clip.
/// </remarks>
/// <param name="ClipId">The clip that would move.</param>
/// <param name="ToClipId">The clip it lines up with.</param>
[Query("audio.sync-offset", Description = "How far to move a clip so its sound lines up with another clip's, and how sure the match is")]
public sealed record SyncOffsetQuery(
    [property: Option("clip", "The clip that would move")] string ClipId,
    [property: Option("to", "The clip it lines up with")] string ToClipId) : IQuery<SyncOffsetInfo>;

/// <summary>What <c>audio.sync-offset</c> found.</summary>
/// <param name="ClipId">The clip that would move.</param>
/// <param name="ToClipId">The clip it lines up with.</param>
/// <param name="Offset">How far to move it: negative is earlier.</param>
/// <param name="Start">Where it would start.</param>
/// <param name="Confidence">How alike the two sound once lined up, 0 to 1; over 0.5 is a sure match.</param>
/// <param name="Ambiguous">True when another offset matched nearly as well, as a steady rhythm does.</param>
public sealed record SyncOffsetInfo(string ClipId, string ToClipId, Flicks Offset, Flicks Start, double Confidence, bool Ambiguous);
