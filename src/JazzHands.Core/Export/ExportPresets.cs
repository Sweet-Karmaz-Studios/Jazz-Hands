using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;

namespace JazzHands.Core.Export;

/// <summary>
/// An export recipe: container, picture and sound, as a JSON file.
/// </summary>
/// <remarks>
/// <para>
/// The built-in presets are files like this compiled into the engine, and a person's own are
/// <c>.json</c> files in <c>%APPDATA%\JazzHands\export-presets</c>; one with a built-in's name
/// replaces it. <c>Docs/schema/export-preset.schema.json</c> describes the format and is generated
/// from these records.
/// </para>
/// <para>
/// A preset says the most it will write, not exactly what: a sequence smaller than
/// <see cref="ExportPresetVideo.MaxHeight"/> keeps its own size, and one slower than
/// <see cref="ExportPresetVideo.MaxFrameRate"/> keeps its own rate. Nothing is scaled up.
/// </para>
/// </remarks>
/// <param name="Name">What it is called on the command line, in kebab case: youtube-1080p.</param>
/// <param name="Label">What the export dialog calls it.</param>
/// <param name="Description">One line for help and the export dialog.</param>
/// <param name="Category">What it is for: youtube, discord, device, archive, web, image, audio or other.</param>
/// <param name="Container">The FFmpeg muxer: mp4, mov, matroska, webm, mxf, gif, image2, wav, flac or mp3.</param>
/// <param name="Extension">The file extension the container goes with, with its dot.</param>
/// <param name="Video">The picture, or null for a sound-only file.</param>
/// <param name="Audio">The sound, or null for a silent file.</param>
/// <param name="TargetBytes">A size the whole file must come in under, or 0. The planner works out the bitrate and, when it must, a smaller picture.</param>
/// <param name="Loudness">Integrated loudness to normalise the mix to, in LUFS, or null to leave it as mixed.</param>
public sealed record ExportPreset(
    string Name,
    string Label = "",
    string Description = "",
    string Category = "other",
    string Container = "mp4",
    string Extension = ".mp4",
    ExportPresetVideo? Video = null,
    ExportPresetAudio? Audio = null,
    long TargetBytes = 0,
    double? Loudness = null) : IEquatable<ExportPreset>
{
    /// <summary>True for a preset that ships with the editor.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }

    /// <summary>The file it was read from, or empty for a built-in.</summary>
    [JsonIgnore]
    public string Source { get; init; } = string.Empty;
}

/// <summary>The picture side of an export preset.</summary>
/// <param name="Codec">h264, hevc, av1, vp9, prores, dnxhr, ffv1, gif or png.</param>
/// <param name="Encoders">FFmpeg encoders in the order to try them, the GPU first. Empty for the codec's usual chain.</param>
/// <param name="MaxWidth">The widest picture it writes, or 0 for no limit.</param>
/// <param name="MaxHeight">The tallest picture it writes, or 0 for the sequence's own size.</param>
/// <param name="MaxFrameRate">The fastest rate it writes, or null for the sequence's own.</param>
/// <param name="Quality">Constant quality: CRF or CQ, lower is better. Ignored for a bitrate or a size target.</param>
/// <param name="Bitrate">A bitrate in bits per second instead of constant quality, or 0.</param>
/// <param name="Speed">fast, medium or slow.</param>
/// <param name="Lossless">Encode without loss.</param>
/// <param name="PixelFormat">The FFmpeg pixel format to encode, for example yuv420p10le; null for the encoder's usual one.</param>
/// <param name="Profile">The codec profile: high, main10, dnxhr_hq, or a ProRes number (3 is 422 HQ).</param>
/// <param name="Level">The codec level, for example 4.2; for FFV1 its version.</param>
/// <param name="KeyframeSeconds">Seconds between keyframes, or 0 for every frame a keyframe.</param>
public sealed record ExportPresetVideo(
    string Codec,
    EquatableArray<string> Encoders = default,
    int MaxWidth = 0,
    int MaxHeight = 0,
    Rational? MaxFrameRate = null,
    int Quality = 20,
    long Bitrate = 0,
    string Speed = "medium",
    bool Lossless = false,
    string? PixelFormat = null,
    string? Profile = null,
    string? Level = null,
    double KeyframeSeconds = 2.0) : IEquatable<ExportPresetVideo>;

