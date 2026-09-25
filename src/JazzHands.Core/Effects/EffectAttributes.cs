namespace JazzHands.Core.Effects;

/// <summary>What an effect type is for, which decides where it may be added.</summary>
public enum EffectKind
{
    /// <summary>Turns a picture into another picture: on a picture clip, a video track or an adjustment layer.</summary>
    Video,

    /// <summary>Turns sound into other sound: on an audio clip or an audio track.</summary>
    Audio,

    /// <summary>Makes a picture from nothing. Its parameters live on the generator clip itself.</summary>
    Generator,

    /// <summary>Mixes the pictures of two adjacent clips across the cut between them.</summary>
    Transition,

    /// <summary>Mixes the sound of two adjacent clips across the cut between them: a crossfade.</summary>
    AudioTransition,

    /// <summary>Makes sound from nothing, on a sound track: a test tone, pink noise. Its parameters live on the generator clip itself.</summary>
    AudioGenerator,
}

/// <summary>The kind of value a parameter holds, which decides how it is typed, stored and drawn.</summary>
public enum ParamType
{
    /// <summary>A number: a slider with drag-scrub.</summary>
    Float,

    /// <summary>A pair of numbers that is not a place, such as a scale per axis.</summary>
    Float2,

    /// <summary>A place in sequence pixels from the frame centre, which the preview can pick.</summary>
    Point,

    /// <summary>Four numbers, such as a rectangle.</summary>
    Float4,

    /// <summary>A colour, linear and premultiplied; typed as sRGB hex.</summary>
    Color,

    /// <summary>A switch.</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>One of a fixed list of names.</summary>
    Enum,

    /// <summary>Free text, such as a file name.</summary>
    Text,

    /// <summary>An outline in SVG path syntax.</summary>
    Path,
}

/// <summary>
/// Marks a class as an effect type and names it. The registry finds these; nothing else
/// registers an effect.
/// </summary>
/// <param name="typeId">The stable identifier the project file stores, such as <c>video.blur.gaussian</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public abstract class EffectAttribute(string typeId) : Attribute
{
    /// <summary>The stable identifier.</summary>
    public string Id { get; } = typeId;

    /// <summary>What the effects browser calls it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The browser folder it sits in, such as Blur.</summary>
    public string Category { get; init; } = "Other";

    /// <summary>One sentence on what it does, for the browser and for an LLM choosing one.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Where it may be added.</summary>
    public abstract EffectKind Kind { get; }
}

/// <summary>Marks a picture effect.</summary>
/// <param name="typeId">The stable identifier, which starts <c>video.</c>.</param>
public sealed class VideoEffectAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.Video;

    /// <summary>
    /// How many earlier frames of its input the effect reads (an echo reads several). Nothing
    /// supplies them before Phase 29a; the contract has room so that effect does not change it.
    /// </summary>
    public int FramesBefore { get; init; }

    /// <summary>How many later frames of its input the effect reads.</summary>
    public int FramesAfter { get; init; }

    /// <summary>
    /// True for an effect with procedural state (particles, shake) that it simulates from a seed,
    /// so an export is bit exact and scrubbing backwards lands on the same frame.
    /// </summary>
    public bool Seeded { get; init; }
}

/// <summary>Marks a sound effect.</summary>
/// <param name="typeId">The stable identifier, which starts <c>audio.</c>.</param>
public sealed class AudioEffectAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.Audio;
}

/// <summary>Marks a generator, whose parameters are stored as an effect of its own type on its clip.</summary>
/// <param name="typeId">The stable identifier, which starts <c>gen.</c>.</param>
public sealed class GeneratorAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.Generator;
}

/// <summary>Marks a sound generator: a clip on a sound track that makes its sound, such as a test tone.</summary>
/// <param name="typeId">The stable identifier, which starts <c>audio.gen.</c>.</param>
public sealed class AudioGeneratorAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.AudioGenerator;
}

/// <summary>
/// Marks a picture transition, which reads the outgoing and the incoming clip's pictures and a
/// progress from 0 to 1. Every transition also takes the easing parameters of
/// <see cref="TransitionEasing"/>, which the registry adds after its own.
/// </summary>
/// <param name="typeId">The stable identifier, which starts <c>transition.</c>.</param>
public sealed class TransitionAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.Transition;
}

/// <summary>Marks a sound transition: a crossfade between two adjacent audio clips.</summary>
/// <param name="typeId">The stable identifier, which starts <c>transition.audio.</c>.</param>
public sealed class AudioTransitionAttribute(string typeId) : EffectAttribute(typeId)
{
    /// <inheritdoc />
    public override EffectKind Kind => EffectKind.AudioTransition;
}

/// <summary>
/// Declares one parameter of an effect, in the order they are shown.
/// </summary>
/// <remarks>
/// Defaults, limits and choices are written as the text the command line would take (<c>"8"</c>,
/// <c>"0, 0"</c>, <c>"#FFFFFF"</c>, <c>"both"</c>), because an attribute cannot hold a vector.
/// The registry reads them once, through the same parser the commands use, so a default that
/// would not parse fails the registry test rather than a frame.
/// </remarks>
/// <param name="name">The name the project file and the commands use, in kebab case.</param>
/// <param name="type">What kind of value it holds.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ParamAttribute(string name, ParamType type) : Attribute
{
    /// <summary>The name.</summary>
    public string Name { get; } = name;

    /// <summary>The kind of value.</summary>
    public ParamType Type { get; } = type;

    /// <summary>The value when nothing is set, as command-line text.</summary>
    public string Default { get; init; } = string.Empty;

    /// <summary>The smallest value accepted, for numbers; NaN for no limit.</summary>
    public double Min { get; init; } = double.NaN;

    /// <summary>The largest value accepted, for numbers; NaN for no limit.</summary>
    public double Max { get; init; } = double.NaN;

    /// <summary>
    /// The top of the slider when it is less than <see cref="Max"/>: a blur accepts 1000 pixels,
    /// but the slider that makes 0 to 1000 useful for the first 50 would be no use at all.
    /// </summary>
    public double SliderMax { get; init; } = double.NaN;

    /// <summary>The unit shown after the number: <c>px</c>, <c>deg</c>, <c>%</c>, <c>dB</c>.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>What the inspector calls it, when not the name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>One sentence on what it does.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>False for a parameter that cannot be keyframed.</summary>
    public bool Animatable { get; init; } = true;

    /// <summary>For an enum, the names it takes, comma separated.</summary>
    public string Choices { get; init; } = string.Empty;
}
