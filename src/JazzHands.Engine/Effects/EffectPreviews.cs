using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Frames;
using JazzHands.Render;
using JazzHands.Render.Compositing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Rational = JazzHands.Core.Time.Rational;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Small pictures of what each picture effect and generator does, for the effects browser.
/// </summary>
/// <remarks>
/// Each is a sample scene built from generators, so it needs no media: a gradient sky, a pale
/// disc and a red card, with the effect on an adjustment layer over all of it at its default
/// settings, one second in. Keys get a scene with something to key (a green screen, a dark to
/// light ramp) over a checkerboard; the drop shadow goes on the card, since a shadow of the whole
/// frame falls outside it; a generator is drawn by itself over dark grey. A few effects whose
/// defaults change nothing (a crop of nothing, a transform to where it already is) are shown
/// with settings that do something, and a few whose defaults are too subtle to see at this size
/// are turned up.
///
/// The scenes are rendered at a quarter or an eighth of their size, and pixel-sized settings
/// scale with it, so a preview looks like the effect on a full frame, smaller. Rendering is
/// serialised: one device context, one frame at a time. Built for a background thread; the
/// editor makes one on WARP, off the GPU the preview plays on.
/// </remarks>
public sealed class EffectPreviews : IDisposable
{
    /// <summary>The width of a preview, in pixels.</summary>
    public const int Width = 160;

    /// <summary>The height of a preview, in pixels.</summary>
    public const int Height = 90;

    /// <summary>Where in the scene the preview is taken.</summary>
    public static readonly Flicks At = Flicks.FromSeconds(1);

    private static readonly Flicks Length = Flicks.FromSeconds(3);

    /// <summary>A warm, crushed look as a 17 point cube, written once into the temp folder for the LUT's preview.</summary>
    private static readonly Lazy<string> DemoLut = new(WriteDemoLut);

    /// <summary>Settings for effects whose defaults leave the picture as it is, or too nearly so to see.</summary>
    private static readonly Dictionary<string, (string Name, string Value)[]> Showcase = new(StringComparer.Ordinal)
    {
        ["video.crop"] = [("left", "12"), ("right", "12"), ("top", "10"), ("bottom", "10"), ("feather", "12")],
        ["video.transform"] = [("scale", "0.7, 0.7"), ("rotation", "-8")],
        ["video.ken-burns"] = [("start-scale", "1.6"), ("end-scale", "1.6")],
        ["video.blur.gaussian"] = [("radius", "16")],
        ["video.sharpen"] = [("amount", "400"), ("radius", "6")],
        ["video.key.luma"] = [("threshold", "0.35"), ("softness", "0.1")],
        ["color.basic"] = [("temperature", "45"), ("contrast", "1.2"), ("saturation", "1.3")],
        ["color.wheels"] = [("lift", "0, 0.02, 0.06, 0"), ("gain", "0.12, 0.04, -0.06, 0")],
        ["color.curves"] = [("master", "0,0 0.25,0.12 0.75,0.9 1,1")],
        ["color.hsl"] = [("hue", "355"), ("hue-width", "25"), ("hue-shift", "150")],
        ["color.lut"] = [("file", DemoLut.Value)],
        ["color.white-balance"] = [("neutral", "#FFD8A8")],
    };

    private readonly Lock _gate = new();
    private readonly RenderDevice _device;
    private readonly bool _ownsDevice;
    private readonly FrameServer _frames;
    private readonly EffectRegistry _registry;
    private bool _disposed;

    /// <summary>Creates a renderer on a device, or on a WARP device of its own.</summary>
    public EffectPreviews(RenderDevice? device = null, EffectRegistry? registry = null)
    {
        _ownsDevice = device is null;
        _device = device ?? RenderDevice.Create(forceWarp: true);
        _frames = new FrameServer(_device);
        _registry = registry ?? EffectCatalog.Registry;
    }

    /// <summary>True for the types that have a preview: picture effects, generators and picture transitions.</summary>
    public static bool HasPreview(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Kind is EffectKind.Video or EffectKind.Generator or EffectKind.Transition;
    }

    /// <summary>
    /// Renders one type's preview as <see cref="Width"/> by <see cref="Height"/> BGRA pixels, top
    /// row first, opaque. Null for a type with no picture (a sound effect).
    /// </summary>
    public byte[]? Render(string typeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);

        if (_registry.Find(typeId) is not { } descriptor || !HasPreview(descriptor))
        {
            return null;
        }

