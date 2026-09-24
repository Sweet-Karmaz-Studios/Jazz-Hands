using System.Globalization;
using System.Numerics;
using System.Text;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;
using JazzHands.Render.Compositing;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DEffects = Vortice.Direct2D1.Effects;

namespace JazzHands.Render.Titles;

/// <summary>
/// Lays a title out with DirectWrite and draws it with Direct2D: the look into a layer, kept, and
/// the layer onto the frame each time.
/// </summary>
/// <remarks>
/// <para>
/// Text is laid out at its size times the quality scale and drawn at the working resolution, so a
/// 4K export draws 4K glyphs rather than enlarging a smaller bitmap. Drawing is into the half float
/// working format with linear premultiplied colours (<see cref="Drawing2D"/>), so a colour typed as
/// sRGB hex comes out as that colour and edges are antialiased in linear light, like every other
/// layer.
/// </para>
/// <para>
/// A layer holds, from the back: the box, the shadow (the letters and outline, blurred, tinted and
/// offset), the outline (each glyph's outline stroked twice its width with round joins, so after
/// the fill covers its inner half the width shows outside), and the fill. Its origin is on a whole
/// texel, so a title at rest is copied to the frame exactly.
/// </para>
/// <para>
/// Looks are kept by everything that changes the picture (text, style, frame size, quality and
/// project folder); the eight most recent are kept, which covers every title on screen at once
/// in any sane edit. Thread affine, like the compositor that owns it.
/// </para>
/// </remarks>
internal sealed class TitleRenderer : IDisposable
{
    private const int Capacity = 8;

    private readonly Drawing2D _drawing;
    private readonly RenderTargetPool _pool;
    private readonly Dictionary<string, LinkedListNode<Look>> _looks = new(StringComparer.Ordinal);
    private readonly LinkedList<Look> _order = new();
    private readonly GlyphPainter _painter;
    private readonly ID2D1StrokeStyle _round;

