using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Titles;
using Vortice.DirectWrite;

namespace JazzHands.Render.Titles;

/// <summary>Where a title's text sits on the frame, in sequence pixels from the frame centre, before the clip's own transform.</summary>
/// <param name="Block">The text block (x, y, width, height): what the alignments place at the position.</param>
/// <param name="Text">The lines' own extent inside the block, which the box pads.</param>
/// <param name="Box">The box, when there is one; otherwise the text's extent.</param>
/// <param name="Lines">How many lines it takes, wrapping included.</param>
public readonly record struct TitleBounds(Vector4 Block, Vector4 Text, Vector4 Box, int Lines)
{
    /// <summary>The middle of the text, which a title scales and turns about.</summary>
    public Vector2 Centre => new(Text.X + (Text.Z / 2.0f), Text.Y + (Text.W / 2.0f));
}

/// <summary>
/// A title's text laid out by DirectWrite at a quality scale, and where on the frame it goes.
/// </summary>
/// <remarks>
/// The one place the parameters become a layout, so the renderer and <see cref="Measure"/> (which
/// the preview's handles and <c>title.measure</c> use) agree to the pixel. DirectWrite's shared
/// factory is safe from any thread, so measuring needs no device.
/// </remarks>
internal sealed class TitleLayout : IDisposable
{
    private TitleLayout(TitleText text, IDWriteTextLayout layout)
    {
        Text = text;
        Layout = layout;
    }

    /// <summary>The text and its styles.</summary>
    public TitleText Text { get; }

    /// <summary>The layout, which the caller keeps or disposes with this.</summary>
    public IDWriteTextLayout Layout { get; }

    /// <summary>The block's width, texels.</summary>
    public float BlockWidth { get; private init; }

    /// <summary>The block's height, texels.</summary>
    public float BlockHeight { get; private init; }

    /// <summary>Where the layout's origin (the block's top left) goes on the frame, texels.</summary>
    public Vector2 Origin { get; private init; }

    /// <summary>The lines' extent, in layout coordinates.</summary>
    public TextMetrics Metrics { get; private init; }

    /// <summary>How far the ink reaches past the block.</summary>
    public OverhangMetrics Overhang { get; private init; }

