using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects;

/// <summary>
/// One shader an effect draws with: a file, its pixel entry point, and the vertex stage that
/// feeds it.
/// </summary>
/// <param name="File">The shader file in Render/Shaders, for example GaussianBlur.hlsl.</param>
/// <param name="Pixel">The pixel shader entry point.</param>
/// <param name="Vertex">The vertex shader entry point; the full-screen triangle from Effect.hlsli by default.</param>
/// <param name="Vertices">How many vertices are drawn: 3 for the full-screen triangle, 4 for a quad strip.</param>
public sealed record PassDescriptor(string File, string Pixel, string Vertex = "VsMain", int Vertices = 3)
{
    /// <summary>True for a four-vertex strip rather than a triangle list.</summary>
    public bool IsStrip => Vertices == 4;
}

/// <summary>
/// The running half of a picture effect: its shaders and how it draws them.
/// </summary>
/// <remarks>
/// A class with <see cref="VideoEffectAttribute"/> and <see cref="ParamAttribute"/> on it is
/// both the description (read by the registry, never instantiated for that) and the
/// implementation. The compositor makes one instance per device on first use and keeps it, so an
/// instance may hold state between frames; it must not hold anything about a particular clip,
/// because one instance runs every clip's copy of the effect. What differs per clip comes in the
/// <see cref="ParameterSet"/> and the <see cref="EffectContext"/>.
///
/// Everything an effect draws goes through <see cref="EffectContext.Draw{T}"/>, which binds the
/// common constants at b0, the effect's at b1, the inputs from t0 and the three samplers, and
/// compiles the pass's shaders once per shader generation. Intermediate targets are rented from
/// the context and returned before <see cref="Apply"/> ends.
/// </remarks>
public abstract class VideoEffect : IDisposable
{
    /// <summary>The shaders it uses, for listing and for compiling ahead of the first frame.</summary>
    public abstract ImmutableArray<PassDescriptor> Passes { get; }

    /// <summary>
    /// Draws the effect: reads <paramref name="input"/> and writes the whole of
    /// <paramref name="output"/>, which is the same size and must not be read.
    /// </summary>
    /// <param name="context">The device, the pool, the quality, the time and the pass runner.</param>
    /// <param name="parameters">Every parameter, evaluated at this frame and inside its limits.</param>
    /// <param name="input">Premultiplied linear light, frame sized at the working resolution.</param>
    /// <param name="output">Where the result goes.</param>
    public abstract void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases anything the effect made itself. Shaders belong to the context.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>The picture effects, generators and their descriptors that this assembly defines.</summary>
public static class VideoEffects
{
    /// <summary>Every effect class in JazzHands.Render.</summary>
    public static EffectRegistry Registry { get; } = EffectRegistry.FromAssemblies(typeof(VideoEffects).Assembly);
}
