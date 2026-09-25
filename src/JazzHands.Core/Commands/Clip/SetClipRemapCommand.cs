namespace JazzHands.Core.Commands;

/// <summary>Turns time remapping on for a clip and the clips linked to it, or off again.</summary>
/// <remarks>
/// On, the clip's speed becomes its <c>remap</c> parameter, starting at the speed it plays at now,
/// which the keyframe commands then shape (<c>keyframe.add &lt;clip&gt; remap --at ... --value 0.25</c>).
/// Off, it plays at its fixed speed again. The clip keeps its place and length on the timeline
/// either way; how much source it plays follows the curve.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Off">Turn it off instead.</param>
[Command("clip.set-remap", Description = "Turn time remapping (a speed curve) on or off for a clip")]
public sealed record SetClipRemapCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("off", "Turn it off instead")] bool Off = false) : ICommand;