    /// <summary>Lays a title out for a frame of a size, in texels; null when there is no text.</summary>
    /// <param name="parameters">The title's parameters.</param>
    /// <param name="scale">Texels per sequence pixel.</param>
    /// <param name="frame">The frame, in texels.</param>
    /// <param name="folder">The project's folder, for its fonts; empty for none.</param>
    public static TitleLayout? Lay(ParameterSet parameters, float scale, Vector2 frame, string folder)
    {
        TitleText text = TitleMarkup.Parse(parameters.Text(TitleParams.Text));
        if (text.Plain.Length == 0)
        {
            return null;
        }

        IDWriteFontCollection1 collection = FontCatalog.Collection(folder);
        FontWeight weight = Weight(parameters.Enum(TitleParams.Weight));
        float size = Math.Max(1.0f, parameters.Float(TitleParams.Size) * scale);
        float wrap = parameters.Float(TitleParams.Width) * scale;
        string align = parameters.Enum(TitleParams.Align);

        using IDWriteTextFormat format = FontCatalog.Factory.CreateTextFormat(
            FontCatalog.Resolve(parameters.Text(TitleParams.Font), folder),
            collection,
            weight,
            parameters.Bool(TitleParams.Italic) ? FontStyle.Italic : FontStyle.Normal,
            FontStretch.Normal,
            size,
            "en-us");
        format.WordWrapping = wrap > 0.0f ? WordWrapping.Wrap : WordWrapping.NoWrap;
        format.ParagraphAlignment = ParagraphAlignment.Near;
        format.TextAlignment = align switch
        {
            "left" => TextAlignment.Leading,
            "right" => TextAlignment.Trailing,
            _ => TextAlignment.Center,
        };

        float spacing = parameters.Float(TitleParams.LineSpacing);
        if (MathF.Abs(spacing - 1.0f) > 1e-4f)
        {
            format.SetLineSpacing(LineSpacingMethod.Proportional, spacing, spacing);
        }

        IDWriteTextLayout layout = FontCatalog.Factory.CreateTextLayout(text.Plain, format, wrap > 0.0f ? wrap : 1_000_000.0f, 1_000_000.0f);
        foreach (TitleSpan span in text.Spans)
        {
            var range = new TextRange((uint)span.Start, (uint)span.Length);
            TitleStyle style = span.Style;
            if (style.Bold)
            {
                layout.SetFontWeight((FontWeight)Math.Min(900, (int)weight + 300), range);
            }

            if (style.Italic)
            {
                layout.SetFontStyle(FontStyle.Italic, range);
            }

            if (style.Underline)
            {
                layout.SetUnderline(true, range);
            }

            if (style.Size is { } spanSize)
            {
                layout.SetFontSize(Math.Max(1.0f, spanSize * scale), range);
            }

            if (style.Font is { } font)
            {
                layout.SetFontFamilyName(FontCatalog.Resolve(font, folder), range);
            }
        }

        float tracking = parameters.Float(TitleParams.Tracking) * scale;
        if (tracking != 0.0f)
        {
            using IDWriteTextLayout1 spaced = layout.QueryInterface<IDWriteTextLayout1>();
            spaced.SetCharacterSpacing(0.0f, tracking, 0.0f, new TextRange(0, (uint)text.Plain.Length));
        }

        // Without wrapping the text is as wide as its longest line, and lines align inside that.
        TextMetrics measured = layout.Metrics;
        float blockWidth = wrap > 0.0f ? wrap : Math.Max(1.0f, measured.WidthIncludingTrailingWhitespace);
        layout.MaxWidth = blockWidth;
        layout.MaxHeight = Math.Max(1.0f, measured.Height);
        TextMetrics metrics = layout.Metrics;
        float blockHeight = Math.Max(1.0f, metrics.Height);

        Vector2 anchor = (frame / 2.0f) + (parameters.Float2(TitleParams.Position) * scale);
        var origin = new Vector2(
            anchor.X - (align switch { "left" => 0.0f, "right" => blockWidth, _ => blockWidth / 2.0f }),
            anchor.Y - (parameters.Enum(TitleParams.VAlign) switch { "top" => 0.0f, "bottom" => blockHeight, _ => blockHeight / 2.0f }));

        // DirectWrite puts letter spacing after every letter, the last on a line too, which would
        // pull centred and right aligned text left of where it belongs.
        origin.X += align switch { "left" => 0.0f, "right" => tracking, _ => tracking / 2.0f };

        return new TitleLayout(text, layout)
        {
            BlockWidth = blockWidth,
            BlockHeight = blockHeight,
            Origin = origin,
            Metrics = metrics,
            Overhang = layout.OverhangMetrics,
        };
    }

    /// <summary>
    /// Where a title's text sits on a frame of the sequence's size, in sequence pixels from its
    /// centre; null when it has no text.
    /// </summary>
    public static TitleBounds? Measure(ParameterSet parameters, Vector2 frame, string folder)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using TitleLayout? laid = Lay(parameters, 1.0f, frame, folder);
        if (laid is null)
        {
            return null;
        }

        Vector2 origin = laid.Origin - (frame / 2.0f);
        TextMetrics metrics = laid.Metrics;
        var text = new Vector4(origin.X + metrics.Left, origin.Y + metrics.Top, metrics.Width, metrics.Height);
        float padding = parameters.Color(TitleParams.Box).W > 0.0f ? parameters.Float(TitleParams.BoxPadding) : 0.0f;
        return new TitleBounds(
            new Vector4(origin.X, origin.Y, laid.BlockWidth, laid.BlockHeight),
            text,
            new Vector4(text.X - padding, text.Y - padding, text.Z + (padding * 2.0f), text.W + (padding * 2.0f)),
            (int)metrics.LineCount);
    }

    /// <summary>Lets go of the layout; a caller that keeps it takes <see cref="Layout"/> and does not call this.</summary>
    public void Dispose() => Layout.Dispose();

    private static FontWeight Weight(string name) => name switch
    {
        "thin" => FontWeight.Thin,
        "extra-light" => FontWeight.ExtraLight,
        "light" => FontWeight.Light,
        "medium" => FontWeight.Medium,
        "semibold" => FontWeight.SemiBold,
        "bold" => FontWeight.Bold,
        "extra-bold" => FontWeight.ExtraBold,
        "black" => FontWeight.Black,
        _ => FontWeight.Normal,
    };
}
