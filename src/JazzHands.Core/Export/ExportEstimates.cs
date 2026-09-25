using System.Globalization;

namespace JazzHands.Core.Export;

/// <summary>About how big a file an export writes and about how long it takes.</summary>
/// <param name="Bytes">The file's size, roughly.</param>
/// <param name="Seconds">Wall clock, roughly, on this kind of machine.</param>
public sealed record ExportEstimate(long Bytes, double Seconds)
{
    /// <summary>The estimate as the dialog and the command line say it: <c>about 45 MB, about 20 s</c>.</summary>
    public override string ToString() =>
        $"about {ExportPresets.FormatBytes(Bytes)}, about {Duration(Seconds)}";

    private static string Duration(double seconds) => seconds switch
    {
        < 1 => "a second",
        < 90 => string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(seconds):0} s"),
        < 5400 => string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(seconds / 60):0} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.#} h"),
    };
}

/// <summary>
/// Rough sizes and times for an export, before it runs.
/// </summary>
/// <remarks>
/// A bitrate or a size target says the size outright. Constant quality does not, so it is guessed
/// from the pixels and a rate of bits a pixel for each codec at a reference quality, halving
/// every few steps of quality: about what these encoders spend on screen recordings and game
/// footage, which is most of what goes through here. Intra codecs spend what their profile says.
/// Times come from pixels a second each encoder manages on a desktop CPU or an RTX card, capped by
/// how fast the compositor renders. Both are for "is this minutes or hours, megabytes or
/// gigabytes", which is what a person deciding between presets needs; the file says the rest.
/// </remarks>
public static class ExportEstimates
{
    /// <summary>The estimate for a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="sourceBytes">For a copy, the bytes of the source it takes; ignored otherwise.</param>
    public static ExportEstimate For(ExportPlan plan, long sourceBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        double seconds = Math.Max(0.001, plan.Duration.ToSeconds());

        if (plan.Mode == ExportMode.Copy)
        {
            // A copy reads and writes each byte once; a desktop SSD does several hundred MB a second.
            return new ExportEstimate(sourceBytes, sourceBytes / 400_000_000.0);
        }

        double bits = 0;
        double time = 0;
        if (plan.Video is { } video)
        {
            double pixels = (double)video.Width * video.Height;
            double rate = video.FrameRate.ToDouble();
            bits += video.Bitrate > 0 && !video.Lossless ? video.Bitrate * seconds : BitsPerPixel(video) * pixels * rate * seconds;

            string encoder = video.Encoders.Length > 0 ? video.Encoders[0] : video.Codec;
            double encodes = PixelsPerSecond(encoder, video.Speed);

            // The compositor renders a simple 1080p stack at about 500 frames a second.
            double renders = 1e9;
            time = pixels * rate * seconds / Math.Min(encodes, renders);
        }

        if (plan.Audio is { } audio)
        {
            double pcm = audio.SampleRate * (double)audio.Channels;
            double perSecond = audio.Encoder switch
            {
                "pcm_s16le" => pcm * 16,
                "pcm_s24le" => pcm * 24,
                "flac" => pcm * 24 * 0.55,
                _ when audio.Bitrate > 0 => audio.Bitrate,
                "eac3" or "ac3" => audio.Channels > 2 ? 640_000 : 192_000,
                _ => 64_000.0 * audio.Channels,
            };
            bits += perSecond * seconds;

            // Mixing and encoding sound alone runs at a few hundred times real time.
            time = Math.Max(time, seconds / 300);
        }

        // The container adds a percent or two.
        return new ExportEstimate((long)(bits / 8 * 1.02), time);
    }

    /// <summary>What a codec spends on a pixel at its quality, in bits.</summary>
    private static double BitsPerPixel(ExportVideo video) => video.Codec switch
    {
        _ when video.Lossless && video.Codec != "ffv1" => 4.0,
        "h264" => 0.10 * Math.Pow(2, (19 - video.Quality) / 6.0),
        "hevc" => 0.06 * Math.Pow(2, (21 - video.Quality) / 6.0),
        "av1" => 0.05 * Math.Pow(2, (26 - video.Quality) / 8.0),
        "vp9" => 0.06 * Math.Pow(2, (31 - video.Quality) / 8.0),
        "prores" => video.Profile switch
        {
            "0" => 0.65,
            "1" => 1.6,
            "2" => 2.35,
            "4" or "5" => 5.3,
            _ => 3.5,
        },
        "dnxhr" => video.Profile switch
        {
            "dnxhr_lb" => 0.7,
            "dnxhr_sq" => 1.9,
            "dnxhr_444" => 5.6,
            _ => 2.8,
        },
        "ffv1" => 5.0,
        "gif" => 1.2,
        "png" => 10.0,
        _ => 0.1,
    };

    /// <summary>Pixels a second an encoder gets through at a speed.</summary>
    private static double PixelsPerSecond(string encoder, string speed)
    {
        double medium = encoder switch
        {
            _ when encoder.Contains("nvenc", StringComparison.Ordinal) => encoder.StartsWith("av1", StringComparison.Ordinal) ? 0.8e9 : 1.2e9,
            "libx264" => 1.2e8,
            "libx265" => 3e7,
            "libsvtav1" => 4e7,
            "libvpx-vp9" => 2e7,
            "prores_ks" or "dnxhd" => 4e8,
            "ffv1" => 2e8,
            _ => 5e7,
        };

        return speed switch
        {
            "fast" => medium * 2,
            "slow" => medium / 2,
            _ => medium,
        };
    }
}
