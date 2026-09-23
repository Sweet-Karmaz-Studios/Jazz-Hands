using System.Globalization;
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

        foreach (PathFigure figure in ParsePath(data))
        {
            sink.BeginFigure(figure.Start, FigureBegin.Filled);

            foreach (PathSegment segment in figure.Segments)
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

    /// <summary>One piece of an outline: a straight line or a cubic bezier to <see cref="End"/>.</summary>
    internal readonly record struct PathSegment(Vector2 End, bool IsCurve, Vector2 Control1 = default, Vector2 Control2 = default);

    /// <summary>A closed outline.</summary>
    internal sealed record PathFigure(Vector2 Start, List<PathSegment> Segments);

    /// <summary>Reads SVG path data into closed figures. Quadratic curves become cubic ones.</summary>
    /// <exception cref="FormatException">The data is not a path this reads.</exception>
    internal static List<PathFigure> ParsePath(string data)
    {
        var figures = new List<PathFigure>();
        var tokens = new PathTokens(data ?? string.Empty);
        PathFigure? figure = null;
        Vector2 current = Vector2.Zero;
        char command = 'M';

        while (tokens.More)
        {
            if (tokens.TryCommand(out char next))
            {
                command = next;
            }
            else if (command is 'Z' or 'z')
            {
                throw new FormatException($"A number follows Z at position {tokens.Position} in the mask path.");
            }

            bool relative = char.IsLower(command);
            Vector2 origin = relative ? current : Vector2.Zero;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = origin + tokens.Point();
                    figure = new PathFigure(current, []);
                    figures.Add(figure);

                    // Pairs after a move are lines, as SVG says.
                    command = relative ? 'l' : 'L';
                    break;

                case 'L':
                    current = origin + tokens.Point();
                    Require(figure).Segments.Add(new PathSegment(current, false));
                    break;

                case 'H':
                    current = new Vector2((relative ? current.X : 0.0f) + tokens.Number(), current.Y);
                    Require(figure).Segments.Add(new PathSegment(current, false));
                    break;

                case 'V':
                    current = new Vector2(current.X, (relative ? current.Y : 0.0f) + tokens.Number());
                    Require(figure).Segments.Add(new PathSegment(current, false));
                    break;

                case 'C':
                    Vector2 c1 = origin + tokens.Point();
                    Vector2 c2 = origin + tokens.Point();
                    current = origin + tokens.Point();
                    Require(figure).Segments.Add(new PathSegment(current, true, c1, c2));
                    break;

                case 'Q':
                    Vector2 control = origin + tokens.Point();
                    Vector2 end = origin + tokens.Point();
                    Vector2 from = current;
                    Require(figure).Segments.Add(new PathSegment(
                        end,
                        true,
                        from + ((control - from) * (2.0f / 3.0f)),
                        end + ((control - end) * (2.0f / 3.0f))));
                    current = end;
                    break;

                case 'Z':
                    // Every figure is closed anyway; a new one starts at the next M.
                    current = Require(figure).Start;
                    break;

                default:
                    throw new FormatException($"'{command}' is not a path command this reads. Use M, L, H, V, C, Q and Z.");
            }
        }

        if (figures.Count == 0)
        {
            throw new FormatException("The mask path is empty. Give it at least M x y and two more points.");
        }

        return figures;

        static PathFigure Require(PathFigure? figure) =>
            figure ?? throw new FormatException("A mask path has to start with M.");
    }

    /// <summary>Reads commands and numbers out of path data.</summary>
    private sealed class PathTokens(string text)
    {
        private int _at;

        public int Position => _at;

        public bool More
        {
            get
            {
                Skip();
                return _at < text.Length;
            }
        }

        public bool TryCommand(out char command)
        {
            Skip();
            if (_at < text.Length && char.IsLetter(text[_at]) && text[_at] is not ('e' or 'E'))
            {
                command = text[_at++];
                return true;
            }

            command = default;
            return false;
        }

        public Vector2 Point() => new(Number(), Number());

        public float Number()
        {
            Skip();
            int start = _at;

            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] is '.' or '-' or '+' or 'e' or 'E'))
            {
                // A sign starts a new number unless it follows an exponent.
                if (_at > start && text[_at] is '-' or '+' && text[_at - 1] is not ('e' or 'E'))
                {
                    break;
                }

                _at++;
            }

            return float.TryParse(text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : throw new FormatException($"Expected a number at position {start} in the mask path.");
        }

        private void Skip()
        {
            while (_at < text.Length && (char.IsWhiteSpace(text[_at]) || text[_at] == ','))
            {
                _at++;
            }
        }
    }
}
