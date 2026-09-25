using System.Globalization;
using System.Numerics;

namespace JazzHands.Core.Model;

/// <summary>
/// The value of an animatable parameter.
/// </summary>
/// <remarks>
/// A closed union: effects, transforms, titles and masks all describe their parameters with these
/// and nothing else, which is what lets the inspector, the curve editor, the CLI and the JSON
/// schema all be written once rather than once per effect. Interpolation is defined per variant;
/// the discrete ones hold rather than blend, because half of "Screen" is not a blend mode.
/// </remarks>
public abstract record ParamValue
{
    /// <summary>True when values of this kind interpolate rather than hold between keyframes.</summary>
    public abstract bool IsContinuous { get; }

    /// <summary>A short name for error messages and the schema.</summary>
    public abstract string TypeName { get; }

    /// <summary>A scalar, for opacity, gain, angles and most effect parameters.</summary>
    /// <param name="Value">The scalar.</param>
    public sealed record Float(float Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => true;

        /// <inheritdoc />
        public override string TypeName => "float";

        /// <inheritdoc />
        public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A two component vector, for position, scale and anchor points.</summary>
    /// <param name="Value">The vector.</param>
    public sealed record Float2(Vector2 Value) : ParamValue
    {
        /// <summary>Creates a vector from components.</summary>
        public Float2(float x, float y)
            : this(new Vector2(x, y))
        {
        }

        /// <inheritdoc />
        public override bool IsContinuous => true;

        /// <inheritdoc />
        public override string TypeName => "float2";

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Value.X:R}, {Value.Y:R}");
    }

    /// <summary>A four component vector, for rectangles and generic shader parameters.</summary>
    /// <param name="Value">The vector.</param>
    public sealed record Float4(Vector4 Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => true;

        /// <inheritdoc />
        public override string TypeName => "float4";

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Value.X:R}, {Value.Y:R}, {Value.Z:R}, {Value.W:R}");
    }

    /// <summary>
    /// A colour in linear RGBA with premultiplied alpha, which is how the compositor works
    /// throughout. Colours entered as sRGB hex are converted on the way in, once.
    /// </summary>
    /// <param name="Value">Red, green, blue, alpha.</param>
    public sealed record Color(Vector4 Value) : ParamValue
    {
        /// <summary>Creates a colour from components.</summary>
        public Color(float red, float green, float blue, float alpha)
            : this(new Vector4(red, green, blue, alpha))
        {
        }

        /// <summary>Opaque black.</summary>
        public static Color Black => new(0.0f, 0.0f, 0.0f, 1.0f);

        /// <summary>Opaque white.</summary>
        public static Color White => new(1.0f, 1.0f, 1.0f, 1.0f);

        /// <summary>Fully transparent.</summary>
        public static Color Transparent => new(0.0f, 0.0f, 0.0f, 0.0f);

        /// <inheritdoc />
        public override bool IsContinuous => true;

        /// <inheritdoc />
        public override string TypeName => "color";

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"rgba({Value.X:R}, {Value.Y:R}, {Value.Z:R}, {Value.W:R})");
    }

    /// <summary>A switch. Holds between keyframes.</summary>
    /// <param name="Value">The flag.</param>
    public sealed record Bool(bool Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => false;

        /// <inheritdoc />
        public override string TypeName => "bool";

        /// <inheritdoc />
        public override string ToString() => Value ? "true" : "false";
    }

    /// <summary>A whole number, for counts and indices. Holds between keyframes.</summary>
    /// <param name="Value">The number.</param>
    public sealed record Int(int Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => false;

        /// <inheritdoc />
        public override string TypeName => "int";

        /// <inheritdoc />
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One of a named set, for modes and presets. Holds between keyframes.</summary>
    /// <param name="Value">The member name.</param>
    public sealed record Enum(string Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => false;

        /// <inheritdoc />
        public override string TypeName => "enum";

        /// <inheritdoc />
        public override string ToString() => Value;
    }

    /// <summary>A vector path, for masks. Morphs between keyframes with the same number of figures (<see cref="Animation.MaskShapes"/>).</summary>
    /// <param name="Value">The path, in SVG path syntax.</param>
    public sealed record Path(string Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => true;

        /// <inheritdoc />
        public override string TypeName => "path";

        /// <inheritdoc />
        public override string ToString() => Value;
    }

    /// <summary>A file or text reference, for title text and LUT paths. Holds between keyframes.</summary>
    /// <param name="Value">The text.</param>
    public sealed record Text(string Value) : ParamValue
    {
        /// <inheritdoc />
        public override bool IsContinuous => false;

        /// <inheritdoc />
        public override string TypeName => "text";

        /// <inheritdoc />
        public override string ToString() => Value;
    }
}