/// <summary>The sound side of an export preset.</summary>
/// <param name="Encoder">The FFmpeg encoder: aac, libopus, flac, libmp3lame, ac3, eac3, pcm_s16le or pcm_s24le.</param>
/// <param name="Bitrate">Bits per second for a lossy encoder, or 0 for its own default.</param>
/// <param name="Channels">1, 2 or 6, or 0 for the sequence's own.</param>
/// <param name="SampleRate">Samples per second, or 0 for the sequence's own.</param>
public sealed record ExportPresetAudio(
    string Encoder,
    long Bitrate = 0,
    int Channels = 0,
    int SampleRate = 0) : IEquatable<ExportPresetAudio>;

/// <summary>A preset as <c>presets.list</c> shows it: what it is, in a line, and where it came from.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Label">What the dialog calls it.</param>
/// <param name="Description">One line on what it is for.</param>
/// <param name="Category">youtube, discord, device, archive, web, image, audio or other.</param>
/// <param name="Summary">What it writes, in a line: <c>H.264 up to 1080p, AAC 320 kb/s, MP4</c>.</param>
/// <param name="Extension">The file extension.</param>
/// <param name="BuiltIn">True for one that ships with the editor.</param>
/// <param name="Source">The file a person's preset was read from, or null.</param>
public sealed record ExportPresetSummary(
    string Name,
    string Label,
    string Description,
    string Category,
    string Summary,
    string Extension,
    bool BuiltIn,
    string? Source);

/// <summary>What a container carries: which codecs, and whether it takes subtitles and chapters.</summary>
/// <param name="Muxer">The FFmpeg muxer name.</param>
/// <param name="Extensions">File extensions it goes with, the usual one first, with dots.</param>
/// <param name="VideoCodecs">The picture codecs it takes; empty for a sound-only container.</param>
/// <param name="AudioEncoders">The sound encoders it takes; empty for a silent one.</param>
/// <param name="Subtitles">The subtitle encoder a soft track uses, or null when it carries none.</param>
/// <param name="Chapters">True when it carries chapters.</param>
/// <param name="Sequence">True for an image sequence, one file a frame.</param>
public sealed record ExportContainer(
    string Muxer,
    EquatableArray<string> Extensions,
    EquatableArray<string> VideoCodecs,
    EquatableArray<string> AudioEncoders,
    string? Subtitles,
    bool Chapters,
    bool Sequence = false) : IEquatable<ExportContainer>;

/// <summary>
/// The rules every export preset follows: the containers and codecs Jazz Hands writes, how a preset
/// file reads and is checked, and the size and rate a preset gives a sequence.
/// </summary>
/// <remarks>
/// The presets themselves are data: <c>JazzHands.Engine.Export.ExportPresetLibrary</c> reads the
/// built-in files and a person's own. Everything here is the part a file cannot change.
/// </remarks>
public static partial class ExportPresets
{
    /// <summary>The preset used when none is named.</summary>
    public const string Default = "youtube-1080p";

    /// <summary>Every container Jazz Hands writes.</summary>
    public static IReadOnlyList<ExportContainer> Containers { get; } =
    [
        new("mp4", [".mp4", ".m4v"], ["h264", "hevc", "av1"], ["aac", "ac3", "eac3", "flac", "libopus", "libmp3lame"], "mov_text", true),
        new("mov", [".mov"], ["h264", "hevc", "prores", "dnxhr"], ["aac", "pcm_s16le", "pcm_s24le", "ac3", "eac3"], "mov_text", true),
        new("matroska", [".mkv"], ["h264", "hevc", "av1", "vp9", "prores", "dnxhr", "ffv1"], ["aac", "flac", "libopus", "ac3", "eac3", "pcm_s16le", "pcm_s24le", "libmp3lame"], "subrip", true),
        new("webm", [".webm"], ["vp9", "av1"], ["libopus"], "webvtt", false),
        new("mxf", [".mxf"], ["dnxhr"], ["pcm_s16le", "pcm_s24le"], null, false),
        new("gif", [".gif"], ["gif"], [], null, false),
        new("image2", [".png"], ["png"], [], null, false, Sequence: true),
        new("wav", [".wav"], [], ["pcm_s16le", "pcm_s24le"], null, false),
        new("flac", [".flac"], [], ["flac"], null, false),
        new("mp3", [".mp3"], [], ["libmp3lame"], null, false),
    ];

