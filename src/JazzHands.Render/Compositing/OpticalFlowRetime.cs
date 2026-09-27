using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Render.Effects;
using Vortice.DXGI;

namespace JazzHands.Render.Compositing;

/// <summary>
/// Optical flow retiming (Phase 42): the picture between two source frames of a slowed clip made
/// by moving both along the motion between them (<see cref="Core.Model.RetimeMode.OpticalFlow"/>).
/// </summary>
/// <remarks>
/// <para>
/// Our own flow, on the GPU in pixel shaders (OpticalFlow.hlsl), rather than a neural network:
/// RIFE, the model the phase named, publishes no ONNX of its own, and this needs no download,
/// runs on any Direct3D 11 device including WARP, and keeps every frame on the GPU. Flow is found
/// at half the frame's size on a luma pyramid down to about 24 texels, coarse to fine, by
/// <see cref="Iterations"/> Lucas-Kanade steps a level each way, each followed by a median; the
/// in-between frame reads both frames along the flows and leans on whichever shows what the other
/// hides.
/// </para>
/// <para>
/// One runner serves every clip and device: it holds nothing between frames, renting what it needs
/// from the context's pool and giving it back.
/// </para>
/// </remarks>
public sealed class OpticalFlowRetime : ITransitionRunner
{
    /// <summary>Lucas-Kanade steps at each pyramid level.</summary>
    public const int Iterations = 3;

    private static readonly PassDescriptor Luma = new("OpticalFlow.hlsl", "PsLuma");
    private static readonly PassDescriptor Down = new("OpticalFlow.hlsl", "PsDown");
    private static readonly PassDescriptor Upsample = new("OpticalFlow.hlsl", "PsUpsample");
    private static readonly PassDescriptor LucasKanade = new("OpticalFlow.hlsl", "PsLucasKanade");
    private static readonly PassDescriptor Median = new("OpticalFlow.hlsl", "PsMedian");
    private static readonly PassDescriptor Interpolate = new("OpticalFlow.hlsl", "PsInterpolate");

    /// <summary>The one runner.</summary>
    public static OpticalFlowRetime Instance { get; } = new();

    /// <summary>Every pass, for compiling ahead.</summary>
    public static ImmutableArray<PassDescriptor> Passes { get; } = [Luma, Down, Upsample, LucasKanade, Median, Interpolate];

    /// <inheritdoc />
    public void Apply(EffectContext context, float progress, RenderTarget outgoing, RenderTarget incoming, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(output);

        // The pyramid of lumas, the first frame's in red and the second's in green.
        var levels = new List<RenderTarget>();
        int width = Math.Max(1, (output.Width + 1) / 2);
        int height = Math.Max(1, (output.Height + 1) / 2);
        RenderTarget top = context.Rent(width, height, Format.R16G16_Float);
        context.Draw(Luma, top, default(FlowConstants), outgoing, incoming);
        levels.Add(top);
        while (Math.Min(width, height) >= 48)
        {
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            RenderTarget next = context.Rent(width, height, Format.R16G16_Float);
            context.Draw(Down, next, default(FlowConstants), levels[^1]);
            levels.Add(next);
        }

        // Coarse to fine: each level starts from the one below's flow, doubled.
        RenderTarget? flow = null;
        for (int level = levels.Count - 1; level >= 0; level--)
        {
            RenderTarget lumas = levels[level];
            RenderTarget current = context.Rent(lumas.Width, lumas.Height, Format.R32G32B32A32_Float);
            if (flow is null)
            {
                context.Clear(current, Vector4.Zero);
            }
            else
            {
                context.Draw(Upsample, current, default(FlowConstants), lumas, lumas, flow);
                context.Return(flow);
            }

            var constants = new FlowConstants { Radius = 3, Lambda = 1e-4f, MaxStep = 2.0f };
            RenderTarget spare = context.Rent(lumas.Width, lumas.Height, Format.R32G32B32A32_Float);
            for (int step = 0; step < Iterations; step++)
            {
                context.Draw(LucasKanade, spare, constants, lumas, lumas, current);
                context.Draw(Median, current, constants, lumas, lumas, spare);
            }

            context.Return(spare);
            flow = current;
        }

        foreach (RenderTarget level in levels)
        {
            context.Return(level);
        }

        context.Draw(
            Interpolate,
            output,
            new FlowConstants { Progress = Math.Clamp(progress, 0.0f, 1.0f), FlowScale = output.Width / (float)flow!.Width },
            outgoing,
            incoming,
            flow);
        context.Return(flow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlowConstants
    {
        public float Progress;
        public int Radius;
        public float Lambda;
        public float MaxStep;
        public float FlowScale;
        public Vector3 Padding;
    }
}
