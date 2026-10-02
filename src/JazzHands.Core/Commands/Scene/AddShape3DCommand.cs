using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a 3D shape on the timeline (Phase 48).</summary>
/// <remarks>
/// A <c>3d.shape</c> clip: a cube, sphere, cylinder, cone, torus or plane as solid geometry, always
/// in 3D, lit, shadowed and seen through the scene's camera. It moves, turns and keyframes like a 3D
/// layer; every option is one of its parameters. Without a track it goes on the lowest video track
/// free for its length above every picture, or on a new track on top.
/// </remarks>
/// <param name="Kind">cube, sphere, cylinder, cone, torus or plane.</param>
/// <param name="At">Where it starts; the timeline's start when left out.</param>
/// <param name="Duration">How long it lasts; to the end of the sequence (at least ten seconds) when left out.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Size">Width and height, as 'w, h' in sequence pixels.</param>
/// <param name="Depth">Front to back, in sequence pixels.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Metallic">0 painted, 1 metal.</param>
/// <param name="Roughness">0 polished, 1 matte.</param>
/// <param name="Name">Its display name.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named.</param>
[Command("shape3d.add", Description = "Put a 3D shape on the timeline")]
public sealed record AddShape3DCommand(
    [property: Option("kind", "cube, sphere, cylinder, cone, torus or plane")] string Kind = "cube",
    [property: Option("at", "Where it starts; the timeline's start when left out")] Flicks? At = null,
    [property: Option("dur", "How long it lasts; to the end of the sequence when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the lowest free one above every picture when left out")] string? TrackId = null,
    [property: Option("size", "Width and height, as 'w, h' in sequence pixels")] string? Size = null,
    [property: Option("depth", "Front to back, in sequence pixels")] double? Depth = null,
    [property: Option("color", "Its colour, for example #3080FF")] string? Color = null,
    [property: Option("metallic", "0 painted, 1 metal")] double? Metallic = null,
    [property: Option("roughness", "0 polished, 1 matte")] double? Roughness = null,
    [property: Option("name", "Its display name")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;
