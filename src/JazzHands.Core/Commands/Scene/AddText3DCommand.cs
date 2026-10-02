using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts 3D text on the timeline (Phase 48).</summary>
/// <remarks>
/// A <c>3d.text</c> clip: the text extruded and bevelled into solid geometry, always in 3D, lit by
/// the scene's lights, throwing shadows, and seen through its camera. It moves, turns and keyframes
/// like a 3D layer (<c>transform.*</c>); every option is one of its parameters, which
/// <c>param.set</c> and <c>keyframe.*</c> change later. Without a track it goes on the lowest video
/// track free for its length above every picture, or on a new track on top.
/// </remarks>
/// <param name="Text">What it says; \n starts a line.</param>
/// <param name="At">Where it starts; the timeline's start when left out.</param>
/// <param name="Duration">How long it lasts; to the end of the sequence (at least ten seconds) when left out.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Font">The font family.</param>
/// <param name="Weight">thin, light, regular, medium, semibold, bold or black.</param>
/// <param name="Size">Letter height in sequence pixels.</param>
/// <param name="Depth">How deep the letters are.</param>
/// <param name="Bevel">The rounded edge's width.</param>
/// <param name="Color">The faces and the bevel.</param>
/// <param name="SideColor">The sides.</param>
/// <param name="Metallic">0 painted, 1 metal.</param>
/// <param name="Roughness">0 polished, 1 matte.</param>
/// <param name="Name">Its display name; its text when left out.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named.</param>
[Command("text3d.add", Description = "Put extruded, bevelled 3D text on the timeline")]
public sealed record AddText3DCommand(
    [property: Option("text", "What it says; \\n starts a line")] string Text = "JAZZ",
    [property: Option("at", "Where it starts; the timeline's start when left out")] Flicks? At = null,
    [property: Option("dur", "How long it lasts; to the end of the sequence when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the lowest free one above every picture when left out")] string? TrackId = null,
    [property: Option("font", "The font family, as fonts list shows them")] string? Font = null,
    [property: Option("weight", "thin, light, regular, medium, semibold, bold or black")] string? Weight = null,
    [property: Option("size", "Letter height in sequence pixels")] double? Size = null,
    [property: Option("depth", "How deep the letters are, front to back, in sequence pixels")] double? Depth = null,
    [property: Option("bevel", "The rounded edge round the faces, in sequence pixels; 0 for none")] double? Bevel = null,
    [property: Option("color", "The faces and the bevel, for example #FFC040")] string? Color = null,
    [property: Option("side-color", "The sides, for example #404858")] string? SideColor = null,
    [property: Option("metallic", "0 painted, 1 metal")] double? Metallic = null,
    [property: Option("roughness", "0 polished, 1 matte")] double? Roughness = null,
    [property: Option("name", "Its display name; its text when left out")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;
