using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Saves an export preset of a person's own: a copy of another with changes, or one written out
/// in full as JSON.
/// </summary>
/// <remarks>
/// Saved to <c>%APPDATA%\JazzHands\export-presets</c>, where it is every project's. One with a
/// built-in's name replaces the built-in until it is deleted. The changes are the same ones
/// <c>jazz export</c> takes for a single export, so a set of overrides that worked once can be
/// kept under a name.
/// </remarks>
/// <param name="Name">What to call it, in kebab case.</param>
/// <param name="From">The preset to start from. Defaults to the default preset.</param>
/// <param name="Json">A whole preset as JSON, or the path of a .json file holding one, instead of starting from another.</param>
/// <param name="Label">What the export dialog calls it.</param>
/// <param name="Description">One line on what it is for.</param>
/// <param name="Size">Fit the picture inside this size, for example 1280x720.</param>
/// <param name="FrameRate">Write at most this rate.</param>
/// <param name="Quality">Constant quality: CRF or CQ, lower is better.</param>
/// <param name="Bitrate">A picture bitrate instead of constant quality: 8M, 2500k.</param>
/// <param name="Encoders">The encoders to try, in order.</param>
/// <param name="AudioEncoder">The sound encoder.</param>
/// <param name="AudioBitrate">The sound bitrate: 320k.</param>
/// <param name="Channels">1, 2 or 6 channels.</param>
/// <param name="Loudness">Normalise the mix to this many LUFS.</param>
/// <param name="TargetSize">Come in under this size: 8MB.</param>
/// <param name="PixelFormat">The pixel format to encode, for ten bits or 4:2:2.</param>
[Command("presets.save",
    Description = "Save an export preset of your own",
    Undoable = false,
    NotUndoableReason = "A preset is a file in your settings, not part of the project.",
    Standalone = true)]
public sealed record SavePresetCommand(
    [property: Arg(0, "What to call it")] string Name,
    [property: Option("from", "The preset to start from")] string? From = null,
    [property: Option("json", "A whole preset as JSON, or a .json file holding one")] string? Json = null,
    [property: Option("label", "What the export dialog calls it")] string? Label = null,
    [property: Option("description", "One line on what it is for")] string? Description = null,
    [property: Option("size", "Fit the picture inside this size, for example 1280x720")] FrameSize? Size = null,
    [property: Option("fps", "Write at most this frame rate")] Rational? FrameRate = null,
    [property: Option("quality", "Constant quality: CRF or CQ, lower is better")] int? Quality = null,
    [property: Option("bitrate", "A picture bitrate instead of constant quality: 8M, 2500k")] string? Bitrate = null,
    [property: Option("encoder", "The encoders to try, in order, comma separated")] EquatableArray<string> Encoders = default,
    [property: Option("audio-encoder", "The sound encoder: aac, libopus, flac, eac3")] string? AudioEncoder = null,
    [property: Option("audio-bitrate", "The sound bitrate: 320k")] string? AudioBitrate = null,
    [property: Option("channels", "1, 2 or 6 channels")] int? Channels = null,
    [property: Option("loudness", "Normalise the mix to this many LUFS, for example -14")] double? Loudness = null,
    [property: Option("target-size", "Come in under this size: 8MB")] string? TargetSize = null,
    [property: Option("pixel-format", "yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit")] string? PixelFormat = null,
    [property: Option("audio-only", "Write the sound alone, in a sound file for its encoder: .m4a, .opus, .flac, .wav or .mp3")] bool AudioOnly = false) : ICommand
{
    /// <summary>The changes to the preset it starts from.</summary>
    public Export.ExportOverrides? ToOverrides() =>
        Export.ExportOverrideText.Parse(Size, FrameRate, Quality, Bitrate, Encoders, AudioEncoder, AudioBitrate, Channels, Loudness, TargetSize, PixelFormat, AudioOnly);
}
