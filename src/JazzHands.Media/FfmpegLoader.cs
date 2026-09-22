using System.Diagnostics.CodeAnalysis;
using FFmpeg.AutoGen;
using Serilog;

namespace JazzHands.Media;

/// <summary>
/// Finds the pinned FFmpeg binaries and points FFmpeg.AutoGen at them. Call
/// <see cref="Initialize"/> once at startup, before any other call into JazzHands.Media.
/// </summary>
/// <remarks>
/// Jazz Hands never shells out to ffmpeg.exe for preview, probing or decode; those all run
/// in process against these DLLs. The only exception is the export fallback.
/// </remarks>
public static class FfmpegLoader
{
    /// <summary>The FFmpeg major.minor this build is pinned to. See Docs/CODECS.md.</summary>
    public const string RequiredVersionPrefix = "n8.1";

    private static readonly Lock Gate = new();
    private static string? _binaryDirectory;
    private static string? _versionInfo;

    /// <summary>True once <see cref="Initialize"/> has completed.</summary>
    public static bool IsInitialized => _versionInfo is not null;

    /// <summary>The directory the FFmpeg DLLs were loaded from. Throws before initialization.</summary>
    public static string BinaryDirectory => _binaryDirectory
        ?? throw new InvalidOperationException("FfmpegLoader.Initialize has not run yet.");

    /// <summary>The value of av_version_info(), for example "n8.1.3-20260922". Throws before initialization.</summary>
    public static string VersionInfo => _versionInfo
        ?? throw new InvalidOperationException("FfmpegLoader.Initialize has not run yet.");

    /// <summary>
    /// Locates the FFmpeg binaries, configures FFmpeg.AutoGen and reads the version string.
    /// Safe to call more than once; later calls are no-ops.
    /// </summary>
    /// <param name="binaryDirectory">
    /// An explicit directory containing avcodec-*.dll. When null the standard locations are searched.
    /// </param>
    /// <exception cref="FfmpegNotFoundException">The binaries are missing or unreadable.</exception>
    public static void Initialize(string? binaryDirectory = null)
    {
        if (IsInitialized && binaryDirectory is null)
        {
            return;
        }

        lock (Gate)
        {
            if (IsInitialized && binaryDirectory is null)
            {
                return;
            }

            string directory = binaryDirectory ?? Locate()
                ?? throw new FfmpegNotFoundException(BuildSearchFailureMessage());

            if (!ContainsFfmpeg(directory))
            {
                throw new FfmpegNotFoundException(
                    $"'{directory}' does not contain the FFmpeg shared libraries. Run tools/get-ffmpeg.ps1.");
            }

            ffmpeg.RootPath = directory;

            string version;
            try
            {
                version = ffmpeg.av_version_info();
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                throw new FfmpegNotFoundException(
                    $"FFmpeg in '{directory}' could not be loaded. It must be a 64-bit shared build matching {RequiredVersionPrefix}.",
                    ex);
            }

            _binaryDirectory = directory;
            _versionInfo = version;

            Log.ForContext("SourceContext", "ffmpeg").Information(
                "FFmpeg {Version} loaded from {Directory} (avcodec {Avcodec}, avformat {Avformat}, avutil {Avutil})",
                version,
                directory,
                FormatVersion(ffmpeg.avcodec_version()),
                FormatVersion(ffmpeg.avformat_version()),
                FormatVersion(ffmpeg.avutil_version()));

            if (!version.StartsWith(RequiredVersionPrefix, StringComparison.Ordinal))
            {
                Log.ForContext("SourceContext", "ffmpeg").Warning(
                    "FFmpeg {Version} does not match the pinned {Expected}. Behaviour is untested against this build",
                    version,
                    RequiredVersionPrefix);
            }
        }
    }

    /// <summary>Formats a packed FFmpeg version integer as "major.minor.micro".</summary>
    public static string FormatVersion(uint packed) =>
        $"{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    /// <summary>
    /// Searches the standard locations in order: an explicit JAZZ_FFMPEG_DIR, the ffmpeg folder
    /// next to the running assembly (published layout), then third_party/ffmpeg/bin walking up
    /// from the assembly towards the repository root (development layout).
    /// </summary>
    public static string? Locate()
    {
        foreach (string candidate in CandidateDirectories())
        {
            if (ContainsFfmpeg(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("JAZZ_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            yield return fromEnvironment;
        }

        // AppContext.BaseDirectory, not Assembly.Location: jazz.exe publishes as a single file.
        string appDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(appDirectory, "ffmpeg");
        yield return Path.Combine(appDirectory, "ffmpeg", "bin");

        // Walk up from the build output to the repository root, which holds third_party/ffmpeg.
        var directory = new DirectoryInfo(appDirectory);
        for (int depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "third_party", "ffmpeg", "bin");
        }
    }

    private static bool ContainsFfmpeg([NotNullWhen(true)] string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory, "avcodec-*.dll").Any();

    private static string BuildSearchFailureMessage() =>
        "FFmpeg was not found. Run tools/get-ffmpeg.ps1 to download the pinned build into third_party/ffmpeg, " +
        "or set JAZZ_FFMPEG_DIR to a directory containing the 64-bit shared libraries. Searched: " +
        string.Join("; ", CandidateDirectories());
}

/// <summary>Thrown when the FFmpeg binaries cannot be found or loaded.</summary>
public sealed class FfmpegNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FfmpegNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public FfmpegNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