    /// <summary>The encoders a codec is written with when a preset names none: the GPU first where there is one.</summary>
    public static IReadOnlyDictionary<string, EquatableArray<string>> CodecEncoders { get; } = new Dictionary<string, EquatableArray<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = ["h264_nvenc", "libx264"],
        ["hevc"] = ["hevc_nvenc", "libx265"],
        ["av1"] = ["av1_nvenc", "libsvtav1"],
        ["vp9"] = ["libvpx-vp9"],
        ["prores"] = ["prores_ks"],
        ["dnxhr"] = ["dnxhd"],
        ["ffv1"] = ["ffv1"],
        ["gif"] = ["gif"],
        ["png"] = ["png"],
    };

    /// <summary>The pixel formats a preset may ask for.</summary>
    public static IReadOnlyList<string> PixelFormats { get; } =
        ["yuv420p", "yuv420p10le", "yuv422p", "yuv422p10le", "yuv444p10le", "nv12", "p010le", "rgb24", "pal8", "yuva420p", "yuva444p10le", "rgba"];

    /// <summary>The categories presets are grouped under.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["youtube", "discord", "device", "archive", "web", "image", "audio", "other"];

    private static readonly JsonSerializerOptions FileJson = new(JazzJson.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A container by muxer name, or null.</summary>
    public static ExportContainer? Container(string muxer) =>
        Containers.FirstOrDefault(container => string.Equals(container.Muxer, muxer, StringComparison.OrdinalIgnoreCase));

    /// <summary>The container a file extension means, or null for one Jazz Hands does not write.</summary>
    public static ExportContainer? ContainerForExtension(string extension)
    {
        string wanted = "." + (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return Containers.FirstOrDefault(container => container.Extensions.Contains(wanted));
    }

    /// <summary>The encoders a preset's picture tries, its own or its codec's usual chain.</summary>
    public static EquatableArray<string> EncodersFor(ExportPresetVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return !video.Encoders.IsEmpty
            ? video.Encoders
            : CodecEncoders.TryGetValue(video.Codec, out EquatableArray<string> chain) ? chain : [video.Codec];
    }

    /// <summary>
    /// The size a preset writes for a sequence: scaled to fit its largest width and height, never
    /// up, on even numbers.
    /// </summary>
    public static (int Width, int Height) SizeFor(ExportPresetVideo? video, int width, int height) =>
        Fit(width, height, video?.MaxWidth ?? 0, video?.MaxHeight ?? 0);

    /// <summary>A size scaled to fit a box, never up, on even numbers. Zero for a side means no limit on it.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = 1.0;
        if (maxHeight > 0 && height > maxHeight)
        {
            scale = Math.Min(scale, (double)maxHeight / height);
        }

        if (maxWidth > 0 && width > maxWidth)
        {
            scale = Math.Min(scale, (double)maxWidth / width);
        }

        if (scale >= 1.0)
        {
            return (Even(width), Even(height));
        }

        // To the nearest even number, not down to one: 1920x1080 at 480 lines is 854 wide, as
        // everyone writes it, where rounding down gives 852.
        return (Round(width * scale), Round(height * scale));
    }

    /// <summary>The rate a preset writes a sequence at: its own, or the preset's limit when that is slower.</summary>
    public static Rational FrameRateFor(ExportPresetVideo? video, Rational sequence) =>
        video?.MaxFrameRate is { } limit && limit.ToDouble() > 0 && limit.ToDouble() < sequence.ToDouble() ? limit : sequence;

    /// <summary>Reads a preset file, or says why it is not one.</summary>
    public static bool TryRead(string json, out ExportPreset? preset, out string? error)
    {
        preset = null;
        try
        {
            preset = JsonSerializer.Deserialize<ExportPreset>(json, FileJson);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }

        if (preset is null)
        {
            error = "The file is not an export preset.";
            return false;
        }

        error = Check(preset);
        if (error is not null)
        {
            preset = null;
            return false;
        }

        return true;
    }

    /// <summary>A preset as a file's text.</summary>
    public static string ToJson(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(preset, JazzJson.Options) + "\n";
    }

    /// <summary>What is wrong with a preset, or null when nothing is.</summary>
    public static string? Check(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (string.IsNullOrWhiteSpace(preset.Name) || !KebabName().IsMatch(preset.Name))
        {
            return $"'{preset.Name}' is not a preset name: use lower case letters, digits and hyphens, like my-youtube.";
        }

        string name = preset.Name;
        if (Container(preset.Container) is not { } container)
        {
            return $"'{name}' writes '{preset.Container}', which is not one of {string.Join(", ", Containers.Select(c => c.Muxer))}.";
        }

        if (string.IsNullOrWhiteSpace(preset.Extension) || !container.Extensions.Contains(preset.Extension.ToLowerInvariant()))
        {
            return $"'{name}' gives {container.Muxer} files the extension '{preset.Extension}'; it takes {string.Join(" or ", container.Extensions)}.";
        }

        if (!Categories.Contains(preset.Category))
        {
            return $"'{name}' is in the category '{preset.Category}', which is not one of {string.Join(", ", Categories)}.";
        }

        if (preset.Video is null && preset.Audio is null)
        {
            return $"'{name}' writes neither picture nor sound.";
        }

        if (preset.Video is { } video)
        {
            if (container.VideoCodecs.IsEmpty)
            {
                return $"'{name}' writes a picture into {container.Muxer}, which only carries sound.";
            }

            if (!container.VideoCodecs.Contains(video.Codec))
            {
                return $"'{name}' writes {video.Codec} into {container.Muxer}, which takes {string.Join(", ", container.VideoCodecs)}.";
            }

            if (video.MaxWidth < 0 || video.MaxHeight < 0 || video.Quality < 0 || video.Bitrate < 0 || video.KeyframeSeconds < 0)
            {
                return $"'{name}' has a negative size, quality, bitrate or keyframe interval.";
            }

            if (video.MaxFrameRate is { } rate && (rate.Num <= 0 || rate.Den <= 0))
            {
                return $"'{name}' limits the frame rate to {rate}, which is not a rate.";
            }

            if (video.Speed is not ("fast" or "medium" or "slow"))
            {
                return $"'{name}' has the speed '{video.Speed}'; it is fast, medium or slow.";
            }

            if (video.PixelFormat is { } format && !PixelFormats.Contains(format))
            {
                return $"'{name}' encodes {format}, which is not one of {string.Join(", ", PixelFormats)}.";
            }
        }
        else if (container.AudioEncoders.IsEmpty)
        {
            return $"'{name}' writes {container.Muxer} with no picture, and {container.Muxer} is a picture format.";
        }

        if (preset.Audio is { } audio)
        {
            if (container.AudioEncoders.IsEmpty)
            {
                return $"'{name}' writes sound into {container.Muxer}, which carries none.";
            }

            if (!container.AudioEncoders.Contains(audio.Encoder))
            {
                return $"'{name}' writes {audio.Encoder} sound into {container.Muxer}, which takes {string.Join(", ", container.AudioEncoders)}.";
            }

            if (audio.Channels is not (0 or 1 or 2 or 6))
            {
                return $"'{name}' writes {audio.Channels} channels; it is 1, 2 or 6, or 0 for the sequence's.";
            }

            if (audio.Bitrate < 0 || audio.SampleRate < 0)
            {
                return $"'{name}' has a negative sound bitrate or sample rate.";
            }
        }

        if (preset.TargetBytes < 0)
        {
            return $"'{name}' aims for a negative size.";
        }

        if (preset.TargetBytes > 0 && preset.Video is { Lossless: true })
        {
            return $"'{name}' is lossless and has a size target; a lossless file is the size it is.";
        }

        if (preset.Loudness is { } lufs && (lufs < -70 || lufs > 0))
        {
            return $"'{name}' normalises to {lufs.ToString(CultureInfo.InvariantCulture)} LUFS; loudness is between -70 and 0.";
        }

        return null;
    }

    /// <summary>A preset as <c>presets.list</c> shows it.</summary>
    public static ExportPresetSummary Summarise(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new ExportPresetSummary(
            preset.Name,
            preset.Label.Length > 0 ? preset.Label : preset.Name,
            preset.Description,
            preset.Category,
            Describe(preset),
            preset.Extension,
            preset.BuiltIn,
            preset.Source.Length > 0 ? preset.Source : null);
    }

    /// <summary>What a preset writes, in a line: <c>H.264 up to 1080p, AAC 320 kb/s, MP4</c>.</summary>
    public static string Describe(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var parts = new List<string>();

        if (preset.Video is { } video)
        {
            string picture = CodecName(video.Codec);
            if (video.Lossless)
            {
                picture = "lossless " + picture;
            }

            if (video.Profile is { } profile)
            {
                picture += " " + ProfileName(video.Codec, profile);
            }

            if (video.PixelFormat is { } format && format.Contains("10", StringComparison.Ordinal))
            {
                picture += " 10 bit";
            }

            if (video.MaxHeight > 0)
            {
                picture += string.Create(CultureInfo.InvariantCulture, $" up to {video.MaxHeight}p");
            }
            else if (video.MaxWidth > 0)
            {
                picture += string.Create(CultureInfo.InvariantCulture, $" up to {video.MaxWidth} wide");
            }

            if (video.MaxFrameRate is { } rate)
            {
                picture += string.Create(CultureInfo.InvariantCulture, $" at up to {rate.ToDouble():0.##} fps");
            }

            parts.Add(picture);
        }

        if (preset.Audio is { } audio)
        {
            string sound = AudioName(audio.Encoder);
            if (audio.Bitrate > 0)
            {
                sound += string.Create(CultureInfo.InvariantCulture, $" {audio.Bitrate / 1000} kb/s");
            }

            if (audio.Channels == 6)
            {
                sound += " 5.1";
            }
            else if (audio.Channels is 1 or 2)
            {
                sound += audio.Channels == 1 ? " mono" : " stereo";
            }

            parts.Add(sound);
        }

        if (preset.TargetBytes > 0)
        {
            parts.Add("under " + FormatBytes(preset.TargetBytes));
        }

        if (preset.Loudness is { } lufs)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{lufs:0.#} LUFS"));
        }

        parts.Add(ContainerName(preset.Container));
        return string.Join(", ", parts);
    }

    /// <summary>A byte count as people write it: 8 MB, 25 MB, 1.5 GB, in binary units as Discord counts them.</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.##} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0.##} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
    };

    /// <summary>
    /// Reads a size: <c>8MB</c>, <c>25 MB</c>, <c>1.5GB</c>, <c>500KB</c> or a plain byte count.
    /// Binary units, since that is how Discord and Windows count them.
    /// </summary>
    public static bool TryParseBytes(string? text, out long bytes)
    {
        bytes = 0;
        if (!TryNumberWithUnit(text, out double number, out string unit))
        {
            return false;
        }

        double scale = unit switch
        {
            "" or "b" => 1,
            "k" or "kb" or "kib" => 1L << 10,
            "m" or "mb" or "mib" => 1L << 20,
            "g" or "gb" or "gib" => 1L << 30,
            _ => 0,
        };

        if (scale == 0 || number <= 0)
        {
            return false;
        }

        bytes = (long)Math.Round(number * scale);
        return true;
    }

    /// <summary>Reads a bitrate: <c>8M</c>, <c>320k</c>, <c>2.5 Mbps</c> or a plain count of bits per second. Decimal units, as bitrates are.</summary>
    public static bool TryParseBitrate(string? text, out long bitsPerSecond)
    {
        bitsPerSecond = 0;
        if (!TryNumberWithUnit(text, out double number, out string unit))
        {
            return false;
        }

        double scale = unit.Replace("bps", string.Empty, StringComparison.Ordinal).Replace("b/s", string.Empty, StringComparison.Ordinal) switch
        {
            "" or "b" => 1,
            "k" or "kb" => 1_000,
            "m" or "mb" => 1_000_000,
            "g" or "gb" => 1_000_000_000,
            _ => 0,
        };

        if (scale == 0 || number <= 0)
        {
            return false;
        }

        bitsPerSecond = (long)Math.Round(number * scale);
        return true;
    }

    /// <summary>A codec as people name it.</summary>
    public static string CodecName(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "prores" => "ProRes",
        "dnxhr" => "DNxHR",
        "ffv1" => "FFV1",
        "gif" => "GIF",
        "png" => "PNG",
        _ => codec,
    };

    private static string ProfileName(string codec, string profile) => codec.ToLowerInvariant() switch
    {
        "prores" => profile switch
        {
            "0" => "Proxy",
            "1" => "LT",
            "2" => "422",
            "3" => "422 HQ",
            "4" => "4444",
            "5" => "4444 XQ",
            _ => profile,
        },
        "dnxhr" => profile.Replace("dnxhr_", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
        _ => profile switch
        {
            "main10" => "Main 10",
            "high" => "High",
            "main" => "Main",
            _ => profile,
        },
    };

    private static string AudioName(string encoder) => encoder switch
    {
        "aac" => "AAC",
        "libopus" => "Opus",
        "flac" => "FLAC",
        "libmp3lame" => "MP3",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "pcm_s16le" => "16 bit PCM",
        "pcm_s24le" => "24 bit PCM",
        _ => encoder,
    };

    private static string ContainerName(string muxer) => muxer switch
    {
        "matroska" => "Matroska",
        "image2" => "PNG sequence",
        "mp3" => "MP3",
        _ => muxer.ToUpperInvariant(),
    };

    private static bool TryNumberWithUnit(string? text, out double number, out string unit)
    {
        number = 0;
        unit = string.Empty;
        string trimmed = (text ?? string.Empty).Trim().ToLowerInvariant();
        int split = 0;
        while (split < trimmed.Length && (char.IsAsciiDigit(trimmed[split]) || trimmed[split] == '.'))
        {
            split++;
        }

        if (split == 0 || !double.TryParse(trimmed[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        unit = trimmed[split..].Trim();
        return true;
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    private static int Round(double value) => Math.Max(2, (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabName();
}
