using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects;

/// <summary>
/// The constant buffer the family shader files share (Stylize.hlsl, Distort.hlsl and others): two
/// float4s and four flags, each effect using what it needs. One layout means one buffer and one
/// place to get the HLSL packing right.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct EffectValues
{
    /// <summary>The first four numbers.</summary>
    public Vector4 Values;

    /// <summary>Four more.</summary>
    public Vector4 More;

    /// <summary>The first flag.</summary>
    public uint FlagX;

    /// <summary>The second flag.</summary>
    public uint FlagY;

    /// <summary>The third flag.</summary>
    public uint FlagZ;

    /// <summary>The fourth flag.</summary>
    public uint FlagW;

    /// <summary>
    /// Four numbers after the flags, for shaders that need a third vector (the generators). A
    /// shader that declares only the first three members reads past none of this.
    /// </summary>
    public Vector4 Extra;
}

/// <summary>
/// An effect that is one pixel shader over its input: most of the library. The subclass says
/// which entry point and what goes in the constants, and when the effect would change nothing,
/// so that case is a copy.
/// </summary>
public abstract class SinglePassEffect : VideoEffect
{
    private readonly PassDescriptor _pass;

    /// <summary>Creates the effect over one entry point of a shader file.</summary>
    protected SinglePassEffect(string file, string pixel)
    {
        _pass = new PassDescriptor(file, pixel);
        Passes = [_pass];
    }

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; }

    /// <inheritdoc />
    public sealed override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (PassesThrough(context, parameters))
        {
            context.Copy(input, output);
            return;
        }

        EffectValues values = Values(context, parameters, input);
        context.Draw(_pass, output, in values, input);
    }

    /// <summary>The constants for this frame.</summary>
    protected abstract EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input);

    /// <summary>True when the effect would change nothing at these values, so it is a copy.</summary>
    protected virtual bool PassesThrough(EffectContext context, ParameterSet parameters) => false;

    /// <summary>A switch as a flag.</summary>
    protected static uint Flag(bool value) => value ? 1u : 0u;

    /// <summary>The index of an enum's value among its choices, as a flag.</summary>
    protected static uint Choice(ParameterSet parameters, string name)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ParamDescriptor descriptor = parameters.Descriptor.Param(name)!;
        int index = descriptor.Choices.IndexOf(choice => string.Equals(choice, parameters.Enum(name), StringComparison.Ordinal));
        return (uint)Math.Max(0, index);
    }
}
