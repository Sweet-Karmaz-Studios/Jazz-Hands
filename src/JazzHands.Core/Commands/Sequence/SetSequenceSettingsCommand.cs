using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Gives a sequence its own frame rate, size or audio format.</summary>
/// <remarks>
/// A sequence with no settings of its own uses the project's. Passing <c>inherit</c> puts it back
/// to that, which is different from setting it to the same values: the sequence then follows the
/// project when the project changes.
/// </remarks>
/// <param name="SequenceId">Which sequence.</param>
/// <param name="Fps">The frame rate.</param>
/// <param name="Size">The frame size.</param>
/// <param name="SampleRate">The audio sample rate.</param>
/// <param name="ChannelCount">The audio channel count.</param>
/// <param name="ColorSpace">The working colour space.</param>
/// <param name="Inherit">Drop the override and follow the project.</param>
[Command("sequence.set-settings", Description = "Give a sequence its own frame rate, size or audio format")]
public sealed record SetSequenceSettingsCommand(
    [property: Arg(0, "The sequence id")] string SequenceId,
    [property: Option("fps", "Frame rate, for example 30 or 30000/1001")] Rational? Fps = null,
    [property: Option("size", "Frame size, for example 1920x1080 or 4k")] FrameSize? Size = null,
    [property: Option("sample-rate", "Audio sample rate")] int? SampleRate = null,
    [property: Option("channels", "Audio channel count")] int? ChannelCount = null,
    [property: Option("color-space", "Working colour space")] string? ColorSpace = null,
    [property: Option("inherit", "Drop the override and follow the project")] bool Inherit = false) : ICommand;
