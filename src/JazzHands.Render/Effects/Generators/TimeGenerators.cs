using System.Globalization;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Time;
using JazzHands.Render.Compositing;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace JazzHands.Render.Effects.Generators;

/// <summary>A film leader countdown: a number each second over a sweeping hand, driven by clip time.</summary>
[Generator("gen.countdown", Name = "Countdown", Category = "Generators", Description = "A film leader countdown from a number of seconds, with a sweeping hand; it ends transparent when it reaches zero.")]
[Param("from", ParamType.Int, Default = "5", Min = 1, Max = 99, Animatable = false, Description = "The number it starts at, one a second.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The number and the rings.")]
[Param("background", ParamType.Color, Default = "#202020", Description = "Behind everything.")]
[Param("sweep", ParamType.Color, Default = "#555555", Description = "The wedge that sweeps round each second.")]
public sealed class CountdownGenerator : VideoGenerator
{
    /// <summary>The number shown at a time, or 0 once the countdown has finished.</summary>
    public static int NumberAt(double seconds, int from) => seconds >= from ? 0 : Math.Max(1, (int)Math.Ceiling(from - seconds));

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        double seconds = Math.Max(0.0, context.Time.ToSeconds());
        int number = NumberAt(seconds, parameters.Int("from"));
        float sweep = (float)(seconds - Math.Floor(seconds));
        var centre = new Vector2(output.Width, output.Height) / 2.0f;
        float radius = Math.Min(output.Width, output.Height) * 0.4f;

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            if (number == 0)
            {
                return;
            }

            using ID2D1SolidColorBrush background = drawing.Brush(parameters.Color("background"));
            using ID2D1SolidColorBrush wedge = drawing.Brush(parameters.Color("sweep"));
            using ID2D1SolidColorBrush ink = drawing.Brush(parameters.Color("colour"));
            target.FillRectangle(new Rect(0, 0, output.Width, output.Height), background);

            // The hand sweeps clockwise from twelve o'clock, a full turn a second.
            if (sweep > 0.001f)
            {
                using ID2D1PathGeometry pie = Pie(drawing, centre, radius * 1.6f, sweep);
                target.FillGeometry(pie, wedge);
            }

            float line = Math.Max(1.0f, radius * 0.02f);
            target.DrawEllipse(new Ellipse(centre, radius, radius), ink, line);
            target.DrawEllipse(new Ellipse(centre, radius * 0.85f, radius * 0.85f), ink, line);
            target.DrawLine(new Vector2(0, centre.Y), new Vector2(output.Width, centre.Y), ink, line);
            target.DrawLine(new Vector2(centre.X, 0), new Vector2(centre.X, output.Height), ink, line);

            IDWriteTextFormat format = drawing.Format("Segoe UI", radius * 1.1f, FontWeight.Bold);
            format.TextAlignment = TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Center;
            target.DrawText(number.ToString(CultureInfo.InvariantCulture), format, new Rect(0, 0, output.Width, output.Height), ink);
        });
    }

    /// <summary>A pie slice from twelve o'clock, clockwise, a fraction of a turn.</summary>
    private static ID2D1PathGeometry Pie(Drawing2D drawing, Vector2 centre, float radius, float fraction)
    {
        ID2D1PathGeometry geometry = drawing.Factory.CreatePathGeometry();
        using ID2D1GeometrySink sink = geometry.Open();
        float angle = fraction * MathF.PI * 2.0f;
        var top = new Vector2(centre.X, centre.Y - radius);
        var point = new Vector2(centre.X + (MathF.Sin(angle) * radius), centre.Y - (MathF.Cos(angle) * radius));

        sink.BeginFigure(centre, FigureBegin.Filled);
        sink.AddLine(top);
        sink.AddArc(new ArcSegment
        {
            Point = point,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = fraction > 0.5f ? ArcSize.Large : ArcSize.Small,
        });
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geometry;
    }
}

/// <summary>The sequence's timecode burned into the picture.</summary>
[Generator("gen.timecode", Name = "Timecode", Category = "Generators", Description = "Burns the sequence's time into the picture as timecode, a clock or a frame count, for review copies.")]
[Param("format", ParamType.Enum, Default = "timecode", Choices = "timecode, clock, frames", Animatable = false, Description = "HH:MM:SS:FF, HH:MM:SS.mmm, or the frame number.")]
[Param("corner", ParamType.Enum, Default = "bottom", Choices = "bottom, bottom-left, bottom-right, top, top-left, top-right, centre", Animatable = false, Description = "Where on the frame it sits.")]
[Param("margin", ParamType.Float, Default = "40", Min = 0, Max = 2000, SliderMax = 200, Unit = "px", Description = "Distance from the frame's edge, in sequence pixels.")]
[Param("size", ParamType.Float, Default = "48", Min = 4, Max = 1000, SliderMax = 200, Unit = "px", Description = "Text height, in sequence pixels.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The text.")]
[Param("box", ParamType.Color, Default = "#000000B3", Description = "The box behind the text; transparent for none.")]
[Param("prefix", ParamType.Text, Default = "", Animatable = false, Description = "Text before the time, such as a reel name.")]
public sealed class TimecodeGenerator : VideoGenerator
{
    /// <summary>The text for a sequence time.</summary>
    public static string TextAt(Flicks time, Rational rate, string format, string prefix)
    {
        string value = format switch
        {
            "clock" => Timecode.FormatClock(time),
            "frames" => time.ToFrames(rate, RoundingMode.Floor).ToString(CultureInfo.InvariantCulture),
            _ => Timecode.Format(time, rate),
        };

        return prefix.Length > 0 ? $"{prefix} {value}" : value;
    }

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        float scale = context.QualityScale;
        string text = TextAt(context.SequenceTime, context.FrameRate, parameters.Enum("format"), parameters.Text("prefix"));
        float size = parameters.Float("size") * scale;
        float margin = parameters.Float("margin") * scale;
        string corner = parameters.Enum("corner");

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            IDWriteTextFormat format = drawing.Format("Consolas", size);
            using IDWriteTextLayout layout = drawing.Text.CreateTextLayout(text, format, output.Width, output.Height);
            TextMetrics metrics = layout.Metrics;
            float pad = size * 0.25f;
            float width = metrics.WidthIncludingTrailingWhitespace + (pad * 2.0f);
            float height = metrics.Height + (pad * 2.0f);

            float x = corner.EndsWith("left", StringComparison.Ordinal) ? margin
                : corner.EndsWith("right", StringComparison.Ordinal) ? output.Width - margin - width
                : (output.Width - width) / 2.0f;
            float y = corner.StartsWith("top", StringComparison.Ordinal) ? margin
                : corner.StartsWith("bottom", StringComparison.Ordinal) ? output.Height - margin - height
                : (output.Height - height) / 2.0f;

            Vector4 boxColour = parameters.Color("box");
            if (boxColour.W > 0.0f)
            {
                using ID2D1SolidColorBrush box = drawing.Brush(boxColour);
                target.FillRectangle(new Rect(x, y, width, height), box);
            }

            using ID2D1SolidColorBrush ink = drawing.Brush(parameters.Color("colour"));
            target.DrawTextLayout(new Vector2(x + pad, y + pad), layout, ink);
        });
    }
}
