namespace JazzHands.Core.Commands;

/// <summary>Makes a clip a 3D layer, or flat again (Phase 47).</summary>
/// <remarks>
/// A 3D layer gains depth (<c>transform.z</c>), a turn about X and Y (<c>transform.rotation-x</c>,
/// <c>transform.rotation-y</c>) and a material (<c>material.*</c>), all set and keyframed with
/// <c>param.set</c> and <c>keyframe.*</c>. 3D layers next to each other in the stack are seen
/// together through the sequence's camera and lit by its lights. Made 3D, a clip does not move:
/// at depth 0 with no turn the default camera sees it where it was. <c>--off</c> makes it flat
/// again and drops its depth, turns and material. The switches change only what they name.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Off">Make it flat again.</param>
/// <param name="Lights">Lit by the scene's lights; false shows the picture as it is.</param>
/// <param name="CastsShadows">Throws shadows from lights that cast them.</param>
/// <param name="AcceptsShadows">Darkened by shadows other layers throw.</param>
[Command("clip.set-3d", Description = "Make a clip a 3D layer, or flat again")]
public sealed record SetClip3DCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("off", "Make it flat again, dropping its depth, turns and material")] bool Off = false,
    [property: Option("lights", "true to be lit by the scene's lights, false to show the picture as it is")] bool? Lights = null,
    [property: Option("casts-shadows", "true to throw shadows from lights that cast them")] bool? CastsShadows = null,
    [property: Option("accepts-shadows", "true to be darkened by shadows other layers throw")] bool? AcceptsShadows = null) : ICommand;
