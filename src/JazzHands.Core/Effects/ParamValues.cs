using System.Globalization;
using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.Core.Effects;

/// <summary>
/// Text to parameter values and back, and the checks every surface applies to them.
/// </summary>
/// <remarks>
/// The one place a parameter value is read from what a person typed: <c>jazz param set</c>, a
/// JSON-RPC call, an MCP tool and the inspector's text boxes all come through here, so "12",
/// "100, 50" and "#FF8800" mean the same thing wherever they are typed. Colours are typed as sRGB
/// with straight alpha, which is what a colour picker or a web colour gives, and stored linear
/// and premultiplied, which is what the compositor works in.
/// </remarks>
public static class ParamValues
{
    /// <summary>Reads a value for a parameter, or throws a coded command error that says what it takes.</summary>
    public static ParamValue Parse(ParamDescriptor descriptor, string? text)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!TryParse(descriptor, text, out ParamValue? value, out string? error))
        {
            throw new CommandException("invalid-value", error!);
        }

        return value!;
    }

    /// <summary>Reads a value for a parameter.</summary>
    /// <returns>False with a sentence saying what the parameter takes when the text is not one.</returns>
    public static bool TryParse(ParamDescriptor descriptor, string? text, out ParamValue? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        value = null;
        error = null;
        string trimmed = (text ?? string.Empty).Trim();
        string name = descriptor.Name;

        switch (descriptor.Type)
        {
            case ParamType.Float:
                if (TryNumbers(trimmed, 1, out Vector4 single))
                {
                    value = new ParamValue.Float(single.X);
                    return true;
                }

                error = $"'{name}' takes a number, not '{text}'.";
                return false;

            case ParamType.Float2:
            case ParamType.Point:
                // One number for both axes, because "scale 2" is what anyone types.
                if (TryNumbers(trimmed, 2, out Vector4 pair) || TryNumbers(trimmed, 1, out pair))
                {
                    value = new ParamValue.Float2(pair.X, CountNumbers(trimmed) == 1 ? pair.X : pair.Y);
                    return true;
                }

                error = $"'{name}' takes two numbers, x and y, such as '100, 50', not '{text}'.";
                return false;

            case ParamType.Float4:
                if (TryNumbers(trimmed, 4, out Vector4 quad))
                {
                    value = new ParamValue.Float4(quad);
                    return true;
                }

                error = $"'{name}' takes four numbers, such as '0, 0, 100, 100', not '{text}'.";
                return false;

            case ParamType.Color:
                if (TryParseColor(trimmed, out Vector4 linear))
                {
                    value = new ParamValue.Color(linear);
                    return true;
                }

                error = $"'{name}' takes a colour: '#RRGGBB', '#RRGGBBAA', or linear 'rgba(r, g, b, a)', not '{text}'.";
                return false;

            case ParamType.Bool:
                if (string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
                {
                    value = new ParamValue.Bool(true);
                    return true;
                }

                if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                {
                    value = new ParamValue.Bool(false);
                    return true;
                }

                error = $"'{name}' takes true or false (or on or off), not '{text}'.";
                return false;

            case ParamType.Int:
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
                {
                    value = new ParamValue.Int(whole);
                    return true;
                }

                error = $"'{name}' takes a whole number, not '{text}'.";
                return false;

            case ParamType.Enum:
                foreach (string choice in descriptor.Choices)
                {
                    if (string.Equals(choice, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        value = new ParamValue.Enum(choice);
                        return true;
                    }
                }

                error = $"'{name}' is one of {string.Join(", ", descriptor.Choices)}, not '{text}'.";
                return false;

            case ParamType.Path:
                value = new ParamValue.Path(text ?? string.Empty);
                return true;

            default:
                value = new ParamValue.Text(text ?? string.Empty);
                return true;
        }
    }

    /// <summary>Writes a value as the text <see cref="Parse"/> reads back.</summary>
    public static string Format(ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            ParamValue.Float number => Number(number.Value),
            ParamValue.Float2 pair => $"{Number(pair.Value.X)}, {Number(pair.Value.Y)}",
            ParamValue.Float4 quad => $"{Number(quad.Value.X)}, {Number(quad.Value.Y)}, {Number(quad.Value.Z)}, {Number(quad.Value.W)}",
            ParamValue.Color colour => FormatColor(colour.Value),
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// A stored value as the parameter's type, or null when it cannot be one.
    /// </summary>
    /// <remarks>
    /// The project file does not say what type a value is: <c>"radius": 8</c> reads as a float
    /// and <c>"direction": "both"</c> as text. This is where they become what the parameter
    /// declares, so a hand-edited file works and an evaluator never sees a mismatch.
    /// </remarks>
    public static ParamValue? Coerce(ParamDescriptor descriptor, ParamValue? value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return (descriptor.Type, value) switch
        {
            (_, null) => null,
            (ParamType.Float, ParamValue.Float) => value,
            (ParamType.Float, ParamValue.Int whole) => new ParamValue.Float(whole.Value),
            (ParamType.Float2 or ParamType.Point, ParamValue.Float2) => value,
            (ParamType.Float2 or ParamType.Point, ParamValue.Float single) => new ParamValue.Float2(single.Value, single.Value),
            (ParamType.Float4, ParamValue.Float4) => value,
            (ParamType.Float4, ParamValue.Color colour) => new ParamValue.Float4(colour.Value),
            (ParamType.Color, ParamValue.Color) => value,
            (ParamType.Color, ParamValue.Float4 quad) => new ParamValue.Color(quad.Value),
            (ParamType.Color, ParamValue.Text text) when TryParseColor(text.Value, out Vector4 linear) => new ParamValue.Color(linear),
            (ParamType.Bool, ParamValue.Bool) => value,
            (ParamType.Bool, ParamValue.Float number) => new ParamValue.Bool(number.Value != 0.0f),
            (ParamType.Int, ParamValue.Int) => value,
            (ParamType.Int, ParamValue.Float number) when float.IsFinite(number.Value) => new ParamValue.Int((int)MathF.Round(number.Value)),
            (ParamType.Enum, ParamValue.Enum member) => Choice(descriptor, member.Value),
            (ParamType.Enum, ParamValue.Text text) => Choice(descriptor, text.Value),
            (ParamType.Text, ParamValue.Text) => value,
            (ParamType.Text, ParamValue.Path path) => new ParamValue.Text(path.Value),
            (ParamType.Text, ParamValue.Enum member) => new ParamValue.Text(member.Value),
            (ParamType.Path, ParamValue.Path) => value,
            (ParamType.Path, ParamValue.Text text) => new ParamValue.Path(text.Value),
            _ => null,
        };
    }

    /// <summary>A value held inside the parameter's limits, for evaluation of a hand-edited file.</summary>
    public static ParamValue Clamp(ParamDescriptor descriptor, ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);

        if (descriptor.Min is null && descriptor.Max is null)
        {
            return value;
        }

        float low = (float)(descriptor.Min ?? double.NegativeInfinity);
        float high = (float)(descriptor.Max ?? double.PositiveInfinity);

        return value switch
        {
            ParamValue.Float number when number.Value < low || number.Value > high => new ParamValue.Float(Math.Clamp(number.Value, low, high)),
            ParamValue.Int whole when whole.Value < low || whole.Value > high => new ParamValue.Int((int)Math.Clamp(whole.Value, low, high)),
            ParamValue.Float2 pair when Outside(pair.Value.X, low, high) || Outside(pair.Value.Y, low, high) =>
                new ParamValue.Float2(Math.Clamp(pair.Value.X, low, high), Math.Clamp(pair.Value.Y, low, high)),
            _ => value,
        };
    }

    /// <summary>
    /// Refuses a value outside the parameter's limits with a coded command error. Commands check;
    /// evaluation clamps, because a hand-edited file should still play.
    /// </summary>
    public static void Check(ParamDescriptor descriptor, ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);

        float[] components = value switch
        {
            ParamValue.Float number => [number.Value],
            ParamValue.Int whole => [whole.Value],
            ParamValue.Float2 pair => [pair.Value.X, pair.Value.Y],
            ParamValue.Float4 quad => [quad.Value.X, quad.Value.Y, quad.Value.Z, quad.Value.W],
            ParamValue.Color colour => [colour.Value.X, colour.Value.Y, colour.Value.Z, colour.Value.W],
            _ => [],
        };

        foreach (float component in components)
        {
            if (!float.IsFinite(component))
            {
                throw new CommandException("value-out-of-range", $"'{descriptor.Name}' has to be a number.");
            }

            if (descriptor.Min is { } min && component < min)
            {
                throw new CommandException("value-out-of-range", $"'{descriptor.Name}' runs from {Range(descriptor)}; {Format(value)} is below that.");
            }

            if (descriptor.Max is { } max && component > max)
            {
                throw new CommandException("value-out-of-range", $"'{descriptor.Name}' runs from {Range(descriptor)}; {Format(value)} is above that.");
            }
        }
    }

    /// <summary>
    /// Reads a colour: <c>#RRGGBB</c> or <c>#RRGGBBAA</c> in sRGB with straight alpha, or
    /// <c>rgba(r, g, b, a)</c> and four bare numbers already linear and premultiplied, which is
    /// what <see cref="ParamValue.Color.ToString"/> writes.
    /// </summary>
    /// <returns>Linear, premultiplied RGBA.</returns>
    public static bool TryParseColor(string? text, out Vector4 linear)
    {
        linear = default;
        string trimmed = (text ?? string.Empty).Trim();

        if (trimmed.StartsWith('#'))
        {
            string hex = trimmed[1..];
            if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            {
                return false;
            }

            if (hex.Length == 6)
            {
                packed = (packed << 8) | 0xFF;
            }

            float alpha = (packed & 0xFF) / 255.0f;
            linear = new Vector4(
                SrgbToLinear(((packed >> 24) & 0xFF) / 255.0f) * alpha,
                SrgbToLinear(((packed >> 16) & 0xFF) / 255.0f) * alpha,
                SrgbToLinear(((packed >> 8) & 0xFF) / 255.0f) * alpha,
                alpha);
            return true;
        }

        if (trimmed.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[5..^1];
        }

        return TryNumbers(trimmed, 4, out linear);
    }

    /// <summary>
    /// A linear premultiplied colour as sRGB hex with straight alpha, the alpha left off when
    /// opaque. Eight bits a channel, so this is for showing a colour, not for storing one.
    /// </summary>
    public static string FormatColor(Vector4 linear)
    {
        float alpha = Math.Clamp(linear.W, 0.0f, 1.0f);
        byte Channel(float premultiplied) => alpha <= 0.0f
            ? (byte)0
            : (byte)Math.Round(Math.Clamp(LinearToSrgb(premultiplied / alpha), 0.0f, 1.0f) * 255.0f);

        string rgb = FormattableString.Invariant($"#{Channel(linear.X):X2}{Channel(linear.Y):X2}{Channel(linear.Z):X2}");
        return alpha >= 1.0f ? rgb : FormattableString.Invariant($"{rgb}{(byte)Math.Round(alpha * 255.0f):X2}");
    }

    /// <summary>The sRGB transfer function, decoded.</summary>
    public static float SrgbToLinear(float encoded) =>
        encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    /// <summary>The sRGB transfer function, encoded.</summary>
    public static float LinearToSrgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1.0f / 2.4f)) - 0.055f;

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool Outside(float value, float low, float high) => value < low || value > high;

    private static string Range(ParamDescriptor descriptor) =>
        FormattableString.Invariant($"{descriptor.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"} to {descriptor.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"}{(descriptor.Unit.Length > 0 ? " " + descriptor.Unit : string.Empty)}");

    private static ParamValue.Enum? Choice(ParamDescriptor descriptor, string text)
    {
        foreach (string choice in descriptor.Choices)
        {
            if (string.Equals(choice, text, StringComparison.OrdinalIgnoreCase))
            {
                return new ParamValue.Enum(choice);
            }
        }

        return null;
    }

    private static int CountNumbers(string text) =>
        text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>Exactly <paramref name="count"/> numbers separated by commas or spaces.</summary>
    private static bool TryNumbers(string text, int count, out Vector4 numbers)
    {
        numbers = default;
        string[] parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            return false;
        }

        Span<float> values = stackalloc float[4];
        for (int index = 0; index < count; index++)
        {
            if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) || !float.IsFinite(values[index]))
            {
                return false;
            }
        }

        numbers = new Vector4(values[0], values[1], values[2], values[3]);
        return true;
    }
}
