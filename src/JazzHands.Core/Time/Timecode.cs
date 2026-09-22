using System.Globalization;
using System.Text;

namespace JazzHands.Core.Time;

/// <summary>
/// Formats and parses the times people type and read. Timecode is a display concern: it is never
/// stored in the project model, and drop-frame is a labelling scheme, not a different clock.
/// </summary>
public static class Timecode
{
    /// <summary>
    /// Renders a position as HH:MM:SS:FF at the given rate. Drop-frame uses a semicolon before the
    /// frame field, as every other editor does, and is only valid for NTSC rates.
    /// </summary>
    public static string Format(Flicks position, Rational fps, bool dropFrame = false)
    {
        if (dropFrame && !SupportsDropFrame(fps))
        {
            throw new ArgumentException($"Drop-frame timecode is not defined for {fps} fps.", nameof(dropFrame));
        }

        long frame = position.ToFrames(fps, RoundingMode.Floor);
        bool negative = frame < 0;
        if (negative)
        {
            frame = -frame;
        }

        int nominal = fps.NominalRate;
        if (dropFrame)
        {
            frame = ApplyDropFrameLabels(frame, nominal);
        }

        long ff = frame % nominal;
        long totalSeconds = frame / nominal;
        long ss = totalSeconds % 60;
        long mm = totalSeconds / 60 % 60;
        long hh = totalSeconds / 3600;

        var sb = new StringBuilder(negative ? 12 : 11);
        if (negative)
        {
            sb.Append('-');
        }

        sb.Append(CultureInfo.InvariantCulture, $"{hh:00}:{mm:00}:{ss:00}");
        sb.Append(dropFrame ? ';' : ':');
        sb.Append(CultureInfo.InvariantCulture, $"{ff:00}");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a position as HH:MM:SS.mmm. This is the wall-clock form used by the CLI, logs and
    /// anywhere a frame rate is not in hand.
    /// </summary>
    public static string FormatClock(Flicks position)
    {
        long value = position.Value;
        bool negative = value < 0;
        if (negative)
        {
            value = -value;
        }

        long totalMilliseconds = value / Flicks.PerMillisecond;
        long ms = totalMilliseconds % 1000;
        long totalSeconds = totalMilliseconds / 1000;
        long ss = totalSeconds % 60;
        long mm = totalSeconds / 60 % 60;
        long hh = totalSeconds / 3600;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(negative ? "-" : string.Empty)}{hh:00}:{mm:00}:{ss:00}.{ms:000}");
    }

    /// <summary>True for the NTSC rates where drop-frame labelling is defined: 29.97 and 59.94.</summary>
    public static bool SupportsDropFrame(Rational fps) =>
        fps.IsNtsc && (fps.NominalRate == 30 || fps.NominalRate == 60);

    /// <summary>
    /// Parses any time Jazz Hands accepts: HH:MM:SS:FF and HH:MM:SS;FF (drop-frame),
    /// HH:MM:SS.mmm, MM:SS.mmm, 12.5s, 750f and 123456789fl. A leading minus is allowed.
    /// </summary>
    public static Flicks Parse(string text, Rational fps) => TryParse(text, fps, out Flicks value)
        ? value
        : throw new FormatException(
            $"'{text}' is not a time. Use 00:00:04:12, 00:00:04.500, 12.5s, 750f or 123456789fl.");

    /// <summary>Parses a time. See <see cref="Parse"/> for the accepted forms.</summary>
    public static bool TryParse(string? text, Rational fps, out Flicks value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();

        bool negative = false;
        if (s[0] is '-' or '+')
        {
            negative = s[0] == '-';
            s = s[1..];
            if (s.IsEmpty)
            {
                return false;
            }
        }

        bool parsed = s.EndsWith("fl", StringComparison.OrdinalIgnoreCase)
            ? TryParseSuffix(s[..^2], 1, out value)
            : s.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                ? TryParseSuffix(s[..^2], Flicks.PerMillisecond, out value)
                : s.EndsWith('s')
                    ? TryParseSeconds(s[..^1], out value)
                    : s.EndsWith('f')
                        ? TryParseFrames(s[..^1], fps, out value)
                        : s.Contains(':')
                            ? TryParseClock(s, fps, out value)
                            : TryParseSeconds(s, out value);

        if (!parsed)
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    private static bool TryParseSuffix(ReadOnlySpan<char> s, long scale, out Flicks value)
    {
        value = default;
        if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long units))
        {
            return false;
        }

