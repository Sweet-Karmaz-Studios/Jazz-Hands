
using System.Numerics;
using JazzHands.Core.Model;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Compositing;

/// <summary>
/// Draws a mask's shape, through its layer's transform, into the alpha of a target.
/// </summary>
/// <remarks>
/// Direct2D on the compositor's own device, so the coverage texture never leaves the GPU and is
/// antialiased the way Direct2D antialiases everything. Shapes are given in the clip's source
/// pixels and drawn with the layer's matrix, so a mask sits on the same part of the picture
/// however the clip is moved, scaled or rotated.
/// </remarks>
internal sealed class MaskRasterizer : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _white;

    public MaskRasterizer(RenderDevice device)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        using IDXGIDevice dxgi = device.Device.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgi);
        _context = _device.CreateDeviceContext(DeviceContextOptions.None);
        _white = _context.CreateSolidColorBrush(new Color4(1.0f, 1.0f, 1.0f, 1.0f));
    }

    /// <summary>Clears a B8G8R8A8 target and fills the shape into it.</summary>
    public void Rasterize(RenderTarget target, MatteShape shape, Matrix3x2 transform)
    {
        using IDXGISurface surface = target.Texture.QueryInterface<IDXGISurface>();
        using ID2D1Bitmap1 bitmap = _context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96.0f,
                96.0f,
                BitmapOptions.Target | BitmapOptions.CannotDraw));

        _context.Target = bitmap;
        _context.BeginDraw();
        _context.Clear(new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        _context.Transform = transform;

        switch (shape.Shape)
        {
            case MaskShape.Rectangle:
                _context.FillRectangle(new Rect(shape.Bounds.X, shape.Bounds.Y, shape.Bounds.Z, shape.Bounds.W), _white);
                break;

            case MaskShape.Ellipse:
                var centre = new Vector2(shape.Bounds.X + (shape.Bounds.Z / 2.0f), shape.Bounds.Y + (shape.Bounds.W / 2.0f));
                _context.FillEllipse(new Ellipse(centre, shape.Bounds.Z / 2.0f, shape.Bounds.W / 2.0f), _white);
                break;

            default:
                using (ID2D1PathGeometry path = Path(shape.PathData))
                {
                    _context.FillGeometry(path, _white);
                }

                break;
        }

        _context.Transform = Matrix3x2.Identity;
        _context.EndDraw();
        _context.Target = null;
    }

    public void Dispose()
    {
        _white.Dispose();
        _context.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// SVG path data as a Direct2D geometry: M, L, H, V, C, Q and Z, absolute and relative.
    /// </summary>
    internal ID2D1PathGeometry Path(string data)
    {
        ID2D1PathGeometry geometry = _factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();
        sink.SetFillMode(FillMode.Winding);

        foreach (MaskPathFigure figure in MaskPath.Parse(data))
        {
            sink.BeginFigure(figure.Start, FigureBegin.Filled);

            foreach (MaskPathSegment segment in figure.Segments)
            {
                if (segment.IsCurve)
                {
                    sink.AddBezier(new BezierSegment { Point1 = segment.Control1, Point2 = segment.Control2, Point3 = segment.End });
                }
                else
                {
                    sink.AddLine(segment.End);
                }
            }

            sink.EndFigure(FigureEnd.Closed);
        }

        sink.Close();
        return geometry;
    }
}
