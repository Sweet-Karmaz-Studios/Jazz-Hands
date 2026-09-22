using System.Numerics;
using JazzHands.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3D11 = Vortice.Direct3D11;

namespace JazzHands.App.Spikes;

/// <summary>
/// Draws the spike's test pattern: a gradient that moves every frame so a stale frame is obvious,
/// plus the frame number in large type so frame continuity can be read off a screen capture.
/// Direct2D, because it is the same path the title renderer will use in Phase 19.
/// </summary>
internal sealed class SpikeRenderer : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _context;
    private readonly IDWriteFactory _writeFactory;
    private readonly IDWriteTextFormat _counterFormat;
    private readonly IDWriteTextFormat _labelFormat;

    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _offscreen;
    private D3D11.ID3D11Texture2D? _offscreenTexture;
    private ID2D1LinearGradientBrush? _gradient;
    private ID2D1SolidColorBrush? _text;
    private int _width;
    private int _height;
    /// <summary>
    /// Rebind the Direct2D target before every frame. Spike S1 found that leaving the target
    /// bound across frames costs a recurring stall when WPF reads the same shared surface.
    /// </summary>
    public bool RebindTargetEachFrame { get; set; } = true;

    private int _targetWidth;
    private int _targetHeight;

    /// <summary>Creates the Direct2D stack on the same device the compositor uses.</summary>
    public SpikeRenderer(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using IDXGIDevice dxgiDevice = device.Device.QueryInterface<IDXGIDevice>();
        _d2dDevice = _factory.CreateDevice(dxgiDevice);
        _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _counterFormat = _writeFactory.CreateTextFormat("Consolas", 160.0f);
        _labelFormat = _writeFactory.CreateTextFormat("Consolas", 48.0f);
    }

    /// <summary>Points the renderer at a new back buffer. Call after the surface resizes.</summary>
    public void SetTarget(D3D11.ID3D11Texture2D texture, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(texture);

        ReleaseTarget();

        using IDXGISurface surface = texture.QueryInterface<IDXGISurface>();
        var properties = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);

        _target = _context.CreateBitmapFromDxgiSurface(surface, properties);
        _context.Target = _target;

        _gradient = _context.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = Vector2.Zero, EndPoint = new Vector2(width, height) },
            _context.CreateGradientStopCollection(
            [
                new GradientStop { Position = 0.0f, Color = new Color4(0.05f, 0.05f, 0.08f, 1.0f) },
                new GradientStop { Position = 0.5f, Color = new Color4(0.85f, 0.25f, 0.10f, 1.0f) },
                new GradientStop { Position = 1.0f, Color = new Color4(0.05f, 0.35f, 0.65f, 1.0f) },
            ]));

        _text = _context.CreateSolidColorBrush(new Color4(1.0f, 1.0f, 1.0f, 1.0f));
        _width = width;
        _height = height;
        _targetWidth = width;
        _targetHeight = height;
    }

    /// <summary>
    /// Renders the pattern at <paramref name="width"/> by <paramref name="height"/> into an
    /// offscreen target and scales it into the present target, which is what the compositor does
    /// when the sequence is larger than the preview panel. Used so the swap chain path is
    /// measured against the same 4K workload as the D3DImage path.
    /// </summary>
    public void SetOffscreenSize(RenderDevice device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);

        _offscreen?.Dispose();
        _offscreenTexture?.Dispose();

        _offscreenTexture = device.CreateRenderTarget(width, height, Format.B8G8R8A8_UNorm);
        using IDXGISurface surface = _offscreenTexture.QueryInterface<IDXGISurface>();
        _offscreen = _context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96.0f,
                96.0f,
                BitmapOptions.Target));

        _width = width;
        _height = height;
    }

    /// <summary>
    /// Draws frame <paramref name="frame"/>. Every frame moves the gradient and changes the
    /// number, so a dropped or repeated frame shows up in a capture rather than hiding.
    /// </summary>
    public void Render(long frame)
    {
        if (_target is null || _gradient is null || _text is null)
        {
            return;
        }

        if (RebindTargetEachFrame || _offscreen is not null)
        {
            _context.Target = _offscreen ?? _target;
        }

        float phase = frame % 120 / 120.0f;
        float sweep = _width * 1.5f;

        _gradient.StartPoint = new Vector2((phase * sweep) - (sweep * 0.5f), 0.0f);
        _gradient.EndPoint = new Vector2((phase * sweep) - (sweep * 0.5f) + (_width * 0.75f), _height);

        _context.BeginDraw();
        _context.FillRectangle(new Rect(0, 0, _width, _height), _gradient);

        // A moving bar gives a hard edge; tearing shows as a horizontal break in it.
        float barX = (frame * 37 % _width) - 40;
        _context.FillRectangle(new Rect(barX, 0, 80, _height), _text);

        _context.DrawText(
            frame.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            _counterFormat,
            new Rect(60, 60, _width, 260),
            _text);

        _context.DrawText(
            $"{_width}x{_height} D3DImage spike",
            _labelFormat,
            new Rect(60, 260, _width, 340),
            _text);

        _context.EndDraw();

        if (_offscreen is not null)
        {
            // Scale the sequence-sized frame into the present target, the way the output pass
            // will when the preview panel is smaller than the sequence.
            _context.Target = _target;
            _context.BeginDraw();
            _context.DrawBitmap(
                _offscreen,
                new Rect(0, 0, _targetWidth, _targetHeight),
                1.0f,
                InterpolationMode.Linear,
                null,
                null);
            _context.EndDraw();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ReleaseTarget();
        _offscreen?.Dispose();
        _offscreenTexture?.Dispose();
        _labelFormat.Dispose();
        _counterFormat.Dispose();
        _writeFactory.Dispose();
        _context.Dispose();
        _d2dDevice.Dispose();
        _factory.Dispose();
    }

    private void ReleaseTarget()
    {
        _context.Target = null;
        _text?.Dispose();
        _text = null;
        _gradient?.Dispose();
        _gradient = null;
        _target?.Dispose();
        _target = null;
    }
}
