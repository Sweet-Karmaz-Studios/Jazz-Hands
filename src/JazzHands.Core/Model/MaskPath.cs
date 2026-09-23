using System.Globalization;
using System.Numerics;

namespace JazzHands.Core.Model;

/// <summary>One piece of a mask outline: a straight line or a cubic bezier to <see cref="End"/>.</summary>
/// <param name="End">Where the segment ends.</param>
/// <param name="IsCurve">True for a cubic bezier, false for a straight line.</param>
/// <param name="Control1">The first control point of a curve.</param>
/// <param name="Control2">The second control point of a curve.</param>
public readonly record struct MaskPathSegment(Vector2 End, bool IsCurve, Vector2 Control1 = default, Vector2 Control2 = default);

/// <summary>A closed outline.</summary>
/// <param name="Start">Where it begins.</param>
/// <param name="Segments">The segments, in order; the last one closes back to the start.</param>
public sealed record MaskPathFigure(Vector2 Start, List<MaskPathSegment> Segments);

/// <summary>
/// Mask outlines in SVG path syntax: M, L, H, V, C, Q and Z, absolute and relative.
/// </summary>
/// <remarks>
/// In Core rather than beside the rasterizer, so that <c>mask.add</c> refuses a path it cannot read
/// at the moment it is typed rather than the preview drawing nothing later.
/// </remarks>
public static class MaskPath
{
    /// <summary>Reads SVG path data into closed figures. Quadratic curves become cubic ones.</summary>
    /// <exception cref="FormatException">The data is not a path this reads.</exception>
    public static List<MaskPathFigure> Parse(string data)
    {
        var figures = new List<MaskPathFigure>();
        var tokens = new PathTokens(data ?? string.Empty);
        MaskPathFigure? figure = null;
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
                    figure = new MaskPathFigure(current, []);
                    figures.Add(figure);

                    // Pairs after a move are lines, as SVG says.
                    command = relative ? 'l' : 'L';
                    break;

                case 'L':
                    current = origin + tokens.Point();
                    Require(figure).Segments.Add(new MaskPathSegment(current, false));
                    break;

                case 'H':
                    current = new Vector2((relative ? current.X : 0.0f) + tokens.Number(), current.Y);
                    Require(figure).Segments.Add(new MaskPathSegment(current, false));
                    break;

                case 'V':
                    current = new Vector2(current.X, (relative ? current.Y : 0.0f) + tokens.Number());
                    Require(figure).Segments.Add(new MaskPathSegment(current, false));
                    break;

                case 'C':
                    Vector2 c1 = origin + tokens.Point();
                    Vector2 c2 = origin + tokens.Point();
                    current = origin + tokens.Point();
                    Require(figure).Segments.Add(new MaskPathSegment(current, true, c1, c2));
                    break;

                case 'Q':
                    Vector2 control = origin + tokens.Point();
                    Vector2 end = origin + tokens.Point();
                    Vector2 from = current;
                    Require(figure).Segments.Add(new MaskPathSegment(
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

        static MaskPathFigure Require(MaskPathFigure? figure) =>
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
