using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Stabilize;

/// <summary>
/// Steadies a shaky camera, from a motion analysis of the clip's file (<c>clip.stabilize</c>).
/// </summary>
/// <remarks>
/// Not a pass over the picture: the render graph builder moves, turns and zooms the clip's layer
/// by the frame's correction (see <see cref="Core.Stabilization.CameraMotion"/>) before its
/// placement, and leaves this out of the effect chain. Without an analysis the clip is drawn as it
/// is. It is registered as an effect so it has parameters to set, an inspector section, and a place
/// in the chain a person can see and switch off.
/// </remarks>
[VideoEffect(StabilizeEffect.TypeId, Name = "Stabilize", Category = "Transform", Description = "Steadies a shaky camera by moving each frame back onto a smoothed path. Needs the clip's motion analysed (clip.stabilize).")]
[Param(StabilizeEffect.Smoothing, ParamType.Int, Default = "15", Min = 0, Max = 120, SliderMax = 60, Animatable = false, Description = "Frames either side the camera's path is smoothed over: more is steadier and keeps less of the camera's own movement. 0 is off.")]
[Param(StabilizeEffect.Zoom, ParamType.Float, Default = "0", Min = 0, Max = 100, SliderMax = 30, Unit = "%", Animatable = false, Description = "Zoom in on top of the automatic zoom, to hide the edges.")]
[Param(StabilizeEffect.AutoZoom, ParamType.Bool, Default = "true", Animatable = false, Description = "Zoom in just enough that the moving frame's edges never show.")]
public sealed class StabilizeEffect : VideoEffect
{
    /// <summary>The effect's type.</summary>
    public const string TypeId = "video.stabilize";

    /// <summary>The smoothing parameter.</summary>
    public const string Smoothing = "smoothing";

    /// <summary>The extra zoom parameter.</summary>
    public const string Zoom = "zoom";

    /// <summary>The automatic zoom parameter.</summary>
    public const string AutoZoom = "auto-zoom";

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes => [];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Copy(input, output);
    }
}
