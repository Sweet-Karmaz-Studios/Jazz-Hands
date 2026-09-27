using System.Globalization;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace JazzHands.Render.Effects.Generators;

/// <summary>A camcorder's on-screen date and time, ticking with the clip.</summary>
[Generator("gen.date-stamp", Name = "Date stamp", Category = "Generators", Description = "A camcorder's date and time in the corner, ticking on from a start as the clip plays, with PLAY in the other corner: put it over video.vhs for home video.")]
[Param("start", ParamType.Text, Default = "1998-12-24 21:37:00", Animatable = false, Description = "The date and time at the clip's start, as year-month-day hours:minutes:seconds.")]
[Param("size", ParamType.Float, Default = "44", Min = 4, Max = 1000, SliderMax = 200, Unit = "px", Description = "Text height, in sequence pixels.")]
[Param("margin", ParamType.Float, Default = "70", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "Distance from the frame's edges.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The text.")]
[Param("play", ParamType.Bool, Default = "true", Animatable = false, Description = "Show PLAY in the top left.")]
public sealed class DateStampGenerator : VideoGenerator, ITimedGenerator
{
    /// <summary>The two lines at a time from the clip's start, or null when the start does not read.</summary>
    public static (string Date, string Clock)? TextAt(string start, double seconds)
    {
        if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at))
        {
            return null;
        }

        DateTime now = at.AddSeconds(Math.Floor(Math.Max(seconds, 0)));
        return (
            now.ToString("MMM. dd yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(),
            now.ToString("h:mm:ss tt", CultureInfo.InvariantCulture).ToUpperInvariant());
    }

    /// <inheritdoc />
    public override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        (string Date, string Clock) lines = TextAt(parameters.Text("start"), context.Time.ToSeconds()) ?? ("--- -- ----", "-:--:-- --");
        float scale = context.QualityScale;
        float size = parameters.Float("size") * scale;
        float margin = parameters.Float("margin") * scale;
        bool play = parameters.Bool("play");

        Drawing2D drawing = context.Drawing;
        drawing.Draw(output, target =>
        {
            IDWriteTextFormat format = drawing.Format("Consolas", size);
            using ID2D1SolidColorBrush ink = drawing.Brush(parameters.Color("colour"));
            using ID2D1SolidColorBrush shadow = drawing.Brush(new Vector4(0, 0, 0, 0.8f));
            float offset = MathF.Max(1, size * 0.06f);

            void Line(string text, float x, float y, bool right)
            {
                using IDWriteTextLayout layout = drawing.Text.CreateTextLayout(text, format, output.Width, output.Height);
                float left = right ? x - layout.Metrics.WidthIncludingTrailingWhitespace : x;
                target.DrawTextLayout(new Vector2(left + offset, y + offset), layout, shadow);
                target.DrawTextLayout(new Vector2(left, y), layout, ink);
            }

            if (play)
            {
                Line("PLAY ▶", margin, margin, right: false);
            }

            float lineHeight = size * 1.25f;
            Line(lines.Date, output.Width - margin, output.Height - margin - (lineHeight * 2), right: true);
            Line(lines.Clock, output.Width - margin, output.Height - margin - lineHeight, right: true);
        });
    }
}