        (Project project, Sequence sequence, int divisor) = Scene(descriptor);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            RenderTarget display = _frames.Compositor.Pool.Rent(Width, Height, Format.B8G8R8A8_UNorm);
            try
            {
                _frames.Render(project, sequence, At, RenderOptions.ForDivisor(divisor), display.View, Width, Height, OutputSettings.Preview);
                return ReadBack(display.Texture);
            }
            finally
            {
                _frames.Compositor.Pool.Return(display);
            }
        }
    }

    /// <summary>
    /// The sample scene a type is shown in, and the divisor that renders it at preview size.
    /// </summary>
    public static (Project Project, Sequence Sequence, int Divisor) Scene(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Kind == EffectKind.Transition)
        {
            return TransitionScene(descriptor);
        }

        // Generators are drawn at 720p, so their default sizes (a 400 pixel ellipse, a 96 point
        // digit) fit as they would in a real frame; effects at 360p, where their default sizes
        // show up at 160 pixels across.
        bool generator = descriptor.Kind == EffectKind.Generator;
        int divisor = generator ? 8 : 4;
        Project project = Project.CreateNew("preview", new ProjectSettings(Rational.Fps30, Width * divisor, Height * divisor));
        Sequence sequence = project.ActiveSequence!;

        List<Clip> layers = generator ? GeneratorLayers(descriptor)
            : descriptor.Category == "Keying" ? KeyingLayers(descriptor)
            : PictureLayers(descriptor);

        Track first = sequence.Tracks.First(track => track.Kind == TrackKind.Video);
        sequence = sequence.ReplaceTrack(first.AddClip(layers[0]));
        foreach (Clip layer in layers.Skip(1))
        {
            sequence = sequence.AddTrack(new Track(Id.New(), TrackKind.Video, $"V{sequence.NextTrackOrder()}", sequence.NextTrackOrder(), EquatableArray.Create(layer)));
        }

        if (!generator && descriptor.Category != "Keying" && !OnTheCard(descriptor))
        {
            var adjustment = new Clip(Id.New(), new TimeRange(Flicks.Zero, Length), Flicks.Zero, Name: descriptor.Name, Effects: EquatableArray.Create(Configured(descriptor)));
            sequence = sequence.AddTrack(new Track(Id.New(), TrackKind.Adjustment, "Adjustment", sequence.NextTrackOrder(), EquatableArray.Create(adjustment)));
        }

        return (project.ReplaceSequence(sequence), sequence, divisor);
    }

    /// <summary>
    /// A transition's scene: a blue to orange sky cutting to a teal to yellow one, with the
    /// transition on the cut at its defaults, a second long and caught 40 percent of the way
    /// through, where every one of them shows both pictures.
    /// </summary>
    private static (Project Project, Sequence Sequence, int Divisor) TransitionScene(EffectDescriptor descriptor)
    {
        const int divisor = 4;
        Project project = Project.CreateNew("preview", new ProjectSettings(Rational.Fps30, Width * divisor, Height * divisor));
        Sequence sequence = project.ActiveSequence!;
        Flicks cut = At + Flicks.FromMilliseconds(100);

        Clip outgoing = Generated("gen.gradient", ("start-colour", "#1D3557"), ("end-colour", "#F4A261"), ("start", "0, -180"), ("end", "0, 180")) with
        {
            Range = new TimeRange(Flicks.Zero, cut),
        };
        Clip incoming = Generated("gen.gradient", ("start-colour", "#2A9D8F"), ("end-colour", "#E9C46A"), ("start", "-320, 0"), ("end", "320, 0")) with
        {
            Range = TimeRange.FromBounds(cut, Length),
        };

        var transition = new Transition(PreviewEffectId, descriptor.TypeId, outgoing.Id, incoming.Id, Flicks.OneSecond, TransitionAlignment.Centered, EquatableArray<EffectParameter>.Empty);
        Track first = sequence.Tracks.First(track => track.Kind == TrackKind.Video);
        first = first.AddClip(outgoing).AddClip(incoming) with { Transitions = EquatableArray.Create(transition) };
        sequence = sequence.ReplaceTrack(first);

        return (project.ReplaceSequence(sequence), sequence, divisor);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frames.Dispose();
            if (_ownsDevice)
            {
                _device.Dispose();
            }
        }
    }

    private static bool OnTheCard(EffectDescriptor descriptor) => descriptor.TypeId == "video.drop-shadow";

    private static List<Clip> PictureLayers(EffectDescriptor descriptor)
    {
        Clip card = Generated("gen.shape.rectangle", ("position", "70, 30"), ("size", "220, 140"), ("rotation", "12"), ("corner-radius", "16"), ("fill", "#E63946"));
        if (OnTheCard(descriptor))
        {
            card = card with { Effects = card.Effects.Add(Configured(descriptor)) };
        }

        return
        [
            Generated("gen.gradient", ("start-colour", "#1D3557"), ("end-colour", "#F4A261"), ("start", "0, -180"), ("end", "0, 180")),
            Generated("gen.shape.ellipse", ("position", "-110, -30"), ("size", "170, 170"), ("fill", "#FFF1B8")),
            card,
        ];
    }

    private static List<Clip> KeyingLayers(EffectDescriptor descriptor)
    {
        Clip screen = descriptor.TypeId == "video.key.luma"
            ? Generated("gen.gradient", ("start-colour", "#000000"), ("end-colour", "#FFFFFF"), ("start", "-320, 0"), ("end", "320, 0"))
            : Generated("gen.solid", ("color", "#00FF00"));

        return
        [
            Generated("gen.checkerboard"),
            screen with { Effects = screen.Effects.Add(Configured(descriptor)) },
            Generated("gen.shape.ellipse", ("position", "0, 20"), ("size", "150, 150"), ("fill", "#E63946")),
        ];
    }

    private static List<Clip> GeneratorLayers(EffectDescriptor descriptor) =>
    [
        Generated("gen.solid", ("color", "#1E1E1E")),

        // The time is drawn large and centred: at its default size it is a speck at 160 pixels.
        descriptor.TypeId == "gen.timecode" ? Generated(descriptor.TypeId, ("size", "150"), ("corner", "centre")) : Generated(descriptor.TypeId),
    ];

    /// <summary>A generator clip running the whole scene, with some of its settings given.</summary>
    private static Clip Generated(string typeId, params (string Name, string Value)[] settings)
    {
        Effect own = Effect.Create(typeId);
        if (EffectCatalog.Registry.Find(typeId) is { } descriptor)
        {
            own = Set(own, descriptor, settings);
        }

        return new Clip(Id.New(), new TimeRange(Flicks.Zero, Length), Flicks.Zero, GeneratorId: typeId, Name: typeId, Effects: EquatableArray.Create(own));
    }

    /// <summary>An effect at its defaults, or at its showcase settings when it has some.</summary>
    private static Effect Configured(EffectDescriptor descriptor) =>
        Set(Effect.Create(descriptor.TypeId) with { Id = PreviewEffectId }, descriptor, Showcase.TryGetValue(descriptor.TypeId, out (string, string)[]? settings) ? settings : []);

    /// <summary>
    /// One id for every previewed effect and transition, so a seeded one (flicker, grain, glitch) is seeded the
    /// same each time and its preview is the same picture, rather than one that by chance barely
    /// shows it.
    /// </summary>
    private const string PreviewEffectId = "01M3B0000000000000000PREV0";

    private static Effect Set(Effect effect, EffectDescriptor descriptor, (string Name, string Value)[] settings)
    {
        foreach ((string name, string value) in settings)
        {
            if (descriptor.Param(name) is { } parameter)
            {
                effect = effect.WithParameter(name, AnimatedValue.Constant(ParamValues.Parse(parameter, value)));
            }
        }

        return effect;
    }

    private static string WriteDemoLut()
    {
        string path = Path.Combine(Path.GetTempPath(), "JazzHands", "preview-look.cube");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var text = new System.Text.StringBuilder("LUT_3D_SIZE 17").AppendLine();
        for (int b = 0; b < 17; b++)
        {
            for (int g = 0; g < 17; g++)
            {
                for (int r = 0; r < 17; r++)
                {
                    // Warm highlights, teal shadows, the blacks lifted a little.
                    float red = 0.04f + (0.96f * MathF.Pow(r / 16.0f, 0.9f));
                    float green = 0.03f + (0.95f * (g / 16.0f));
                    float blue = 0.08f + (0.80f * MathF.Pow(b / 16.0f, 1.1f));
                    text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{red:0.####} {green:0.####} {blue:0.####}").AppendLine();
                }
            }
        }

        File.WriteAllText(path, text.ToString());
        return path;
    }

    private unsafe byte[] ReadBack(ID3D11Texture2D texture)
    {
        using ID3D11Texture2D staging = _device.CreateStagingTexture(texture);
        _device.ImmediateContext.CopyResource(staging, texture);
        MappedSubresource mapped = _device.ImmediateContext.Map(staging, 0, MapMode.Read);

        try
        {
            byte[] pixels = new byte[Width * Height * 4];
            for (int row = 0; row < Height; row++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (row * (int)mapped.RowPitch), Width * 4).CopyTo(pixels.AsSpan(row * Width * 4));
            }

            return pixels;
        }
        finally
        {
            _device.ImmediateContext.Unmap(staging, 0);
        }
    }
}
