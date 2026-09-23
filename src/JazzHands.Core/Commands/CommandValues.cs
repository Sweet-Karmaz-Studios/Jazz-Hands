using System.Globalization;
using System.Text.Json;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>A frame size, as a command writes it: <c>1920x1080</c>, <c>1080p</c>, <c>4k</c>.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct FrameSize(int Width, int Height)
{
    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// Turns the text a person types into the values a command record holds, and back again.
/// </summary>
/// <remarks>
/// One place, because the same string has to mean the same thing whether it arrives from the
/// command line, a JSON-RPC call, an MCP tool argument, a <c>jazz apply</c> script or the GUI's
/// command console. A frame rate that parsed differently in two of those would be a bug nobody
/// found until an export went out of sync.
///
/// Times need a frame rate to parse, because <c>00:00:01:12</c> means different things at 24 and
/// at 60 fps. The registry passes the project's rate.
/// </remarks>
public static class CommandValues
{
    /// <summary>
    /// Parses a frame rate, refusing the decimal spellings of the broadcast rates.
    /// </summary>
    /// <remarks>
    /// 29.97 is not a frame rate; 30000/1001 is. The four decimal shorthands people actually type
    /// are recognised and turned into the exact ratio they mean. Any other decimal is refused
    /// rather than stored as an approximation that drifts by a frame every thousand frames.
    /// </remarks>
    public static bool TryParseFrameRate(string? text, out Rational rate, out string? error)
    {
        rate = default;
        error = null;

        string trimmed = (text ?? string.Empty).Trim();

        switch (trimmed)
        {
            case "23.976" or "23.98":
                rate = Rational.Fps23976;
                return true;
            case "29.97":
                rate = Rational.Fps2997;
                return true;
            case "59.94":
                rate = Rational.Fps5994;
                return true;
            case "119.88":
                rate = Rational.Fps11988;
                return true;
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            error = $"'{trimmed}' is not an exact frame rate. Write it as a ratio, for example 30000/1001.";
            return false;
        }

        if (!Rational.TryParse(trimmed, out rate) || rate.Num <= 0)
        {
            error = $"'{trimmed}' is not a frame rate. Try 30, 60, or 30000/1001.";
            return false;
        }

        return true;
    }

    /// <summary>Parses a frame rate or throws a coded command error.</summary>
    public static Rational ParseFrameRate(string? text) =>
        TryParseFrameRate(text, out Rational rate, out string? error)
            ? rate
            : throw new CommandException("invalid-frame-rate", error!);

    /// <summary>Parses a frame size, accepting the named shorthands.</summary>
    public static bool TryParseSize(string? text, out FrameSize size, out string? error)
    {
        size = default;
        error = null;

        string trimmed = (text ?? string.Empty).Trim().ToLowerInvariant();

        switch (trimmed)
        {
            case "720p":
                size = new FrameSize(1280, 720);
                return true;
            case "1080p" or "fhd":
                size = new FrameSize(1920, 1080);
                return true;
            case "1440p" or "2k":
                size = new FrameSize(2560, 1440);
                return true;
            case "4k" or "2160p" or "uhd":
                size = new FrameSize(3840, 2160);
                return true;
            case "8k" or "4320p":
                size = new FrameSize(7680, 4320);
                return true;
        }

        string[] parts = trimmed.Split('x');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            && width >= 16
            && height >= 16)
        {
            size = new FrameSize(width, height);
            return true;
        }

        error = $"'{text}' is not a frame size. Try 1920x1080, 1080p or 4k.";
        return false;
    }

    /// <summary>Parses a frame size or throws a coded command error.</summary>
    public static FrameSize ParseSize(string? text) =>
        TryParseSize(text, out FrameSize size, out string? error)
            ? size
            : throw new CommandException("invalid-frame-size", error!);

    /// <summary>
    /// Normalizes a colour into the <c>#RRGGBB</c> or <c>#RRGGBBAA</c> the model stores.
    /// </summary>
    /// <remarks>
    /// A handful of names are accepted because typing <c>--color red</c> is what people do. The
    /// list is deliberately short: this is a timeline label colour, not a palette.
    /// </remarks>
    public static bool TryParseColor(string? text, out string color, out string? error)
    {
        color = string.Empty;
        error = null;

        string trimmed = (text ?? string.Empty).Trim();

        color = trimmed.ToLowerInvariant() switch
        {
            "red" => "#E5484D",
            "orange" => "#F76B15",
            "yellow" => "#FFCC00",
            "green" => "#46A758",
            "blue" => "#3A6EA5",
            "purple" => "#8E4EC6",
            "pink" => "#E93D82",
            "grey" or "gray" => "#7E7E7E",
            "white" => "#FFFFFF",
            "black" => "#000000",
            _ => string.Empty,
        };

        if (color.Length > 0)
        {
            return true;
        }

        if (trimmed.StartsWith('#') && (trimmed.Length == 7 || trimmed.Length == 9) && IsHex(trimmed[1..]))
        {
            color = "#" + trimmed[1..].ToUpperInvariant();
            return true;
        }

        error = $"'{text}' is not a colour. Try #RRGGBB, #RRGGBBAA, or a name such as red.";
        return false;
    }

    /// <summary>Parses a colour or throws a coded command error.</summary>
    public static string ParseColor(string? text) =>
        TryParseColor(text, out string color, out string? error)
            ? color
            : throw new CommandException("invalid-color", error!);

    /// <summary>Checks an identifier and throws a coded command error if it is not one.</summary>
    public static string ParseId(string? text) => Id.IsValid(text)
        ? text!
        : throw new CommandException("invalid-id", $"'{text}' is not a ULID. Use 'jazz ids new' for a fresh one.");

    /// <summary>
    /// Turns one piece of command-line text into the type a command property holds.
    /// </summary>
    /// <param name="target">The property type.</param>
    /// <param name="text">What the user typed.</param>
    /// <param name="frameRate">The rate timecode is read against.</param>
    /// <param name="name">The parameter name, for the error message.</param>
    public static object? Parse(Type target, string? text, Rational frameRate, string name = "value")
    {
        ArgumentNullException.ThrowIfNull(target);

        Type type = Nullable.GetUnderlyingType(target) ?? target;

        if (text is null || (text.Length == 0 && type != typeof(string)))
        {
            return Nullable.GetUnderlyingType(target) is not null || !type.IsValueType
                ? null
                : throw new CommandException("missing-value", $"'{name}' needs a value.");
        }

        if (type == typeof(string))
        {
            return text;
        }

        if (type == typeof(Flicks))
        {
            return Timecode.TryParse(text, frameRate, out Flicks time)
                ? time
                : throw new CommandException(
                    "invalid-time",
                    $"'{text}' is not a time. Try 00:00:01.500, 00:00:01:12, 90f, 1.5s or 123456789fl.");
        }

        if (type == typeof(Rational))
        {
            return ParseFrameRate(text);
        }

        if (type == typeof(FrameSize))
        {
            return ParseSize(text);
        }

        if (type == typeof(bool))
        {
            // On and off as well, because "jazz playback loop on" is what anyone would type.
            if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return bool.TryParse(text, out bool flag)
                ? flag
                : throw new CommandException("invalid-value", $"'{name}' takes true or false (or on or off), not '{text}'.");
        }

        if (type == typeof(int))
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? number
                : throw new CommandException("invalid-value", $"'{name}' takes a whole number, not '{text}'.");
        }

        if (type == typeof(double))
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number
                : throw new CommandException("invalid-value", $"'{name}' takes a number, not '{text}'.");
        }

        if (type.IsEnum)
        {
            // The command line and the project file both spell a multi-word member in kebab
            // case (ease-in-out), so the hyphens go before the name is matched. A digit is
            // refused, because Enum.TryParse would otherwise accept any number as a member.
            string bare = text.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

            return bare.Length > 0 && !char.IsAsciiDigit(bare[0]) && Enum.TryParse(type, bare, ignoreCase: true, out object? member)
                ? member
                : throw new CommandException(
                    "invalid-value",
                    $"'{text}' is not one of {string.Join(", ", Enum.GetNames(type).Select(JsonNamingPolicy.KebabCaseLower.ConvertName))}.");
        }

        if (type == typeof(string[]))
        {
            return Split(text);
        }

        if (type == typeof(EquatableArray<string>))
        {
            return new EquatableArray<string>(Split(text));
        }

        if (type == typeof(EquatableArray<TimeRange>))
        {
            return new EquatableArray<TimeRange>([.. Split(text).Select(pair => ParseRange(pair, frameRate, name))]);
        }

        throw new CommandException("unsupported-type", $"A command cannot take a {type.Name}.");
    }

    /// <summary>
    /// Writes a value back as the text a person would type for it.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="Parse"/>, used by help, by the round-trip tests and by anything
    /// that shows a command as a command line. Times come out as a clock rather than a flick
    /// count, because that is what a person reads.
    /// </remarks>
    public static string Format(object? value, Rational frameRate) => value switch
    {
        null => string.Empty,

        // A clock is what a person reads, but it only holds milliseconds. A time that does not
        // land on one is written as flicks instead, so that writing a command out and reading it
        // back gives the same command rather than one that is a few hundred microseconds off.
        Flicks time => time.Value % Flicks.PerMillisecond == 0
            ? Timecode.FormatClock(time)
            : $"{time.Value}fl",
        Rational rate => rate.ToString(),
        FrameSize size => size.ToString(),
        bool flag => flag ? "true" : "false",
        string[] items => string.Join(",", items),
        EquatableArray<string> items => string.Join(",", items),
        EquatableArray<TimeRange> ranges => string.Join(",", ranges.Select(range => $"{Format(range.Start, frameRate)}-{Format(range.End, frameRate)}")),
        Enum member => ToKebabCase(member.ToString()),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Reads one <c>start-end</c> pair. Neither half of any time form Jazz Hands reads contains a
    /// hyphen, so the first one is the separator.
    /// </summary>
    private static TimeRange ParseRange(string pair, Rational frameRate, string name)
    {
        int dash = pair.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == pair.Length - 1)
        {
            throw new CommandException(
                "invalid-range",
                $"'{pair}' is not a range for '{name}'. Write start-end, for example 00:10-00:25.");
        }

        var start = (Flicks)Parse(typeof(Flicks), pair[..dash].Trim(), frameRate, name)!;
        var end = (Flicks)Parse(typeof(Flicks), pair[(dash + 1)..].Trim(), frameRate, name)!;

        return end > start
            ? TimeRange.FromBounds(start, end)
            : throw new CommandException("invalid-range", $"'{pair}' ends before it starts.");
    }

    /// <summary>Turns a PascalCase property name into the kebab-case an option uses.</summary>
    public static string ToKebabCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            return name;
        }

        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string[] Split(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsHex(string text)
    {
        foreach (char character in text)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
