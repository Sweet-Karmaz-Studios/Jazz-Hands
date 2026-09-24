using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JazzHands.Render.Effects.Generators;

/// <summary>
/// A rectangle or an ellipse with a fill, an outline and rounded corners, drawn by Direct2D. Every
/// part is animatable: move it, grow it, turn it, round it, change its colours.
/// </summary>
public abstract class BoxShapeGenerator : VideoGenerator
{
    /// <summary>True for an ellipse, false for a rectangle.</summary>
    protected abstract bool IsEllipse { get; }

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        float scale = context.QualityScale;
        Vector2 size = Vector2.Abs(parameters.Float2("size")) * scale;
        Vector2 centre = (new Vector2(output.Width, output.Height) / 2.0f) + (parameters.Float2("position") * scale);
        float stroke = parameters.Float("stroke-width") * scale;
        float corner = IsEllipse ? 0.0f : Math.Min(parameters.Float("corner-radius") * scale, Math.Min(size.X, size.Y) / 2.0f);
        Vector4 fillColour = parameters.Color("fill");
        Vector4 strokeColour = parameters.Color("stroke");

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            target.Transform = Matrix3x2.CreateRotation(parameters.Float("rotation") * MathF.PI / 180.0f) * Matrix3x2.CreateTranslation(centre);
            var box = new Rect(-size.X / 2.0f, -size.Y / 2.0f, size.X, size.Y);

            if (fillColour.W > 0.0f)
            {
                using ID2D1SolidColorBrush fill = drawing.Brush(fillColour);
                Fill(target, box, corner, fill);
            }

            if (stroke > 0.0f && strokeColour.W > 0.0f)
            {
                using ID2D1SolidColorBrush outline = drawing.Brush(strokeColour);
                Outline(target, box, corner, outline, stroke);
            }
        });
    }

    private void Fill(ID2D1DeviceContext target, Rect box, float corner, ID2D1SolidColorBrush brush)
    {
        if (IsEllipse)
        {
            target.FillEllipse(new Ellipse(new Vector2(0, 0), box.Width / 2.0f, box.Height / 2.0f), brush);
        }
        else if (corner > 0.0f)
        {
            target.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(box.X, box.Y, box.Width, box.Height), corner, corner), brush);
        }
        else
        {
            target.FillRectangle(box, brush);
        }
    }

    private void Outline(ID2D1DeviceContext target, Rect box, float corner, ID2D1SolidColorBrush brush, float width)
    {
        if (IsEllipse)
        {
            target.DrawEllipse(new Ellipse(new Vector2(0, 0), box.Width / 2.0f, box.Height / 2.0f), brush, width);
        }
        else if (corner > 0.0f)
        {
            target.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(box.X, box.Y, box.Width, box.Height), corner, corner), brush, width);
        }
        else
        {
            target.DrawRectangle(box, brush, width);
        }
    }
}

