using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;

namespace JazzHands.App.Controls;

/// <summary>
/// A title's text as a WPF document and back: a paragraph a line, a run a styled stretch.
/// </summary>
/// <remarks>
/// Bold, italic and underline are the run's own; a colour is its foreground, a font its family.
/// A size is shown relative to the title's so a small second line looks smaller without being
/// unreadable: the editor's size times the square root of the span's share of the title's, which
/// reads back exactly. Whatever a person does in the box (types, pastes, deletes across lines)
/// the document is read back through the effective values of each run, so a style WPF moved to an
/// enclosing element still counts.
/// </remarks>
public static class TitleDocument
{
    /// <summary>The editor's text size, in device-independent pixels, which the title's own size is shown at.</summary>
    public const double EditorSize = 15.0;

    /// <summary>A document showing a title's text.</summary>
    /// <param name="text">The text and its styles.</param>
    /// <param name="baseSize">The title's own size, which span sizes are shown against.</param>
    public static FlowDocument ToDocument(TitleText text, double baseSize)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = new FlowDocument { FontSize = EditorSize, PagePadding = new Thickness(2) };
        int at = 0;
        foreach (string line in text.Plain.Split('\n'))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            int end = at + line.Length;
            foreach (TitleSpan span in Pieces(text, at, end))
            {
                paragraph.Inlines.Add(Styled(new Run(text.Plain.Substring(span.Start, span.Length)), span.Style, baseSize));
            }

            document.Blocks.Add(paragraph);
            at = end + 1;
        }

        return document;
    }

    /// <summary>The text and styles a document shows.</summary>
    public static TitleText FromDocument(FlowDocument document, double baseSize)
    {
        ArgumentNullException.ThrowIfNull(document);

        var plain = new StringBuilder();
        var spans = new List<TitleSpan>();
        bool first = true;
        foreach (Paragraph paragraph in document.Blocks.OfType<Paragraph>())
        {
            if (!first)
            {
                plain.Append('\n');
            }

            first = false;
            Read(paragraph.Inlines, document, baseSize, plain, spans);
        }

        return new TitleText(plain.ToString(), new EquatableArray<TitleSpan>(Merge(spans)));
    }

    /// <summary>A place in the document as an offset into the title's plain text.</summary>
    public static int Offset(FlowDocument document, TextPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pointer);
        return new TextRange(document.ContentStart, pointer).Text.Replace("\r\n", "\n", StringComparison.Ordinal).Length;
    }

    /// <summary>The place in the document at an offset into the plain text, or its end past that.</summary>
    public static TextPointer Pointer(FlowDocument document, int offset)
    {
        ArgumentNullException.ThrowIfNull(document);

        int left = Math.Max(0, offset);
        foreach (Paragraph paragraph in document.Blocks.OfType<Paragraph>())
        {
            foreach (Run run in Runs(paragraph.Inlines))
            {
                if (left <= run.Text.Length)
                {
                    return run.ContentStart.GetPositionAtOffset(left) ?? run.ContentEnd;
                }

                left -= run.Text.Length;
            }

            if (left == 0)
            {
                return paragraph.ContentEnd;
            }

            left--;
        }

        return document.ContentEnd;
    }

    /// <summary>A run with a style's formatting.</summary>
    private static Run Styled(Run run, TitleStyle style, double baseSize)
    {
        if (style.Bold)
        {
            run.FontWeight = FontWeights.Bold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.Underline)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        if (style.Color is { } hex && Brush(hex) is { } brush)
        {
            run.Foreground = brush;
        }

        if (style.Size is { } size && baseSize > 0)
        {
            run.FontSize = EditorSize * Math.Sqrt(size / baseSize);
        }

        if (style.Font is { } font)
        {
            run.FontFamily = new FontFamily(font);
        }

        return run;
    }

    private static void Read(InlineCollection inlines, FlowDocument document, double baseSize, StringBuilder plain, List<TitleSpan> spans)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run when run.Text.Length > 0:
                    spans.Add(new TitleSpan(plain.Length, run.Text.Length, StyleOf(run, document, baseSize)));
                    plain.Append(run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                    break;
                case LineBreak:
                    plain.Append('\n');
                    break;
                case Span span:
                    Read(span.Inlines, document, baseSize, plain, spans);
                    break;
            }
        }
    }

    /// <summary>What a run shows that the document as a whole does not.</summary>
    private static TitleStyle StyleOf(Run run, FlowDocument document, double baseSize)
    {
        string? colour = run.Foreground is SolidColorBrush brush
            && !(document.Foreground is SolidColorBrush plain && plain.Color == brush.Color)
            ? Hex(brush.Color)
            : null;

        float? size = Math.Abs(run.FontSize - document.FontSize) > 0.01 && baseSize > 0
            ? (float)Math.Round(baseSize * Math.Pow(run.FontSize / EditorSize, 2), 2)
            : null;

        string? font = !string.Equals(run.FontFamily.Source, document.FontFamily.Source, StringComparison.Ordinal) ? run.FontFamily.Source : null;

        return new TitleStyle(
            Bold: run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight() && document.FontWeight.ToOpenTypeWeight() < FontWeights.SemiBold.ToOpenTypeWeight(),
            Italic: run.FontStyle == FontStyles.Italic,
            Underline: Underlined(run),
            Color: colour,
            Size: size,
            Font: font);
    }

    /// <summary>True when the run or anything it sits in draws an underline.</summary>
    private static bool Underlined(Inline inline)
    {
        for (DependencyObject? at = inline; at is not null; at = (at as TextElement)?.Parent)
        {
            TextDecorationCollection? decorations = at switch
            {
                Inline element => element.TextDecorations,
                Paragraph paragraph => paragraph.TextDecorations,
                _ => null,
            };

            if (decorations?.Any(decoration => decoration.Location == TextDecorationLocation.Underline) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (Run inner in Runs(span.Inlines))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>The spans' pieces between two offsets, plain where none says otherwise.</summary>
    private static IEnumerable<TitleSpan> Pieces(TitleText text, int start, int end)
    {
        int at = start;
        while (at < end)
        {
            TitleStyle style = text.StyleAt(at);
            int next = at + 1;
            while (next < end && text.StyleAt(next) == style)
            {
                next++;
            }

            yield return new TitleSpan(at, next - at, style);
            at = next;
        }
    }

    private static List<TitleSpan> Merge(List<TitleSpan> spans)
    {
        var merged = new List<TitleSpan>(spans.Count);
        foreach (TitleSpan span in spans)
        {
            if (merged.Count > 0 && merged[^1].Style == span.Style && merged[^1].End == span.Start)
            {
                merged[^1] = merged[^1] with { Length = merged[^1].Length + span.Length };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    private static SolidColorBrush? Brush(string hex)
    {
        string digits = hex.TrimStart('#');
        if ((digits.Length != 6 && digits.Length != 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
        {
            return null;
        }

        if (digits.Length == 6)
        {
            packed = (packed << 8) | 0xFF;
        }

        var brush = new SolidColorBrush(Color.FromArgb((byte)packed, (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8)));
        brush.Freeze();
        return brush;
    }

    private static string Hex(Color colour) => colour.A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}");
}
