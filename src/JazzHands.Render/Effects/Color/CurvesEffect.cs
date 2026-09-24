using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render.Effects.Color;

/// <summary>
/// Tone curves (master, then red, green and blue) and hue and saturation curves, baked into a
/// small float texture whenever they change.
/// </summary>
/// <remarks>
/// A curve is text, <c>"0,0 0.3,0.25 1,1"</c>, read by <see cref="CurvePoints"/>. The seven are
/// baked into 1024 by 2 texels, four curves a row, and kept by their text: every clip with the
/// same curves shares one texture, and nothing about a clip is held. Tone curves run on perceptual
/// values, where a curve drawn in any editor means what it looks like; values above 1 are held at
/// 1 for the curves. The hue and saturation curves are flat at 0.5 for no change: hue-vs-sat and
/// sat-vs-sat multiply saturation by twice their value, hue-vs-hue turns the hue by its value less
/// a half.
/// </remarks>
[VideoEffect("color.curves", Name = "Curves", Category = "Color", Description = "Tone curves on the master and on red, green and blue, and hue against saturation, hue against hue and saturation against saturation; each a list of x,y points from 0 to 1.")]
[Param("master", ParamType.Text, Default = "0,0 1,1", Animatable = false, Description = "All three channels: points as x,y pairs from 0 to 1, for example \"0,0 0.25,0.2 0.75,0.8 1,1\" for a gentle S.")]
[Param("red", ParamType.Text, Default = "0,0 1,1", Animatable = false, Description = "The red channel, after the master curve.")]
[Param("green", ParamType.Text, Default = "0,0 1,1", Animatable = false, Description = "The green channel, after the master curve.")]
[Param("blue", ParamType.Text, Default = "0,0 1,1", Animatable = false, Description = "The blue channel, after the master curve.")]
[Param("hue-vs-sat", ParamType.Text, Default = "0,0.5 1,0.5", Animatable = false, Description = "Saturation by hue: x is the hue (0 red, 1/3 green, 2/3 blue), y 0.5 leaves it, 1 doubles it, 0 greys it out.")]
[Param("hue-vs-hue", ParamType.Text, Default = "0,0.5 1,0.5", Animatable = false, Description = "Hue by hue: y 0.5 leaves a hue where it is; above or below turns it round the circle.")]
[Param("sat-vs-sat", ParamType.Text, Default = "0,0.5 1,0.5", Animatable = false, Description = "Saturation by saturation: y 0.5 leaves it, above boosts, below mutes, so muted colours can be lifted and loud ones calmed.")]
public sealed class CurvesEffect : GradingEffect
{
    /// <summary>Texels along each curve.</summary>
    public const int Resolution = 1024;

    private const int Kept = 8;
    private static readonly CurvePoints Flat = CurvePoints.Parse("0,0.5 1,0.5");

    private readonly Dictionary<string, Baked> _baked = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();

    /// <summary>Creates the effect.</summary>
    public CurvesEffect()
        : base("PsCurves")
    {
    }

    /// <summary>
    /// The seven curves as 1024 by 2 texels of four floats: row 0 master, red, green, blue; row 1
    /// hue-vs-sat, hue-vs-hue, sat-vs-sat, unused.
    /// </summary>
    public static float[] Bake(ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        float[][] curves =
        [
            CurvePoints.Parse(parameters.Text("master")).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("red")).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("green")).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("blue")).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("hue-vs-sat"), Flat, periodic: true).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("hue-vs-hue"), Flat, periodic: true).Bake(Resolution),
            CurvePoints.Parse(parameters.Text("sat-vs-sat"), Flat).Bake(Resolution),
            Flat.Bake(Resolution),
        ];

        float[] texels = new float[Resolution * 2 * 4];
        for (int row = 0; row < 2; row++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    texels[(((row * Resolution) + x) * 4) + channel] = curves[(row * 4) + channel][x];
                }
            }
        }

        return texels;
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters)
    {
        bool colour = !(CurvePoints.Parse(parameters.Text("hue-vs-sat"), Flat, periodic: true).IsNeutral(0.5f)
            && CurvePoints.Parse(parameters.Text("hue-vs-hue"), Flat, periodic: true).IsNeutral(0.5f)
            && CurvePoints.Parse(parameters.Text("sat-vs-sat"), Flat).IsNeutral(0.5f));
        return new GradingValues { FlagX = Flag(colour) };
    }

    /// <inheritdoc />
    protected override ID3D11ShaderResourceView Resource(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);

        string key = string.Join('|', parameters.Text("master"), parameters.Text("red"), parameters.Text("green"), parameters.Text("blue"), parameters.Text("hue-vs-sat"), parameters.Text("hue-vs-hue"), parameters.Text("sat-vs-sat"));
        if (_baked.TryGetValue(key, out Baked? known))
        {
            _recent.Remove(key);
            _recent.AddFirst(key);
            return known.View;
        }

        Baked baked = Upload(context.Device, Bake(parameters));
        _baked[key] = baked;
        _recent.AddFirst(key);

        while (_recent.Count > Kept)
        {
            string oldest = _recent.Last!.Value;
            _recent.RemoveLast();
            _baked.Remove(oldest, out Baked? gone);
            gone?.Dispose();
        }

        return baked.View;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Baked baked in _baked.Values)
            {
                baked.Dispose();
            }

            _baked.Clear();
            _recent.Clear();
        }

        base.Dispose(disposing);
    }

    private static unsafe Baked Upload(RenderDevice device, float[] texels)
    {
        var description = new Texture2DDescription
        {
            Width = Resolution,
            Height = 2,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Immutable,
        };

        fixed (float* data = texels)
        {
            ID3D11Texture2D texture = device.Device.CreateTexture2D(description, [new SubresourceData((IntPtr)data, Resolution * 4 * sizeof(float))]);
            return new Baked(texture, device.Device.CreateShaderResourceView(texture));
        }
    }

    private sealed record Baked(ID3D11Texture2D Texture, ID3D11ShaderResourceView View) : IDisposable
    {
        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }
}
