using System.CommandLine;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Cli;

/// <summary>
/// The options <c>jazz export</c> and <c>jazz trim</c> take to change a preset for one export:
/// <c>--size</c>, <c>--fps</c>, <c>--bitrate</c>, <c>--loudness</c>, <c>--target-size</c> and the
/// rest, spelled and read exactly as <c>export.enqueue</c> reads them.
/// </summary>
internal sealed class ExportOverrideOptions
{
    private readonly Option<string?> _size = new("--size") { Description = "Fit the picture inside this size, for example 1280x720. Never scales up." };
    private readonly Option<string?> _fps = new("--fps") { Description = "Write at this frame rate, for example 30 or 30000/1001." };
    private readonly Option<string?> _quality = new("--quality") { Description = "Constant quality: CRF or CQ, lower is better." };
    private readonly Option<string?> _bitrate = new("--bitrate") { Description = "A picture bitrate instead of constant quality: 8M, 2500k." };
    private readonly Option<string?> _encoder = new("--encoder") { Description = "The encoders to try, in order, comma separated: libx264 or hevc_nvenc,libx265." };
    private readonly Option<string?> _audioEncoder = new("--audio-encoder") { Description = "The sound encoder: aac, libopus, flac, eac3, pcm_s24le." };
    private readonly Option<string?> _audioBitrate = new("--audio-bitrate") { Description = "The sound bitrate: 320k." };
    private readonly Option<string?> _channels = new("--channels") { Description = "1, 2 or 6 channels. Stereo from a 5.1 sequence folds it down." };
    private readonly Option<string?> _loudness = new("--loudness") { Description = "Normalise the mix to this many LUFS, for example -14." };
    private readonly Option<string?> _targetSize = new("--target-size") { Description = "Come in under this size: 8MB. The picture gets smaller when it must." };
    private readonly Option<string?> _pixelFormat = new("--pixel-format") { Description = "The pixel format: yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit." };
    private readonly Option<string?> _start = new("--start") { Description = "Export from here, in sequence time." };
    private readonly Option<string?> _end = new("--end") { Description = "Export to here, in sequence time." };

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        foreach (Option option in new Option[] { _size, _fps, _quality, _bitrate, _encoder, _audioEncoder, _audioBitrate, _channels, _loudness, _targetSize, _pixelFormat, _start, _end })
        {
            command.Options.Add(option);
        }
    }

    /// <summary>The overrides asked for, or null for none.</summary>
    public ExportOverrides? Overrides(ParseResult parse, Rational rate) => ExportOverrideText.Parse(
        Value<FrameSize?>(parse, _size, rate, "size"),
        Value<Rational?>(parse, _fps, rate, "fps"),
        Value<int?>(parse, _quality, rate, "quality"),
        parse.GetValue(_bitrate),
        Value<EquatableArray<string>>(parse, _encoder, rate, "encoder"),
        parse.GetValue(_audioEncoder),
        parse.GetValue(_audioBitrate),
        Value<int?>(parse, _channels, rate, "channels"),
        Value<double?>(parse, _loudness, rate, "loudness"),
        parse.GetValue(_targetSize),
        parse.GetValue(_pixelFormat));

    /// <summary>The stretch asked for, or null for all of it.</summary>
    public TimeRange? Range(ParseResult parse, Rational rate) => ExportOverrideText.Range(
        Value<Flicks?>(parse, _start, rate, "start"),
        Value<Flicks?>(parse, _end, rate, "end"));

    private static T Value<T>(ParseResult parse, Option<string?> option, Rational rate, string name) =>
        parse.GetValue(option) is { } text ? (T)CommandValues.Parse(typeof(T), text, rate, name)! : default!;
}
