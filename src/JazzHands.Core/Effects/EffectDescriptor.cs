using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Effects;

/// <summary>
/// Everything known about one parameter: its type, default, limits and how to show it.
/// </summary>
/// <remarks>
/// One description serves every surface. The inspector draws a control from it, the commands
/// parse and range check against it, the MCP tool schema and <c>jazz effect list</c> print it,
/// and evaluation falls back to its default when the project has no value. Effect parameters,
/// a clip's own transform and opacity, a track's volume and a mask's feather are all described
/// the same way, so every one of them is keyframed by the same commands.
/// </remarks>
/// <param name="Name">The name, in kebab case, as the project file and the commands spell it.</param>
/// <param name="Type">What kind of value it holds.</param>
/// <param name="Default">Its value when nothing is set.</param>
/// <param name="Label">What the inspector calls it.</param>
/// <param name="Description">One sentence on what it does.</param>
/// <param name="Min">The smallest value accepted, for numbers.</param>
/// <param name="Max">The largest value accepted, for numbers.</param>
/// <param name="SliderMax">The top of the slider, when lower than <paramref name="Max"/>.</param>
/// <param name="Unit">The unit shown after the number.</param>
/// <param name="Animatable">False for a parameter that cannot be keyframed.</param>
/// <param name="Choices">For an enum, the names it takes.</param>
public sealed record ParamDescriptor(
    string Name,
    ParamType Type,
    ParamValue Default,
    string Label = "",
    string Description = "",
    double? Min = null,
    double? Max = null,
    double? SliderMax = null,
    string Unit = "",
    bool Animatable = true,
    EquatableArray<string> Choices = default)
{
    /// <summary>The label, or the name made readable when there is none.</summary>
    public string DisplayName => Label.Length > 0 ? Label : Readable(Name);

    /// <summary>The low end of the slider: the minimum, or the slider's top mirrored when the minimum is far below it, as for a rotation.</summary>
    public double SliderLow => Min is { } low && SliderMax is { } top && low < -top ? -top : Min ?? 0.0;

    /// <summary>The high end of the slider.</summary>
    public double SliderHigh => SliderMax ?? Max ?? 100.0;

    /// <summary>The first letter up and the hyphens and dots as spaces: <c>repeat-edges</c> reads Repeat edges.</summary>
    internal static string Readable(string name)
    {
        int dot = name.LastIndexOf('.');
        string last = dot >= 0 ? name[(dot + 1)..] : name;
        string spaced = last.Replace('-', ' ');
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}

/// <summary>
/// Everything known about one effect type: what it is called, where it goes and what it takes.
/// </summary>
/// <param name="TypeId">The stable identifier the project file stores.</param>
/// <param name="Kind">Where it may be added.</param>
/// <param name="Name">What the browser calls it.</param>
/// <param name="Category">The browser folder.</param>
/// <param name="Description">One sentence on what it does.</param>
/// <param name="Params">Its parameters, in the order they are shown.</param>
public sealed record EffectDescriptor(
    string TypeId,
    EffectKind Kind,
    string Name,
    string Category,
    string Description,
    ImmutableArray<ParamDescriptor> Params)
{
    /// <summary>The class that runs it, for the render or audio layer to instantiate. Not serialized.</summary>
    public Type? Implementation { get; init; }

    /// <summary>How many earlier input frames it reads. See <see cref="VideoEffectAttribute.FramesBefore"/>.</summary>
    public int FramesBefore { get; init; }

    /// <summary>How many later input frames it reads.</summary>
    public int FramesAfter { get; init; }

    /// <summary>True when it simulates procedural state from a seed.</summary>
    public bool Seeded { get; init; }

    /// <summary>The parameter with a name, or null.</summary>
    public ParamDescriptor? Param(string name)
    {
        foreach (ParamDescriptor parameter in Params)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>Where a parameter is in <see cref="Params"/>, or -1.</summary>
    public int IndexOf(string name)
    {
        for (int index = 0; index < Params.Length; index++)
        {
            if (string.Equals(Params[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
