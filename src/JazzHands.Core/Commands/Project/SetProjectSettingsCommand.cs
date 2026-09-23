using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes the project's default frame rate, size and audio format.</summary>
/// <remarks>
/// Only the members given are changed, so setting the frame rate alone leaves the size as it was.
/// Sequences with their own settings are unaffected.
/// </remarks>
/// <param name="Fps">The frame rate.</param>
/// <param name="Size">The frame size.</param>
/// <param name="SampleRate">The audio sample rate.</param>
/// <param name="ChannelCount">The audio channel count.</param>
/// <param name="ColorSpace">The working colour space, for example bt709.</param>
[Command("project.set-settings", Description = "Change the project frame rate, size or audio format")]
public sealed record SetProjectSettingsCommand(
    [property: Option("fps", "Frame rate, for example 30 or 30000/1001")] Rational? Fps = null,
    [property: Option("size", "Frame size, for example 1920x1080 or 4k")] FrameSize? Size = null,
    [property: Option("sample-rate", "Audio sample rate")] int? SampleRate = null,
    [property: Option("channels", "Audio channel count")] int? ChannelCount = null,
    [property: Option("color-space", "Working colour space")] string? ColorSpace = null) : ICommand;
