using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace JazzHands.Core.Effects;

/// <summary>
/// A curve as a parameter: control points written as text, <c>"0,0 0.25,0.2 1,1"</c>, x and y
/// each 0 to 1, and the smooth curve through them.
/// </summary>
/// <remarks>
/// Text so a curve is one readable value in the project file, on the command line and over MCP,
/// and one keyframeable (held) parameter, like a mask's path. Between points the curve is a
/// monotone cubic (Fritsch and Carlson): smooth, through every point, and never overshooting
/// between two of them, so a gentle S does not dip below black or poke above white. An empty or
/// unreadable curve is the identity, so a hand edit gone wrong changes nothing rather than failing
/// the frame. A periodic curve (hue) wraps: its ends meet as if 1 were 0.
/// </remarks>
public sealed class CurvePoints
{
    private readonly ImmutableArray<Vector2> _points;
    private readonly float[] _slopes;

    private CurvePoints(ImmutableArray<Vector2> points, bool periodic)
    {
        _points = points;
        IsPeriodic = periodic;
        _slopes = Slopes(points, periodic);
    }

    /// <summary>The straight line y = x.</summary>
    public static CurvePoints Identity { get; } = new([new Vector2(0, 0), new Vector2(1, 1)], periodic: false);

    /// <summary>The control points, in order of x.</summary>
    public ImmutableArray<Vector2> Points => _points;

    /// <summary>True for a curve whose ends wrap, like hue.</summary>
    public bool IsPeriodic { get; }

    /// <summary>True when the curve changes nothing: y = x, or for a flat curve y = 0.5 everywhere.</summary>
    public bool IsNeutral(float neutral = float.NaN) =>
        float.IsNaN(neutral)
            ? _points.All(point => MathF.Abs(point.X - point.Y) < 1e-4f)
            : _points.All(point => MathF.Abs(point.Y - neutral) < 1e-4f);

    /// <summary>
    /// Reads a curve. Empty text is <paramref name="fallback"/>. Points outside 0 to 1 are held
    /// inside; two points at one x keep the later.
    /// </summary>
    public static CurvePoints Parse(string? text, CurvePoints? fallback = null, bool periodic = false)
    {
        fallback ??= Identity;
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var points = new SortedDictionary<float, float>();
        foreach (string pair in text.Split([' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split(',');
            if (parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.IsFinite(x) || !float.IsFinite(y))
            {
                return fallback;
            }

            points[Math.Clamp(x, 0, 1)] = Math.Clamp(y, 0, 1);
        }

        return points.Count == 0
            ? fallback
            : new CurvePoints([.. points.Select(point => new Vector2(point.Key, point.Value))], periodic);
    }

    /// <summary>A curve through these points.</summary>
    public static CurvePoints From(IEnumerable<Vector2> points, bool periodic = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Parse(Format(points), periodic: periodic);
    }

    /// <summary>Writes points the way <see cref="Parse"/> reads them, shortest round trip.</summary>
    public static string Format(IEnumerable<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var text = new StringBuilder();
        foreach (Vector2 point in points.OrderBy(point => point.X))
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(point.X.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(point.Y.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Format(_points);

    /// <summary>The curve at x, 0 to 1.</summary>
    public float Evaluate(float x)
    {
        x = Math.Clamp(x, 0.0f, 1.0f);

        if (_points.Length == 1)
        {
            return _points[0].Y;
        }

        if (IsPeriodic)
        {
            return EvaluatePeriodic(x);
        }

        if (x <= _points[0].X)
        {
            return _points[0].Y;
        }

        if (x >= _points[^1].X)
        {
            return _points[^1].Y;
        }

        int index = Segment(x);
        return Hermite(_points[index], _points[index + 1], _slopes[index], _slopes[index + 1], x);
    }

    /// <summary>The curve sampled at <paramref name="count"/> evenly spaced points from 0 to 1.</summary>
    public float[] Bake(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);

        float[] values = new float[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = Evaluate(index / (float)(count - 1));
        }

        return values;
    }

    private static float Hermite(Vector2 a, Vector2 b, float slopeA, float slopeB, float x)
    {
        float h = b.X - a.X;
        if (h <= 1e-6f)
        {
            return b.Y;
        }

        float t = (x - a.X) / h;
        float t2 = t * t;
        float t3 = t2 * t;
        float value = ((2 * t3) - (3 * t2) + 1) * a.Y
            + (t3 - (2 * t2) + t) * h * slopeA
            + ((-2 * t3) + (3 * t2)) * b.Y
            + (t3 - t2) * h * slopeB;
        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private int Segment(float x)
    {
        int low = 0;
        int high = _points.Length - 2;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_points[middle].X <= x)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>Between the last point and the first, round the wrap.</summary>
    private float EvaluatePeriodic(float x)
    {
        Vector2 first = _points[0];
        Vector2 last = _points[^1];

        if (x >= first.X && x <= last.X)
        {
            int index = Segment(x);
            return Hermite(_points[index], _points[index + 1], _slopes[index], _slopes[index + 1], x);
        }

        // The gap from the last point to the first, one turn on.
        var from = last;
        var to = new Vector2(first.X + 1.0f, first.Y);
        float wrapped = x < first.X ? x + 1.0f : x;
        return Hermite(from, to, _slopes[^1], _slopes[0], wrapped);
    }

    /// <summary>Fritsch and Carlson: secant slopes, averaged, then limited so no segment overshoots.</summary>
    private static float[] Slopes(ImmutableArray<Vector2> points, bool periodic)
    {
        int count = points.Length;
        float[] slopes = new float[count];
        if (count < 2)
        {
            return slopes;
        }

        float Secant(int index)
        {
            Vector2 a = points[index];
            Vector2 b = index + 1 < count ? points[index + 1] : new Vector2(points[0].X + 1.0f, points[0].Y);
            float h = b.X - a.X;
            return h <= 1e-6f ? 0.0f : (b.Y - a.Y) / h;
        }

        int segments = periodic ? count : count - 1;
        float[] secants = new float[segments];
        for (int index = 0; index < segments; index++)
        {
            secants[index] = Secant(index);
        }

        for (int index = 0; index < count; index++)
        {
            bool hasBefore = periodic || index > 0;
            bool hasAfter = periodic || index < count - 1;
            float before = hasBefore ? secants[(index - 1 + segments) % segments] : secants[0];
            float after = hasAfter ? secants[index % segments] : secants[segments - 1];

            slopes[index] = !hasBefore ? after
                : !hasAfter ? before
                : before * after <= 0 ? 0.0f
                : (before + after) / 2.0f;
        }

        // Limit each segment's end slopes to three times its secant, which keeps it monotone.
        for (int index = 0; index < segments; index++)
        {
            float secant = secants[index];
            int next = (index + 1) % count;
            if (MathF.Abs(secant) < 1e-6f)
            {
                slopes[index] = 0.0f;
                slopes[next] = 0.0f;
                continue;
            }

            float alpha = slopes[index] / secant;
            float beta = slopes[next] / secant;
            float length = (alpha * alpha) + (beta * beta);
            if (length > 9.0f)
            {
                float tau = 3.0f / MathF.Sqrt(length);
                slopes[index] = tau * alpha * secant;
                slopes[next] = tau * beta * secant;
            }
        }

        return slopes;
    }
}
