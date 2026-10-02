using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a 3D camera on the timeline (Phase 47).</summary>
/// <remarks>
/// A camera is a <c>3d.camera</c> clip; it draws nothing. While it is under the playhead the
/// sequence's 3D layers are seen through it (the topmost camera when several are). At rest it sees
/// the frame exactly as it is; every option is one of its parameters, which <c>param.set</c> and
/// <c>keyframe.*</c> change and animate later. Without a track it goes on the lowest video track
/// free for its length above every picture, or on a new track on top.
/// </remarks>
/// <param name="At">Where it starts; the timeline's start when left out.</param>
/// <param name="Duration">How long it lasts; to the end of the sequence (at least ten seconds) when left out.</param>
/// <param name="TrackId">Which video track.</param>
/// <param name="Dolly">How far it moves in from rest, with its point of interest, in sequence pixels.</param>
/// <param name="Orbit">Degrees round the point of interest, left and right.</param>
/// <param name="Tilt">Degrees round the point of interest, up and down.</param>
/// <param name="Roll">Degrees about the way it looks.</param>
/// <param name="Angle">Angle of view across the frame, in degrees.</param>
/// <param name="Position">Where it is across the frame, as 'x, y' in sequence pixels from the centre.</param>
/// <param name="Target">Its point of interest across the frame, as 'x, y'.</param>
/// <param name="TargetZ">The depth of its point of interest.</param>
/// <param name="DepthOfField">Blur what is out of focus.</param>
/// <param name="Focus">Focus distance in sequence pixels; the point of interest when left out.</param>
/// <param name="Aperture">The blur, in pixels, of something infinitely far.</param>
/// <param name="Name">Its display name.</param>
/// <param name="ClipId">The identifier to give it.</param>
/// <param name="SequenceId">Which sequence, when no track is named.</param>
[Command("camera.add", Description = "Put a 3D camera on the timeline")]
public sealed record AddCameraCommand(
    [property: Option("at", "Where it starts; the timeline's start when left out")] Flicks? At = null,
    [property: Option("dur", "How long it lasts; to the end of the sequence when left out")] Flicks? Duration = null,
    [property: Option("track", "Which video track; the lowest free one above every picture when left out")] string? TrackId = null,
    [property: Option("dolly", "How far it moves in from rest, with its point of interest, in sequence pixels; negative pulls back")] double? Dolly = null,
    [property: Option("orbit", "Degrees round the point of interest, left and right")] double? Orbit = null,
    [property: Option("tilt", "Degrees round the point of interest, up (positive) and down")] double? Tilt = null,
    [property: Option("roll", "Degrees about the way it looks, clockwise")] double? Roll = null,
    [property: Option("angle", "Angle of view across the frame in degrees; 39.6 is a 50 mm lens")] double? Angle = null,
    [property: Option("position", "Where it is across the frame, as 'x, y' in sequence pixels from the centre")] string? Position = null,
    [property: Option("target", "Its point of interest across the frame, as 'x, y'")] string? Target = null,
    [property: Option("target-z", "The depth of its point of interest, in sequence pixels")] double? TargetZ = null,
    [property: Option("dof", "Blur what is nearer or farther than the focus distance")] bool DepthOfField = false,
    [property: Option("focus", "Focus distance in sequence pixels; the point of interest when left out")] double? Focus = null,
    [property: Option("aperture", "The blur, in pixels, of something infinitely far; 20 when left out")] double? Aperture = null,
    [property: Option("name", "Its display name")] string? Name = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("sequence", "Which sequence, when no track is named")] string? SequenceId = null) : ICommand;
