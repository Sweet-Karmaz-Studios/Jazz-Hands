using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Media;
using JazzHands.Render;

namespace JazzHands.Cli;

/// <summary>What "jazz version" reports: everything needed to reproduce a bug report.</summary>
/// <param name="JazzHands">The Jazz Hands informational version.</param>
/// <param name="Runtime">The .NET runtime version.</param>
/// <param name="Architecture">The process architecture, which must be x64.</param>
/// <param name="OperatingSystem">The Windows version string.</param>
/// <param name="Ffmpeg">The FFmpeg build, or the reason it could not be loaded.</param>
/// <param name="FfmpegDirectory">Where the FFmpeg libraries came from.</param>
/// <param name="Libraries">The individual FFmpeg library versions.</param>
/// <param name="Gpu">The Direct3D 11 adapter the compositor would use.</param>
/// <param name="FeatureLevel">The Direct3D feature level.</param>
/// <param name="HardwareVideo">Whether the device supports hardware video decode.</param>
public sealed record VersionReport(
    string JazzHands,
    string Runtime,
    string Architecture,
    string OperatingSystem,
    string Ffmpeg,
    string? FfmpegDirectory,
    IReadOnlyDictionary<string, string> Libraries,
    string Gpu,
    string FeatureLevel,
    bool HardwareVideo)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Probes the machine. Never throws: a missing dependency becomes a line saying so.</summary>
    public static VersionReport Collect()
    {
        string ffmpeg;
        string? ffmpegDirectory = null;
        Dictionary<string, string> libraries = [];
        try
        {
            FfmpegLoader.Initialize();
            ffmpeg = FfmpegLoader.VersionInfo;
            ffmpegDirectory = FfmpegLoader.BinaryDirectory;
            libraries["avcodec"] = FfmpegLoader.FormatVersion(FFmpeg.AutoGen.ffmpeg.avcodec_version());
            libraries["avformat"] = FfmpegLoader.FormatVersion(FFmpeg.AutoGen.ffmpeg.avformat_version());
            libraries["avutil"] = FfmpegLoader.FormatVersion(FFmpeg.AutoGen.ffmpeg.avutil_version());
            libraries["swresample"] = FfmpegLoader.FormatVersion(FFmpeg.AutoGen.ffmpeg.swresample_version());
            libraries["swscale"] = FfmpegLoader.FormatVersion(FFmpeg.AutoGen.ffmpeg.swscale_version());
        }
        catch (FfmpegNotFoundException ex)
        {
            ffmpeg = $"not found: {ex.Message}";
        }

        string gpu;
        string featureLevel;
        bool hardwareVideo;
        try
        {
            using RenderDevice device = RenderDevice.Create();
            gpu = device.IsHardware ? device.AdapterName : $"{device.AdapterName} (software)";
            featureLevel = device.FeatureLevel.ToString();
            hardwareVideo = device.SupportsVideo;
        }
        catch (RenderDeviceException ex)
        {
            gpu = $"unavailable: {ex.Message}";
            featureLevel = "none";
            hardwareVideo = false;
        }

        return new VersionReport(
            InformationalVersion(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.OSDescription,
            ffmpeg,
            ffmpegDirectory,
            libraries,
            gpu,
            featureLevel,
            hardwareVideo);
    }

    /// <summary>The human-readable form printed by default.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Jazz Hands {JazzHands}");
        sb.AppendLine($".NET         {Runtime} ({Architecture})");
        sb.AppendLine($"Windows      {OperatingSystem}");
        sb.Append($"FFmpeg       {Ffmpeg}");
        if (Libraries.Count > 0)
        {
            sb.Append(" [");
            sb.Append(string.Join(", ", Libraries.Select(pair => $"{pair.Key} {pair.Value}")));
            sb.Append(']');
        }

        sb.AppendLine();
        if (FfmpegDirectory is not null)
        {
            sb.AppendLine($"             {FfmpegDirectory}");
        }

        sb.AppendLine($"GPU          {Gpu}");
        sb.Append($"Direct3D     {FeatureLevel}, hardware video {(HardwareVideo ? "yes" : "no")}");
        return sb.ToString();
    }

    /// <summary>The --json form.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static string InformationalVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Strip the source-control metadata the SDK appends so the banner stays short.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