        value = new Flicks(units * scale);
        return true;
    }

    private static bool TryParseFrames(ReadOnlySpan<char> s, Rational fps, out Flicks value)
    {
        value = default;
        if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long frames))
        {
            return false;
        }

        value = Flicks.FromFrames(frames, fps);
        return true;
    }

    private static bool TryParseSeconds(ReadOnlySpan<char> s, out Flicks value)
    {
        value = default;
        s = s.Trim();

        int dot = s.IndexOf('.');
        ReadOnlySpan<char> wholePart = dot < 0 ? s : s[..dot];
        ReadOnlySpan<char> fractionPart = dot < 0 ? default : s[(dot + 1)..];

        if (!long.TryParse(wholePart, NumberStyles.None, CultureInfo.InvariantCulture, out long whole))
        {
            return false;
        }

        if (!TryScaleFraction(fractionPart, out long fraction))
        {
            return false;
        }

        value = new Flicks((whole * Flicks.PerSecond) + fraction);
        return true;
    }

    private static bool TryParseClock(ReadOnlySpan<char> s, Rational fps, out Flicks value)
    {
        value = default;

        // A semicolon anywhere means drop-frame labelling; editors write 00:00:04;12 or 00;00;04;12.
        bool dropFrame = s.Contains(';');
        Span<Range> fields = stackalloc Range[5];
        int count = SplitFields(s, fields);
        if (count is < 2 or > 4)
        {
            return false;
        }

        ReadOnlySpan<char> last = s[fields[count - 1]];
        bool hasFrames = count == 4 || dropFrame;
        if (hasFrames && count != 4)
        {
            return false;
        }

        if (hasFrames && last.Contains('.'))
        {
            return false;
        }

        long[] parts = new long[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<char> field = s[fields[i]];
            if (i == count - 1 && !hasFrames)
            {
                break;
            }

            if (field.IsEmpty ||
                !long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        if (hasFrames)
        {
            return TryBuildFromFrameFields(parts, fps, dropFrame, out value);
        }

        // HH:MM:SS.mmm or MM:SS.mmm; only the seconds field may carry a fraction.
        if (!TryParseSeconds(last, out Flicks seconds))
        {
            return false;
        }

        long minutes = parts[count - 2];
        long hours = count >= 3 ? parts[count - 3] : 0;
        if ((count >= 3 && minutes > 59) || seconds >= Flicks.OneSecond * 60)
        {
            return false;
        }

        value = seconds + new Flicks(((hours * 3600) + (minutes * 60)) * Flicks.PerSecond);
        return true;
    }

    private static bool TryBuildFromFrameFields(long[] parts, Rational fps, bool dropFrame, out Flicks value)
    {
        value = default;
        if (dropFrame && !SupportsDropFrame(fps))
        {
            return false;
        }

        long hours = parts[0];
        long minutes = parts[1];
        long seconds = parts[2];
        long frames = parts[3];

        int nominal = fps.NominalRate;
        if (minutes > 59 || seconds > 59 || frames >= nominal)
        {
            return false;
        }

        long frameNumber = (((hours * 3600) + (minutes * 60) + seconds) * nominal) + frames;
        if (dropFrame)
        {
            int dropped = nominal / 15;
            long totalMinutes = (hours * 60) + minutes;

            // The first frames of every minute that is not a multiple of ten do not exist.
            if (seconds == 0 && frames < dropped && minutes % 10 != 0)
            {
                return false;
            }

            frameNumber -= dropped * (totalMinutes - (totalMinutes / 10));
        }

        value = Flicks.FromFrames(frameNumber, fps);
        return true;
    }

    private static bool TryScaleFraction(ReadOnlySpan<char> fraction, out long flicks)
    {
        flicks = 0;
        if (fraction.IsEmpty)
        {
            return true;
        }

        if (fraction.Length > 18)
        {
            fraction = fraction[..18];
        }

        if (!long.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out long digits))
        {
            return false;
        }

        long scale = 1;
        for (int i = 0; i < fraction.Length; i++)
        {
            scale *= 10;
        }

        // Exact for up to five fractional digits; beyond that the flick grid is finer than the input.
        Int128 numerator = (Int128)digits * Flicks.PerSecond;
        Int128 quotient = numerator / scale;
        Int128 remainder = numerator - (quotient * scale);
        if (remainder * 2 >= scale)
        {
            quotient += 1;
        }

        flicks = (long)quotient;
        return true;
    }

    private static int SplitFields(ReadOnlySpan<char> s, Span<Range> fields)
    {
        int count = 0;
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] is ':' or ';')
            {
                if (count == fields.Length)
                {
                    return fields.Length + 1;
                }

                fields[count++] = new Range(start, i);
                start = i + 1;
            }
        }

        return count;
    }

    private static long ApplyDropFrameLabels(long frameNumber, int nominal)
    {
        int dropped = nominal / 15;
        long framesPerMinute = (nominal * 60L) - dropped;
        long framesPer10Minutes = (nominal * 600L) - (9L * dropped);

        long tenMinuteBlocks = frameNumber / framesPer10Minutes;
        long withinBlock = frameNumber % framesPer10Minutes;

        long adjusted = frameNumber + (dropped * 9 * tenMinuteBlocks);
        if (withinBlock > dropped)
        {
            adjusted += dropped * ((withinBlock - dropped) / framesPerMinute);
        }

        return adjusted;
    }
}