    public TitleRenderer(Drawing2D drawing, RenderTargetPool pool)
    {
        _drawing = drawing;
        _pool = pool;
        _round = drawing.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            LineJoin = LineJoin.Round,
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
        });
        _painter = new GlyphPainter(drawing, _round);
    }

    /// <summary>How many looks are kept.</summary>
    public int Cached => _looks.Count;

    /// <summary>How many looks have been drawn.</summary>
    public int Drawn { get; private set; }

    public void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        float fade = parameters.Float(TitleParams.Fade);
        string text = parameters.Text(TitleParams.Text);
        if (fade <= 0.0f || text.Length == 0)
        {
            _drawing.Draw(output, _ => { });
            return;
        }

        Look? look = Find(context, parameters, output);
        if (look is null)
        {
            _drawing.Draw(output, _ => { });
            return;
        }

        float reveal = parameters.Float(TitleParams.Reveal);
        string revealBy = parameters.Enum(TitleParams.RevealBy);
        float soft = parameters.Float(TitleParams.RevealSoft);
        bool wipe = revealBy == "wipe";
        bool partial = reveal < 1.0f && !wipe;

        RenderTarget layer = look.Layer;
        RenderTarget? drawn = null;
        if (partial)
        {
            // Part way through a reveal by characters, words or lines: the letters are drawn
            // again with each unit's own opacity, over the same box.
            drawn = _pool.Rent(look.Layer.Width, look.Layer.Height);
            float[] alphas = TitleReveal.Alphas(look.Units(revealBy), look.Text.Plain.Length, reveal, soft);
            DrawLook(look, drawn, alphas);
            layer = drawn;
        }

        try
        {
            Place(context, parameters, output, look, layer, fade, wipe ? reveal : 1.0f, soft);
        }
        finally
        {
            if (drawn is not null)
            {
                _pool.Return(drawn);
            }
        }
    }

    public void Dispose()
    {
        foreach (Look look in _order)
        {
            look.Release(_pool);
        }

        _looks.Clear();
        _order.Clear();
        _round.Dispose();
    }

    /// <summary>The look for these parameters, kept or drawn now; null when the text has no size.</summary>
    private Look? Find(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        string key = KeyOf(context, parameters, output);
        if (_looks.TryGetValue(key, out LinkedListNode<Look>? node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value;
        }

        Look? look = Build(context, parameters, output, key);
        if (look is null)
        {
            return null;
        }

        _looks[key] = _order.AddFirst(look);
        while (_order.Count > Capacity)
        {
            Look oldest = _order.Last!.Value;
            _order.RemoveLast();
            _looks.Remove(oldest.Key);
            oldest.Release(_pool);
        }

        return look;
    }

    /// <summary>Everything that changes the look, as one string.</summary>
    private static string KeyOf(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        var key = new StringBuilder(256);
        key.Append(CultureInfo.InvariantCulture, $"{output.Width}x{output.Height}@{context.QualityScale:R}|{context.ProjectFolder}");
        for (int index = 0; index < parameters.Values.Length; index++)
        {
            string name = parameters.Descriptor.Params[index].Name;
            if (name == TitleParams.Reveal)
            {
                // The look is everything above the animation channels.
                break;
            }

            key.Append('|').Append(parameters.Values[index]);
        }

        return key.ToString();
    }

    /// <summary>Lays the text out, finds where it goes and how big its layer is, and draws it.</summary>
    private Look? Build(EffectContext context, ParameterSet parameters, RenderTarget output, string key)
    {
        float scale = context.QualityScale;
        TitleText text = TitleMarkup.Parse(parameters.Text(TitleParams.Text));
        if (text.Plain.Length == 0)
        {
            return null;
        }

        string folder = context.ProjectFolder;
        IDWriteFontCollection1 collection = FontCatalog.Collection(folder);
        FontWeight weight = Weight(parameters.Enum(TitleParams.Weight));
        float size = Math.Max(1.0f, parameters.Float(TitleParams.Size) * scale);
        float wrap = parameters.Float(TitleParams.Width) * scale;

        using IDWriteTextFormat format = FontCatalog.Factory.CreateTextFormat(
            FontCatalog.Resolve(parameters.Text(TitleParams.Font), folder),
            collection,
            weight,
            parameters.Bool(TitleParams.Italic) ? Vortice.DirectWrite.FontStyle.Italic : Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            size,
            "en-us");
        format.WordWrapping = wrap > 0.0f ? WordWrapping.Wrap : WordWrapping.NoWrap;
        format.ParagraphAlignment = ParagraphAlignment.Near;
        format.TextAlignment = parameters.Enum(TitleParams.Align) switch
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
                layout.SetFontStyle(Vortice.DirectWrite.FontStyle.Italic, range);
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
        OverhangMetrics overhang = layout.OverhangMetrics;
        float blockHeight = Math.Max(1.0f, metrics.Height);

        var frame = new Vector2(output.Width, output.Height);
        Vector2 anchor = (frame / 2.0f) + (parameters.Float2(TitleParams.Position) * scale);
        var origin = new Vector2(
            anchor.X - (parameters.Enum(TitleParams.Align) switch { "left" => 0.0f, "right" => blockWidth, _ => blockWidth / 2.0f }),
            anchor.Y - (parameters.Enum(TitleParams.VAlign) switch { "top" => 0.0f, "bottom" => blockHeight, _ => blockHeight / 2.0f }));

        // DirectWrite puts letter spacing after every letter, the last on a line too, which would
        // pull centred and right aligned text left of where it belongs.
        origin.X += parameters.Enum(TitleParams.Align) switch { "left" => 0.0f, "right" => tracking, _ => tracking / 2.0f };

        // What the layer must hold, in layout coordinates: the ink, the outline around it, the
        // box, and the shadow where it falls.
        float stroke = parameters.Float(TitleParams.StrokeWidth) * scale;
        Vector4 strokeColour = parameters.Color(TitleParams.Stroke);
        bool outlined = stroke > 0.0f && strokeColour.W > 0.0f;
        var ink = new Bounds(-overhang.Left, -overhang.Top, blockWidth + overhang.Right, blockHeight + overhang.Bottom);
        ink = Inflate(Union(ink, new Bounds(metrics.Left, metrics.Top, metrics.Left + metrics.Width, metrics.Top + metrics.Height)), (outlined ? stroke : 0.0f) + 1.0f);

        Vector4 boxColour = parameters.Color(TitleParams.Box);
        float padding = parameters.Float(TitleParams.BoxPadding) * scale;
        Bounds? box = boxColour.W > 0.0f
            ? new Bounds(metrics.Left - padding, metrics.Top - padding, metrics.Left + metrics.Width + padding, metrics.Top + metrics.Height + padding)
            : null;

        Vector4 shadowColour = parameters.Color(TitleParams.Shadow);
        Vector2 shadowOffset = parameters.Float2(TitleParams.ShadowOffset) * scale;
        float shadowSigma = parameters.Float(TitleParams.ShadowBlur) * scale / 2.0f;
        bool shadowed = shadowColour.W > 0.0f;

        Bounds content = box is { } boxed ? Union(ink, boxed) : ink;
        if (shadowed)
        {
            Bounds cast = Inflate(Offset(ink, shadowOffset), (shadowSigma * 3.0f) + 1.0f);
            content = Union(content, cast);
        }

        // On whole texels, inside a margin of a frame around the frame so a slide can bring in
        // what is off it, and no bigger than a texture may be.
        float left = MathF.Max(MathF.Floor(origin.X + content.Left) - 1.0f, -frame.X);
        float top = MathF.Max(MathF.Floor(origin.Y + content.Top) - 1.0f, -frame.Y);
        float right = MathF.Min(MathF.Ceiling(origin.X + content.Right) + 1.0f, frame.X * 2.0f);
        float bottom = MathF.Min(MathF.Ceiling(origin.Y + content.Bottom) + 1.0f, frame.Y * 2.0f);
        int width = (int)Math.Clamp(right - left, 1.0f, 16384.0f);
        int height = (int)Math.Clamp(bottom - top, 1.0f, 16384.0f);
        if (right <= left || bottom <= top)
        {
            layout.Dispose();
            return null;
        }

        var look = new Look(key, text, layout)
        {
            TopLeft = new Vector2(left, top),
            TextOrigin = origin - new Vector2(left, top),
            Centre = origin + new Vector2(metrics.Left + (metrics.Width / 2.0f), metrics.Top + (metrics.Height / 2.0f)),
            Colour = parameters.Color(TitleParams.Colour),
            Outline = outlined ? (strokeColour, stroke) : null,
            Box = box is { } rect ? (Offset(rect, origin - new Vector2(left, top)), boxColour, parameters.Float(TitleParams.BoxRadius) * scale) : null,
            Shadow = shadowed ? (shadowColour, shadowOffset, shadowSigma) : null,
            Layer = _pool.Rent(width, height),
        };

        look.Source = _drawing.Source(look.Layer);
        DrawLook(look, look.Layer, alphas: null);
        Drawn++;
        return look;
    }

    /// <summary>Draws a look into a layer: box, shadow, outline and fill, each unit of the text at its opacity.</summary>
    private void DrawLook(Look look, RenderTarget layer, float[]? alphas)
    {
        List<ID2D1SolidColorBrush> brushes = Brushes(look, alphas);
        try
        {
            if (look.Shadow is { } shadow)
            {
                // The letters on their own first, for the shadow to be made from.
                RenderTarget ink = _pool.Rent(layer.Width, layer.Height);
                try
                {
                    _drawing.Draw(ink, target => Letters(target, look));
                    using ID2D1Bitmap1 letters = _drawing.Source(ink);
                    _drawing.Draw(layer, target =>
                    {
                        Box(target, look);
                        using var cast = new D2DEffects.Shadow(target);
                        cast.SetInput(0, letters, true);
                        cast.BlurStandardDeviation = Math.Max(0.0f, shadow.Sigma);
                        cast.Color = Straight(shadow.Colour);
                        target.DrawImage(cast.Output, shadow.Offset, null, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                        target.DrawImage(letters, Vector2.Zero, null, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
                    });
                }
                finally
                {
                    _pool.Return(ink);
                }
            }
            else
            {
                _drawing.Draw(layer, target =>
                {
                    Box(target, look);
                    Letters(target, look);
                });
            }
        }
        finally
        {
            foreach (ID2D1SolidColorBrush brush in brushes)
            {
                brush.Dispose();
            }

            look.ClearEffects();
        }
    }

    /// <summary>
    /// Gives every stretch of the text that has one colour and one opacity a brush of its own, as
    /// the layout's drawing effect, so DirectWrite hands the painter a run per stretch.
    /// </summary>
    private List<ID2D1SolidColorBrush> Brushes(Look look, float[]? alphas)
    {
        string plain = look.Text.Plain;
        var brushes = new List<ID2D1SolidColorBrush>();
        int start = 0;
        while (start < plain.Length)
        {
            TitleStyle style = look.Text.StyleAt(start);
            float alpha = alphas?[start] ?? 1.0f;
            int end = start + 1;
            while (end < plain.Length && look.Text.StyleAt(end) == style && (alphas?[end] ?? 1.0f) == alpha)
            {
                end++;
            }

            Vector4 fill = style.Color is { } hex && ParamValues.TryParseColor(hex, out Vector4 linear) ? linear : look.Colour;
            ID2D1SolidColorBrush brush = _drawing.Brush(fill);
            brush.Opacity = alpha;
            brushes.Add(brush);
            look.Layout.SetDrawingEffect(brush, new TextRange((uint)start, (uint)(end - start)));
            start = end;
        }

        return brushes;
    }

    private static void Box(ID2D1DeviceContext target, Look look)
    {
        if (look.Box is not { } box)
        {
            return;
        }

        using ID2D1SolidColorBrush fill = BrushOn(target, box.Colour);
        var rect = new Rect(box.Rect.Left, box.Rect.Top, box.Rect.Right - box.Rect.Left, box.Rect.Bottom - box.Rect.Top);
        if (box.Radius > 0.0f)
        {
            float radius = Math.Min(box.Radius, Math.Min(rect.Width, rect.Height) / 2.0f);
            target.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius, radius), fill);
        }
        else
        {
            target.FillRectangle(rect, fill);
        }
    }

    /// <summary>The outline of every glyph, then every glyph's fill over it.</summary>
    private void Letters(ID2D1DeviceContext target, Look look)
    {
        if (look.Outline is { } outline)
        {
            _painter.Begin(target, outline: true, outline.Colour, outline.Width);
            look.Layout.Draw(IntPtr.Zero, _painter, look.TextOrigin.X, look.TextOrigin.Y);
        }

        _painter.Begin(target, outline: false, Vector4.Zero, 0.0f);
        look.Layout.Draw(IntPtr.Zero, _painter, look.TextOrigin.X, look.TextOrigin.Y);
    }

    /// <summary>Puts a layer on the frame: moved, scaled about the text's centre, blurred, faded and wiped.</summary>
    private void Place(EffectContext context, ParameterSet parameters, RenderTarget output, Look look, RenderTarget layer, float fade, float wipe, float soft)
    {
        float scale = context.QualityScale;
        float zoom = parameters.Float(TitleParams.Zoom);
        Vector2 offset = parameters.Float2(TitleParams.Offset) * scale;
        float blur = parameters.Float(TitleParams.Blur) * scale;
        if (zoom <= 0.0f)
        {
            _drawing.Draw(output, _ => { });
            return;
        }

        Matrix3x2 place = Matrix3x2.CreateTranslation(look.TopLeft)
            * Matrix3x2.CreateTranslation(-look.Centre)
            * Matrix3x2.CreateScale(zoom)
            * Matrix3x2.CreateTranslation(look.Centre + offset);
        bool exact = MathF.Abs(zoom - 1.0f) < 1e-6f && IsWhole(place.M31) && IsWhole(place.M32);

        using ID2D1Bitmap1 source = ReferenceEquals(layer, look.Layer) ? look.Source!.QueryInterface<ID2D1Bitmap1>() : _drawing.Source(layer);
        _drawing.Draw(output, target =>
        {
            target.Transform = place;
            var bounds = new Vortice.RawRectF(0, 0, layer.Width, layer.Height);

            if (blur <= 0.0f && wipe >= 1.0f)
            {
                var mode = exact ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor
                    : zoom < 1.0f ? Vortice.Direct2D1.InterpolationMode.HighQualityCubic
                    : Vortice.Direct2D1.InterpolationMode.Linear;
                target.DrawBitmap(source, bounds, fade, mode, null, null);
                return;
            }

            ID2D1LinearGradientBrush? edge = wipe < 1.0f ? Wipe(target, layer.Width, wipe, soft) : null;
            ID2D1GradientStopCollection? stops = edge?.GradientStopCollection;
            try
            {
                target.PushLayer(
                    new LayerParameters1
                    {
                        ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f),
                        Opacity = fade,
                        OpacityBrush = edge!,
                        MaskTransform = Matrix3x2.Identity,
                        LayerOptions = LayerOptions1.None,
                    },
                    null!);

                if (blur > 0.0f)
                {
                    using var soften = new D2DEffects.GaussianBlur(target);
                    soften.SetInput(0, source, true);
                    soften.StandardDeviation = blur / 3.0f;
                    soften.BorderMode = BorderMode.Soft;
                    target.DrawImage(soften.Output, Vector2.Zero, null, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                }
                else
                {
                    target.DrawBitmap(source, bounds, 1.0f, exact ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor : Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                }

                target.PopLayer();
            }
            finally
            {
                stops?.Dispose();
                edge?.Dispose();
            }
        });
    }

    /// <summary>
    /// The opacity across a wipe: shown left of a soft edge, hidden right of it. The edge crosses
    /// the layer as the reveal goes from 0 to 1, starting just off its left and ending just off
    /// its right, so both ends are clean.
    /// </summary>
    private static ID2D1LinearGradientBrush Wipe(ID2D1DeviceContext target, int width, float reveal, float soft)
    {
        float edge = Math.Max(1.0f, width * Math.Clamp(soft, 0.0f, 20.0f) / 10.0f);
        float centre = (-edge / 2.0f) + (reveal * (width + edge));
        GradientStop[] stops =
        [
            new(0.0f, new Color4(0, 0, 0, 1)),
            new(1.0f, new Color4(0, 0, 0, 0)),
        ];

        using ID2D1GradientStopCollection collection = target.CreateGradientStopCollection(stops, Gamma.Linear, ExtendMode.Clamp);
        return target.CreateLinearGradientBrush(
            new LinearGradientBrushProperties(new Vector2(centre - (edge / 2.0f), 0), new Vector2(centre + (edge / 2.0f), 0)),
            collection);
    }

    private static bool IsWhole(float value) => MathF.Abs(value - MathF.Round(value)) < 1e-4f;

    private static ID2D1SolidColorBrush BrushOn(ID2D1DeviceContext target, Vector4 premultiplied)
    {
        Vector4 straight = Straight(premultiplied);
        return target.CreateSolidColorBrush(new Color4(straight.X, straight.Y, straight.Z, straight.W));
    }

    /// <summary>Direct2D takes straight colour; the model holds premultiplied.</summary>
    private static Vector4 Straight(Vector4 premultiplied) => premultiplied.W > 0.0f
        ? new Vector4(premultiplied.X / premultiplied.W, premultiplied.Y / premultiplied.W, premultiplied.Z / premultiplied.W, premultiplied.W)
        : Vector4.Zero;

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

    private static Bounds Union(Bounds a, Bounds b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static Bounds Inflate(Bounds rect, float by) => new(rect.Left - by, rect.Top - by, rect.Right + by, rect.Bottom + by);

    private static Bounds Offset(Bounds rect, Vector2 by) => new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);

    /// <summary>A title's look: its layout, where it goes and its drawn layer.</summary>
    private sealed class Look(string key, TitleText text, IDWriteTextLayout layout)
    {
        private readonly Dictionary<string, (int Start, int Length)[]> _units = new(StringComparer.Ordinal);

        public string Key { get; } = key;

        public TitleText Text { get; } = text;

        public IDWriteTextLayout Layout { get; } = layout;

        /// <summary>The layer's top left on the frame, in texels.</summary>
        public Vector2 TopLeft { get; init; }

        /// <summary>Where the layout's origin is inside the layer.</summary>
        public Vector2 TextOrigin { get; init; }

        /// <summary>The middle of the text on the frame, which zoom scales about.</summary>
        public Vector2 Centre { get; init; }

        public Vector4 Colour { get; init; }

        public (Vector4 Colour, float Width)? Outline { get; init; }

        public (Bounds Rect, Vector4 Colour, float Radius)? Box { get; init; }

        public (Vector4 Colour, Vector2 Offset, float Sigma)? Shadow { get; init; }

        public required RenderTarget Layer { get; init; }

        public ID2D1Bitmap1? Source { get; set; }

        /// <summary>The stretches a reveal counts, found once per kind.</summary>
        public (int Start, int Length)[] Units(string by)
        {
            if (!_units.TryGetValue(by, out (int Start, int Length)[]? units))
            {
                units = by == "lines" ? Lines() : TitleReveal.Units(Text.Plain, by == "words");
                _units[by] = units;
            }

            return units;
        }

        public void ClearEffects() => Layout.SetDrawingEffect(null, new TextRange(0, (uint)Text.Plain.Length));

        public void Release(RenderTargetPool pool)
        {
            Source?.Dispose();
            Layout.Dispose();
            pool.Return(Layer);
        }

        private (int Start, int Length)[] Lines()
        {
            LineMetrics[] lines = Layout.LineMetrics;
            var units = new List<(int Start, int Length)>(lines.Length);
            int start = 0;
            foreach (LineMetrics line in lines)
            {
                int length = (int)line.Length;
                if (Text.Plain.AsSpan(start, Math.Min(length, Text.Plain.Length - start)).Trim().Length > 0)
                {
                    units.Add((start, length));
                }

                start += length;
            }

            return [.. units];
        }
    }
}

/// <summary>A rectangle by its edges.</summary>
internal readonly record struct Bounds(float Left, float Top, float Right, float Bottom);
