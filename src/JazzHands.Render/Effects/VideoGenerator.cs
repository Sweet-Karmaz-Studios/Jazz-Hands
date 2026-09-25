using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects;

/// <summary>
/// The running half of a generator: a picture made from nothing, frame sized.
/// </summary>
/// <remarks>
/// A class with <see cref="GeneratorAttribute"/> and its <see cref="ParamAttribute"/>s. The clip
/// it makes is placed like any picture the size of the frame, so the clip's own transform, crop,
/// opacity, masks and effects all apply to it. <see cref="Render"/> writes every texel of a
/// frame-sized target at the working resolution, transparent where there is nothing; positions and
/// sizes in parameters are sequence pixels from the frame centre, times
/// <see cref="EffectContext.QualityScale"/>. One instance per device draws every clip of its type.
/// </remarks>
public abstract class VideoGenerator : IDisposable
{
    /// <summary>The shaders it draws with; empty for one that draws with Direct2D.</summary>
    public virtual ImmutableArray<PassDescriptor> Passes => [];

    /// <summary>Draws the picture for this frame into <paramref name="output"/>.</summary>
    public abstract void Render(EffectContext context, ParameterSet parameters, RenderTarget output);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the generator made itself.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// A generator whose picture changes with time by itself, with none of its parameters animated:
/// particles, a countdown, a clock, drifting noise. Motion blur draws it again at each moment
/// across the shutter; any other generator is drawn once and placed at each moment.
/// </summary>
public interface ITimedGenerator;
