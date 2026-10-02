using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a 3D light on the timeline (Phase 47).</summary>
/// <remarks>
/// A light is a <c>3d.light</c> clip; it draws nothing. While it is under the playhead it lights
/// the sequence's 3D layers that take lights. Once a scene has a light, a layer is lit only by the
/// lights there are, so a scene without an ambient light has black shadows. Every option is one of
/// its parameters, which <c>param.set</c> and <c>keyframe.*</c> change and animate later. Without a
/// track it goes on the lowest video track free for its length above every picture, or on a new
/// track on top.
/// </remarks>
/// <param name="Kind">point, spot, directional, ambient or environment.</param>
/// <param name="At">Where it starts; the timeline's start when left out.</param>
/// <param name="Duration">How long it lasts; to the end of the sequence (at least ten seconds) when left out.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Intensity">How bright; 1 lights a white layer facing it to full brightness.</param>
/// <param name="Position">Where it is across the frame, as 'x, y'.</param>
/// <param name="Z">Its depth; negative is in front of the frame.</param>
/// <param name="Target">What a spot or directional light points at, as 'x, y'.</param>
/// <param name="TargetZ">The depth of what it points at.</param>
/// <param name="Cone">A spot light's cone, in degrees.</param>
/// <param name="Feather">How soft a spot light's edge is, in percent.</param>
/// <param name="Falloff">none, smooth or inverse-square.</param>
/// <param name="Radius">How far a point or spot light reaches at full strength.</param>
/// <param name="Shadows">Layers that cast shadows throw them from this light.</param>
/// <param name="Softness">How soft its shadows' edges are, in sequence pixels.</param>
/// <param name="Darkness">How much of the light a shadow takes away, 0 to 1.</param>
/// <param name="Image">For an environment light: the media item, by id, whose still surrounds the scene.</param>
/// <param name="Name">Its display name.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named.</param>
[Command("light.add", Description = "Put a 3D light on the timeline")]
public sealed record AddLightCommand(
    [property: Option("kind", "point, spot, directional, ambient or environment")] string Kind = "point",
    [property: Option("at", "Where it starts; the timeline's start when left out")] Flicks? At = null,
    [property: Option("dur", "How long it lasts; to the end of the sequence when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the lowest free one above every picture when left out")] string? TrackId = null,
    [property: Option("color", "Its colour, for example #FFE8C0")] string? Color = null,
    [property: Option("intensity", "How bright; 1 lights a white layer facing it to full brightness")] double? Intensity = null,
    [property: Option("position", "Where it is across the frame, as 'x, y' in sequence pixels from the centre")] string? Position = null,
    [property: Option("z", "Its depth in sequence pixels; negative is in front of the frame, towards the camera")] double? Z = null,
    [property: Option("target", "What a spot or directional light points at, as 'x, y'")] string? Target = null,
    [property: Option("target-z", "The depth of what it points at")] double? TargetZ = null,
    [property: Option("cone", "A spot light's cone, in degrees")] double? Cone = null,
    [property: Option("feather", "How soft a spot light's edge is, in percent")] double? Feather = null,
    [property: Option("falloff", "none, smooth or inverse-square")] string? Falloff = null,
    [property: Option("radius", "How far a point or spot light reaches at full strength, in sequence pixels")] double? Radius = null,
    [property: Option("shadows", "Layers that cast shadows throw them from this light")] bool Shadows = false,
    [property: Option("softness", "How soft its shadows' edges are, in sequence pixels")] double? Softness = null,
    [property: Option("darkness", "How much of the light a shadow takes away, 0 to 1")] double? Darkness = null,
    [property: Option("image", "For an environment light: the media item, by id, whose still surrounds the scene and is reflected")] string? Image = null,
    [property: Option("name", "Its display name")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;
