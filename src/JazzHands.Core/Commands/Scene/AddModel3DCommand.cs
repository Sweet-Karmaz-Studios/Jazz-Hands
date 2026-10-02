using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a glTF 2.0 model on the timeline (Phase 48).</summary>
/// <remarks>
/// A <c>3d.model</c> clip: the model's meshes with their own materials and textures, always in 3D,
/// centred on the clip's position, sized so its largest side is <paramref name="Size"/>, lit,
/// shadowed and seen through the scene's camera. Its animation plays over the clip. The file is read
/// first and refused with <c>model-unreadable</c> when it cannot be drawn; what this reader leaves
/// out (skins, morph targets, Draco) is reported by <c>model3d.info</c> rather than refused. The path
/// is kept relative to the project. Without a track it goes on the lowest video track free for its
/// length above every picture, or on a new track on top.
/// </remarks>
/// <param name="File">The .gltf or .glb file.</param>
/// <param name="At">Where it starts; the timeline's start when left out.</param>
/// <param name="Duration">How long it lasts; its animation's length, or to the end of the sequence (at least ten seconds), when left out.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Size">Its largest side, in sequence pixels.</param>
/// <param name="Animation">Which animation plays, by name or number; the first when left out.</param>
/// <param name="Name">Its display name; the file's when left out.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named.</param>
[Command("model3d.add", Description = "Put a glTF 3D model on the timeline")]
public sealed record AddModel3DCommand(
    [property: Arg(0, "The .gltf or .glb file")] string File,
    [property: Option("at", "Where it starts; the timeline's start when left out")] Flicks? At = null,
    [property: Option("dur", "How long it lasts; its animation's length or to the end of the sequence when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the lowest free one above every picture when left out")] string? TrackId = null,
    [property: Option("size", "Its largest side, in sequence pixels; 400 when left out")] double? Size = null,
    [property: Option("animation", "Which of its animations plays, by name or number from 0; the first when left out")] string? Animation = null,
    [property: Option("name", "Its display name; the file's when left out")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;
