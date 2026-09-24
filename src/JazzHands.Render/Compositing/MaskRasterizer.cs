using System.Numerics;
using JazzHands.Core.Model;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Compositing;

/// <summary>
/// Rasterizes mask shapes with Direct2D into the alpha of a B8G8R8A8 target, through a layer's
/// matrix, so a mask stays on its part of the picture however the layer is placed.
/// </summary>
/// <remarks>
/// An expansion grows the shape by stroking its outline with twice the distance and shrinks it
/// by stroking it again with transparency, round joined either way. The outline is transformed
/// to output pixels first, so the stroke is as wide on screen whatever the layer's scale.
/// </remarks>
internal sealed class MaskRasterizer : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _white;
    private readonly ID2D1SolidColorBrush _clear;
    private readonly ID2D1StrokeStyle _round;

    public MaskRasterizer(RenderDevice device)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        using IDXGIDevice dxgi = device.Device.QueryInterface<IDXGIDevice>();
        _device = _factory.CreateDevice(dxgi);
        _context = _device.CreateDeviceContext(DeviceContextOptions.None);
        _white = _context.CreateSolidColorBrush(new Color4(1.0f, 1.0f, 1.0f, 1.0f));
        _clear = _context.CreateSolidColorBrush(new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        _round = _factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
    }

    /// <summary>Draws one shape's coverage into a cleared target.</summary>
    /// <param name="target">A B8G8R8A8 target the size of the frame.</param>
    /// <param name="shape">The shape, in the layer's source pixels.</param>
    /// <param name="transform">Source pixels to target pixels.</param>
    /// <param name="scale">Target pixels per sequence pixel, which the expansion is multiplied by.</param>
    public void Rasterize(RenderTarget target, MatteShape shape, Matrix3x2 transform, float scale = 1.0f)
    {
        using IDXGISurface surface = target.Texture.QueryInterface<IDXGISurface>();
        using ID2D1Bitmap1 bitmap = _context.CreateBitmapFromDxgiSurface(
            surface,
            new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96.0f,
                96.0f,
                BitmapOptions.Target | BitmapOptions.CannotDraw));

        using ID2D1Geometry outline = Geometry(shape);
        using ID2D1TransformedGeometry placed = _factory.CreateTransformedGeometry(outline, transform);

        _context.Target = bitmap;
        _context.BeginDraw();
        _context.Clear(new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        _context.Transform = Matrix3x2.Identity;
        _context.FillGeometry(placed, _white);

        float stroke = Math.Abs(shape.Expansion) * scale * 2.0f;
        if (stroke > 0.01f)
        {
            if (shape.Expansion > 0)
            {
                _context.DrawGeometry(placed, _white, stroke, _round);
            }
            else
            {
                // Copy rather than blend, so the transparent stroke clears what the fill drew.
                _context.PrimitiveBlend = PrimitiveBlend.Copy;
                _context.DrawGeometry(placed, _clear, stroke, _round);
                _context.PrimitiveBlend = PrimitiveBlend.SourceOver;
            }
        }

        _context.EndDraw();
        _context.Target = null;
    }

    public void Dispose()
    {
        _round.Dispose();
        _clear.Dispose();
        _white.Dispose();
        _context.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }

    /// <summary>A shape's outline in its own pixels.</summary>
    internal ID2D1Geometry Geometry(MatteShape shape)
    {
        switch (shape.Shape)
        {
            case MaskShape.Rectangle:
                return _factory.CreateRectangleGeometry(new Rect(shape.Bounds.X, shape.Bounds.Y, shape.Bounds.Z, shape.Bounds.W));

            case MaskShape.Ellipse:
                var centre = new Vector2(shape.Bounds.X + (shape.Bounds.Z / 2.0f), shape.Bounds.Y + (shape.Bounds.W / 2.0f));
                return _factory.CreateEllipseGeometry(new Ellipse(centre, shape.Bounds.Z / 2.0f, shape.Bounds.W / 2.0f));

            default:
                return Path(shape.PathData);
        }
    }

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