/// <summary>A rectangle.</summary>
[Generator("gen.shape.rectangle", Name = "Rectangle", Category = "Shapes", Description = "A filled and outlined rectangle with optional rounded corners, for boxes, bars and callouts.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The centre, in sequence pixels from the frame centre.")]
[Param("size", ParamType.Float2, Default = "600, 300", Unit = "px", Description = "Width and height, in sequence pixels.")]
[Param("rotation", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Degrees clockwise.")]
[Param("corner-radius", ParamType.Float, Default = "0", Min = 0, Max = 5000, SliderMax = 200, Unit = "px", Description = "How round the corners are, in sequence pixels.")]
[Param("fill", ParamType.Color, Default = "#FFFFFF", Description = "The inside; a transparent colour for an outline only.")]
[Param("stroke", ParamType.Color, Default = "#000000", Description = "The outline's colour.")]
[Param("stroke-width", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 50, Unit = "px", Description = "The outline's width; 0 for none.")]
public sealed class RectangleGenerator : BoxShapeGenerator
{
    /// <inheritdoc />
    protected override bool IsEllipse => false;
}

/// <summary>An ellipse.</summary>
[Generator("gen.shape.ellipse", Name = "Ellipse", Category = "Shapes", Description = "A filled and outlined ellipse or circle, for spotlights, rings and highlights.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The centre, in sequence pixels from the frame centre.")]
[Param("size", ParamType.Float2, Default = "400, 400", Unit = "px", Description = "Width and height, in sequence pixels; equal for a circle.")]
[Param("rotation", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Degrees clockwise.")]
[Param("fill", ParamType.Color, Default = "#FFFFFF", Description = "The inside; a transparent colour for a ring only.")]
[Param("stroke", ParamType.Color, Default = "#000000", Description = "The outline's colour.")]
[Param("stroke-width", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 50, Unit = "px", Description = "The outline's width; 0 for none.")]
public sealed class EllipseGenerator : BoxShapeGenerator
{
    /// <inheritdoc />
    protected override bool IsEllipse => true;
}

/// <summary>A straight line, or an arrow with heads at either or both ends.</summary>
public abstract class LineShapeGenerator : VideoGenerator
{
    /// <summary>True when heads are drawn.</summary>
    protected abstract bool HasHeads { get; }

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        float scale = context.QualityScale;
        var frameCentre = new Vector2(output.Width, output.Height) / 2.0f;
        Vector2 start = frameCentre + (parameters.Float2("start") * scale);
        Vector2 end = frameCentre + (parameters.Float2("end") * scale);
        float width = parameters.Float("width") * scale;
        Vector4 colour = parameters.Color("colour");

        string heads = HasHeads ? parameters.Enum("heads") : "none";
        float head = HasHeads ? parameters.Float("head-size") * scale : 0.0f;
        bool atEnd = heads is "end" or "both";
        bool atStart = heads is "start" or "both";

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            if (colour.W <= 0.0f || width <= 0.0f)
            {
                return;
            }

            float length = Vector2.Distance(start, end);
            Vector2 direction = length > 1e-3f ? (end - start) / length : Vector2.UnitX;

            // The shaft stops where a head begins, so its round cap does not poke through the point.
            Vector2 from = atStart ? start + (direction * Math.Min(head, length / 2.0f)) : start;
            Vector2 to = atEnd ? end - (direction * Math.Min(head, length / 2.0f)) : end;

            using ID2D1SolidColorBrush brush = drawing.Brush(colour);
            using ID2D1StrokeStyle round = drawing.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            target.DrawLine(from, to, brush, width, round);

            if (atEnd)
            {
                Head(drawing, target, end, direction, head, brush);
            }

            if (atStart)
            {
                Head(drawing, target, start, -direction, head, brush);
            }
        });
    }

    /// <summary>A filled triangle pointing along a direction with its tip at a point.</summary>
    private static void Head(Drawing2D drawing, ID2D1DeviceContext target, Vector2 tip, Vector2 direction, float size, ID2D1SolidColorBrush brush)
    {
        if (size <= 0.0f)
        {
            return;
        }

        var side = new Vector2(-direction.Y, direction.X);
        Vector2 back = tip - (direction * size);

        using ID2D1PathGeometry geometry = drawing.Factory.CreatePathGeometry();
        using (ID2D1GeometrySink sink = geometry.Open())
        {
            sink.BeginFigure(tip, FigureBegin.Filled);
            sink.AddLine(back + (side * size * 0.6f));
            sink.AddLine(back - (side * size * 0.6f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        target.FillGeometry(geometry, brush);
    }
}

/// <summary>A straight line.</summary>
[Generator("gen.shape.line", Name = "Line", Category = "Shapes", Description = "A straight line with round ends, for underlines, dividers and pointers.")]
[Param("start", ParamType.Point, Default = "-400, 0", Unit = "px", Description = "One end, in sequence pixels from the frame centre.")]
[Param("end", ParamType.Point, Default = "400, 0", Unit = "px", Description = "The other end.")]
[Param("width", ParamType.Float, Default = "8", Min = 0, Max = 1000, SliderMax = 60, Unit = "px", Description = "How thick, in sequence pixels.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "Its colour.")]
public sealed class LineGenerator : LineShapeGenerator
{
    /// <inheritdoc />
    protected override bool HasHeads => false;
}

/// <summary>An arrow.</summary>
[Generator("gen.shape.arrow", Name = "Arrow", Category = "Shapes", Description = "An arrow with a head at one or both ends, for pointing things out; animate the end to draw it on.")]
[Param("start", ParamType.Point, Default = "-400, 0", Unit = "px", Description = "The tail, in sequence pixels from the frame centre.")]
[Param("end", ParamType.Point, Default = "400, 0", Unit = "px", Description = "The point.")]
[Param("width", ParamType.Float, Default = "10", Min = 0, Max = 1000, SliderMax = 60, Unit = "px", Description = "How thick the shaft is, in sequence pixels.")]
[Param("colour", ParamType.Color, Default = "#FFD400", Description = "Its colour.")]
[Param("heads", ParamType.Enum, Default = "end", Choices = "end, start, both", Animatable = false, Description = "Which ends have a head.")]
[Param("head-size", ParamType.Float, Default = "40", Min = 0, Max = 1000, SliderMax = 150, Unit = "px", Description = "How long a head is, in sequence pixels.")]
public sealed class ArrowGenerator : LineShapeGenerator
{
    /// <inheritdoc />
    protected override bool HasHeads => true;
}
