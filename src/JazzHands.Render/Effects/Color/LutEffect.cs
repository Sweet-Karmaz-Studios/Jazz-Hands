using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Color;
using JazzHands.Render.Compositing;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render.Effects.Color;

/// <summary>A 3D LUT from a .cube file, as a creative look or a log to Rec.709 conversion.</summary>
/// <remarks>
/// The file is read once and kept on the GPU as a 3D texture, keyed by its full path and when it
/// was last written, so an edited LUT is read again and every clip using one file shares it. A
/// relative path is from the project's folder. A file that is missing or does not parse leaves the
/// picture as it is and is logged once, rather than failing every frame. The input domain says
/// what the LUT expects: sRGB-encoded values (most looks), linear light, or ARRI LogC3 with Rec.709
/// out (camera conversion LUTs).
/// </remarks>
[VideoEffect("color.lut", Name = "LUT", Category = "Color", Description = "Applies a 3D LUT from a .cube file: a look, or a camera's log to Rec.709 conversion, blended in by intensity.")]
[Param("file", ParamType.Text, Default = "", Animatable = false, Description = "The .cube file: a full path, or one relative to the project's folder.")]
[Param("intensity", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How much of the LUT's result to use: 0 none, 1 all.")]
[Param("domain", ParamType.Enum, Default = "srgb", Choices = "srgb, linear, logc", Animatable = false, Description = "What the LUT expects: sRGB-encoded values (most looks), linear light, or ARRI LogC3 in with Rec.709 out.")]
[Param("interpolation", ParamType.Enum, Default = "trilinear", Choices = "trilinear, tetrahedral", Animatable = false, Description = "How between entries is worked out: trilinear, or tetrahedral, smoother on steep LUTs.")]
public sealed class LutEffect : GradingEffect
{
    private const int Kept = 8;

    private readonly ILogger _log = Log.ForContext<LutEffect>();
    private readonly Dictionary<(string Path, DateTime Written), Loaded?> _loaded = [];
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the effect.</summary>
    public LutEffect()
        : base("PsLut")
    {
    }

    /// <summary>The LUT file a parameter names, made absolute against the project's folder.</summary>
    public static string? PathOf(string file, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        return Path.IsPathRooted(file) || projectFolder.Length == 0 ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(projectFolder, file));
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters)
    {
        Loaded lut = Find(context, parameters)!;
        return new GradingValues
        {
            A = new Vector4(parameters.Float("intensity"), lut.Size, 0, 0),
            B = new Vector4(lut.DomainMin, 0),
            C = new Vector4(lut.DomainMax, 0),
            FlagX = parameters.Enum("domain") switch
            {
                "linear" => 1u,
                "logc" => 2u,
                _ => 0u,
            },
            FlagY = Flag(parameters.Enum("interpolation") == "tetrahedral"),
        };
    }

    /// <inheritdoc />
    protected override ID3D11ShaderResourceView? Resource(EffectContext context, ParameterSet parameters) => Find(context, parameters)?.View;

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float("intensity") == 0 || Find(context, parameters) is null;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Loaded? loaded in _loaded.Values)
            {
                loaded?.Dispose();
            }

            _loaded.Clear();
        }

        base.Dispose(disposing);
    }

    private Loaded? Find(EffectContext context, ParameterSet parameters)
    {
        if (PathOf(parameters.Text("file"), context.ProjectFolder) is not { } path)
        {
            return null;
        }

        DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_loaded.TryGetValue((path, written), out Loaded? known))
        {
            return known;
        }

        Loaded? loaded = null;
        try
        {
            loaded = written == DateTime.MinValue
                ? Report(path, "is not there")
                : Upload(context.Device, CubeLut.Load(path));
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            Report(path, error.Message);
        }

        if (_loaded.Count >= Kept)
        {
            foreach (Loaded? old in _loaded.Values)
            {
                old?.Dispose();
            }

            _loaded.Clear();
        }

        _loaded[(path, written)] = loaded;
        return loaded;
    }

    private Loaded? Report(string path, string reason)
    {
        if (_reported.Add(path))
        {
            _log.Warning("The LUT {Path} {Reason}; the picture is left as it is", path, reason);
        }

        return null;
    }

    private static unsafe Loaded Upload(RenderDevice device, CubeLut lut)
    {
        int size = lut.Size;
        float[] texels = new float[size * size * size * 4];
        for (int index = 0; index < lut.Entries.Length; index++)
        {
            Vector3 entry = lut.Entries[index];
            texels[(index * 4) + 0] = entry.X;
            texels[(index * 4) + 1] = entry.Y;
            texels[(index * 4) + 2] = entry.Z;
            texels[(index * 4) + 3] = 1.0f;
        }

        var description = new Texture3DDescription
        {
            Width = (uint)size,
            Height = (uint)size,
            Depth = (uint)size,
            MipLevels = 1,
            Format = Format.R32G32B32A32_Float,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Immutable,
        };

        fixed (float* data = texels)
        {
            uint row = (uint)(size * 4 * sizeof(float));
            ID3D11Texture3D texture = device.Device.CreateTexture3D(description, [new SubresourceData((IntPtr)data, row, row * (uint)size)]);

            return new Loaded(texture, device.Device.CreateShaderResourceView(texture), size, lut.DomainMin, lut.DomainMax);
        }
    }

    private sealed record Loaded(ID3D11Texture3D Texture, ID3D11ShaderResourceView View, int Size, Vector3 DomainMin, Vector3 DomainMax) : IDisposable
    {
        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }
}
