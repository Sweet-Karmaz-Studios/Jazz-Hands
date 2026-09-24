using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>The colour of the picture at a point and a time, averaged over a small square.</summary>
/// <remarks>
/// What an eyedropper reads. The point is in sequence pixels from the frame centre, as a point
/// parameter is. With <c>--before</c>, the frame is drawn with that effect and every one after it
/// in its chain switched off: a white balance picks its neutral from the picture it will correct,
/// not from its own result.
/// </remarks>
/// <param name="At">When, on the timeline.</param>
/// <param name="X">Across, in sequence pixels from the frame centre.</param>
/// <param name="Y">Down, in sequence pixels from the frame centre.</param>
/// <param name="BeforeEffectId">An effect to read the picture before, with it and those after it off.</param>
/// <param name="Size">The side of the square averaged, in sequence pixels, 1 to 64.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
[Query("color.sample", Description = "Read the picture's colour at a point")]
public sealed record SampleColorQuery(
    [property: Option("at", "When, on the timeline")] Flicks At,
    [property: Option("x", "Across, in sequence pixels from the frame centre")] double X = 0,
    [property: Option("y", "Down, in sequence pixels from the frame centre")] double Y = 0,
    [property: Option("before", "An effect to read the picture before")] string? BeforeEffectId = null,
    [property: Option("size", "The side of the square averaged, in pixels")] int Size = 5,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<ColorSample>;
