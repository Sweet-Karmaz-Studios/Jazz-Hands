using System.Numerics;
using JazzHands.Render.Compositing;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace JazzHands.Render.Titles;

/// <summary>
/// Draws a laid out title's glyph runs: their outlines on one pass, their fills on the next.
/// </summary>
/// <remarks>
/// DirectWrite hands over a run per stretch of one font and one drawing effect, which is a brush
/// with the stretch's colour and its opacity in a reveal. The outline is the run's glyph outline
/// as geometry, stroked twice the outline width with round joins; the fill is the run itself,
/// which Direct2D draws with the font's hinting and antialiasing rather than as geometry.
/// </remarks>
internal sealed class GlyphPainter(Drawing2D drawing, ID2D1StrokeStyle round) : TextRendererBase
{
    private ID2D1DeviceContext? _target;
    private bool _outline;
    private Vector4 _colour;
    private float _width;

    /// <summary>Aims the painter at a target for one pass.</summary>
    public void Begin(ID2D1DeviceContext target, bool outline, Vector4 colour, float width)
    {
        _target = target;
        _outline = outline;
        _colour = colour;
        _width = width;
    }

    /// <inheritdoc />
    public override void DrawGlyphRun(IntPtr clientDrawingContext, float baselineOriginX, float baselineOriginY, MeasuringMode measuringMode, GlyphRun glyphRun, GlyphRunDescription glyphRunDescription, IUnknown clientDrawingEffect)
    {
        ID2D1DeviceContext target = _target ?? throw new InvalidOperationException("The painter has no target.");
        if (glyphRun.FontFace is null || glyphRun.Indices is null || glyphRun.Indices.Length == 0)
        {
            return;
        }

        using ID2D1SolidColorBrush? brush = (clientDrawingEffect as ComObject)?.QueryInterfaceOrNull<ID2D1SolidColorBrush>();
        float opacity = brush?.Opacity ?? 1.0f;
        if (opacity <= 0.0f)
        {
            return;
        }

        var origin = new Vector2(baselineOriginX, baselineOriginY);
        if (_outline)
        {
            using ID2D1PathGeometry geometry = drawing.Factory.CreatePathGeometry();
            using (ID2D1GeometrySink sink = geometry.Open())
            {
                glyphRun.FontFace.GetGlyphRunOutline(
                    glyphRun.FontEmSize,
                    glyphRun.Indices,
                    glyphRun.Advances,
                    glyphRun.Offsets,
                    glyphRun.IsSideways,
                    glyphRun.BidiLevel % 2 == 1,
                    sink);
                sink.Close();
            }

            using ID2D1SolidColorBrush stroke = drawing.Brush(_colour);
            stroke.Opacity = opacity;
            Matrix3x2 before = target.Transform;
            target.Transform = Matrix3x2.CreateTranslation(origin) * before;
            target.DrawGeometry(geometry, stroke, _width * 2.0f, round);
            target.Transform = before;
            return;
        }

        if (brush is not null)
        {
            target.DrawGlyphRun(origin, glyphRun, glyphRunDescription, brush, measuringMode);
        }
    }

    /// <inheritdoc />
    public override void DrawUnderline(IntPtr clientDrawingContext, float baselineOriginX, float baselineOriginY, ref Underline underline, IUnknown clientDrawingEffect)
    {
        ID2D1DeviceContext target = _target ?? throw new InvalidOperationException("The painter has no target.");
        using ID2D1SolidColorBrush? brush = (clientDrawingEffect as ComObject)?.QueryInterfaceOrNull<ID2D1SolidColorBrush>();
        if (brush is null || brush.Opacity <= 0.0f)
        {
            return;
        }

        var rect = new Rect(baselineOriginX, baselineOriginY + underline.Offset, underline.Width, underline.Thickness);
        if (_outline)
        {
            using ID2D1SolidColorBrush stroke = drawing.Brush(_colour);
            stroke.Opacity = brush.Opacity;
            target.DrawRectangle(rect, stroke, _width * 2.0f, round);
            return;
        }

        target.FillRectangle(rect, brush);
    }

    /// <inheritdoc />
    public override RawBool IsPixelSnappingDisabled(IntPtr clientDrawingContext) => true;

    /// <inheritdoc />
    public override Matrix3x2 GetCurrentTransform(IntPtr clientDrawingContext) => _target?.Transform ?? Matrix3x2.Identity;

    /// <inheritdoc />
    public override float GetPixelsPerDip(IntPtr clientDrawingContext) => 1.0f;
}
