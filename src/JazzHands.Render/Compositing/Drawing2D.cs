using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Compositing;

/// <summary>
/// Direct2D and DirectWrite drawing straight into the compositor's half float targets, for
/// generators that are shapes or text.
/// </summary>
/// <remarks>
/// Direct2D draws into an <c>R16G16B16A16_FLOAT</c> surface with premultiplied alpha and no
/// transfer function, so colours given to it are linear light and what it writes is already in the
/// working space. Antialiasing is therefore computed in linear light, which is how everything else
/// here blends. Made on first use by <see cref="EffectContext"/>, one per device.
/// </remarks>
public sealed class Drawing2D : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly Dictionary<(string Family, float Size, FontWeight Weight), IDWriteTextFormat> _formats = [];

    internal Drawing2D(RenderDevice device)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using IDXGIDevice dxgi = device.Device.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgi);
        Context = _device.CreateDeviceContext(DeviceContextOptions.None);
        Context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        // Effects (a title's shadow and blur) keep half floats between steps, as the targets do:
        // eight bits of linear light would band a soft shadow's falloff.
        RenderingControls controls = Context.RenderingControls;
        controls.BufferPrecision = BufferPrecision.PerChannel16Float;
        Context.RenderingControls = controls;
        Text = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    }

    /// <summary>The Direct2D factory, for geometry.</summary>
    public ID2D1Factory1 Factory => _factory;

    /// <summary>The device context, aimed at the target while <see cref="Draw"/> runs.</summary>
    public ID2D1DeviceContext Context { get; }

    /// <summary>The DirectWrite factory.</summary>
    public IDWriteFactory Text { get; }

    /// <summary>
    /// Clears a target to transparent and draws into it. Coordinates are target texels with the
    /// origin top left.
    /// </summary>
    public void Draw(RenderTarget target, Action<ID2D1DeviceContext> draw)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(draw);

        using IDXGISurface surface = target.Texture.QueryInterface<IDXGISurface>();
        using ID2D1Bitmap1 bitmap = Context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new PixelFormat(target.Format, Vortice.DCommon.AlphaMode.Premultiplied),
                96.0f,
                96.0f,
                BitmapOptions.Target | BitmapOptions.CannotDraw));

        Context.Target = bitmap;
        Context.BeginDraw();
        Context.Clear(new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        Context.Transform = Matrix3x2.Identity;
        try
        {
            draw(Context);
        }
        finally
        {
            Context.Transform = Matrix3x2.Identity;
            Context.EndDraw();
            Context.Target = null;
        }
    }

    /// <summary>A target as a bitmap Direct2D can draw from, which the caller disposes.</summary>
    public ID2D1Bitmap1 Source(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        using IDXGISurface surface = target.Texture.QueryInterface<IDXGISurface>();
        return Context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(new PixelFormat(target.Format, Vortice.DCommon.AlphaMode.Premultiplied), 96.0f, 96.0f, BitmapOptions.None));
    }

    /// <summary>A brush of a linear premultiplied colour, which the caller disposes.</summary>
    public ID2D1SolidColorBrush Brush(Vector4 premultiplied)
    {
        // Direct2D takes straight colour and premultiplies it itself.
        Vector4 straight = premultiplied.W > 0 ? new Vector4(premultiplied.X / premultiplied.W, premultiplied.Y / premultiplied.W, premultiplied.Z / premultiplied.W, premultiplied.W) : Vector4.Zero;
        return Context.CreateSolidColorBrush(new Color4(straight.X, straight.Y, straight.Z, straight.W));
    }

    /// <summary>A text format, made once per family, size and weight.</summary>
    public IDWriteTextFormat Format(string family, float size, FontWeight weight = FontWeight.Normal)
    {
        float rounded = MathF.Round(size * 4.0f) / 4.0f;
        if (!_formats.TryGetValue((family, rounded, weight), out IDWriteTextFormat? format))
        {
            format = Text.CreateTextFormat(family, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, Math.Max(1.0f, rounded));
            _formats[(family, rounded, weight)] = format;
        }

        return format;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDWriteTextFormat format in _formats.Values)
        {
            format.Dispose();
        }

        Text.Dispose();
        Context.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }
}
