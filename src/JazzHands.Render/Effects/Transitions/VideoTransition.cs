using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Transitions;

/// <summary>
/// The running half of a picture transition: its shaders and how it mixes two pictures.
/// </summary>
/// <remarks>
/// Like a <see cref="VideoEffect"/>, a class with <see cref="TransitionAttribute"/> and
/// <see cref="ParamAttribute"/>s is both the description and the implementation, made once per
/// device and kept. It reads the outgoing picture at t0 and the incoming one at t1, both
/// premultiplied linear light and frame sized, and writes the mix. Progress arrives eased;
/// <see cref="EffectContext.Time"/> is the time from the start of the transition and
/// <see cref="EffectContext.OwnerLength"/> its length.
/// </remarks>
public abstract class VideoTransition : IDisposable
{
    /// <summary>The shaders it uses, compiled before its first frame.</summary>
    public abstract ImmutableArray<PassDescriptor> Passes { get; }

    /// <summary>Mixes the two pictures into <paramref name="output"/>, which is the same size and must not be read.</summary>
    /// <param name="context">The device, the pool, the quality, the time and the pass runner.</param>
    /// <param name="parameters">Every parameter at this frame, inside its limits.</param>
    /// <param name="progress">How far through the transition, 0 all outgoing to 1 all incoming, eased.</param>
    /// <param name="outgoing">The clip the transition leaves.</param>
    /// <param name="incoming">The clip it arrives at.</param>
    /// <param name="output">Where the mix goes.</param>
    public abstract void Apply(EffectContext context, ParameterSet parameters, float progress, RenderTarget outgoing, RenderTarget incoming, RenderTarget output);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases anything the transition made itself.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// The constants every transition shader gets at b1, as Transition.hlsli declares them: the
/// progress, a seed, a choice, a flag, eight numbers and a colour. One layout for the whole
/// catalogue and for transitions written outside the build.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TransitionValues
{
    /// <summary>How far through, 0 to 1, eased.</summary>
    public float Progress;

    /// <summary>A whole number under 2^24 for repeatable randomness.</summary>
    public float Seed;

    /// <summary>The index of the transition's main choice (a direction, a shape).</summary>
    public uint Mode;

    /// <summary>A switch.</summary>
    public uint Flag;

    /// <summary>The first four numbers.</summary>
    public Vector4 Values;

    /// <summary>Four more.</summary>
    public Vector4 More;

    /// <summary>A colour, premultiplied linear light.</summary>
    public Vector4 Tint;
}

/// <summary>A transition that is one pixel shader over its two inputs: the whole built-in catalogue but one.</summary>
public abstract class SinglePassTransition : VideoTransition
{
    private readonly PassDescriptor _pass;

    /// <summary>Creates the transition over one entry point of Transitions.hlsl.</summary>
    protected SinglePassTransition(string pixel, string file = "Transitions.hlsl")
    {
        _pass = new PassDescriptor(file, pixel);
        Passes = [_pass];
    }

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; }

    /// <inheritdoc />
    public sealed override void Apply(EffectContext context, ParameterSet parameters, float progress, RenderTarget outgoing, RenderTarget incoming, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(output);

        // The ends are the pictures themselves, whatever the shader would make of them.
        if (progress <= 0.0f)
        {
            context.Copy(outgoing, output);
            return;
        }

        if (progress >= 1.0f)
        {
            context.Copy(incoming, output);
            return;
        }

        TransitionValues values = Values(context, parameters, progress);
        values.Progress = progress;
        values.Seed = ((uint)context.Seed & 0xFFFFFFu) >> 1;
        context.Draw(_pass, output, in values, outgoing, incoming);
    }

    /// <summary>The constants for this frame; the progress and the seed are filled in after.</summary>
    protected abstract TransitionValues Values(EffectContext context, ParameterSet parameters, float progress);

    /// <summary>A switch as a flag.</summary>
    protected static uint Flag(bool value) => value ? 1u : 0u;

    /// <summary>The index of an enum's value among its choices.</summary>
    protected static uint Choice(ParameterSet parameters, string name)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ParamDescriptor descriptor = parameters.Descriptor.Param(name)!;
        int index = descriptor.Choices.IndexOf(choice => string.Equals(choice, parameters.Enum(name), StringComparison.Ordinal));
        return (uint)Math.Max(0, index);
    }
}
