using System.Numerics;

namespace JazzHands.Media.Analysis;

/// <summary>Where a tracked point was found in one frame.</summary>
/// <param name="Position">Its centre, in the frame's pixels, to a fraction of a pixel.</param>
/// <param name="Confidence">How well the frame matched what was being followed, 0 to 1.</param>
public readonly record struct TrackedPoint(Vector2 Position, float Confidence);

/// <summary>
/// Follows a small square of picture from frame to frame by template matching: the square in the
/// first frame, found again in each next one near where its motion so far says it will be.
/// </summary>
/// <remarks>
/// The match is normalised cross correlation on brightness, so a frame that is lighter or darker
/// overall still matches, over every whole pixel offset within the search reach; the best is then
/// refined to a fraction of a pixel by fitting a parabola through it and its neighbours on each
/// axis. The square is always the first frame's, so the track does not drift as a template that
/// was updated every frame would. Confidence is the correlation at the match: near 1 is the same
/// picture, below about 0.5 the point is probably lost.
/// </remarks>
public sealed class PointTracker
{
    private readonly int _half;
    private readonly int _search;
    private readonly float[] _template;
    private readonly float _templateDeviation;
    private Vector2? _last;
    private Vector2? _before;

    /// <summary>Starts a track at a point of a frame.</summary>
    /// <param name="frame">The frame the point is picked on.</param>
    /// <param name="at">The point, in its pixels.</param>
    /// <param name="size">The side of the square followed, in pixels; odd.</param>
    /// <param name="search">How far from where it is expected the point may be found, in pixels.</param>
    public PointTracker(LumaImage frame, Vector2 at, int size = 31, int search = 48)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _half = Math.Max(2, size / 2);
        _search = Math.Max(1, search);
        int side = (_half * 2) + 1;
        _template = new float[side * side];

        float sum = 0;
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                float value = Sample(frame, at.X - _half + column, at.Y - _half + row);
                _template[(row * side) + column] = value;
                sum += value;
            }
        }

        float mean = sum / _template.Length;
        float variance = 0;
        for (int index = 0; index < _template.Length; index++)
        {
            _template[index] -= mean;
            variance += _template[index] * _template[index];
        }

        _templateDeviation = MathF.Sqrt(variance);
        _last = at;
    }

    /// <summary>Finds the point in the next frame, expecting it where its motion so far leads.</summary>
    public TrackedPoint Next(LumaImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Vector2 last = _last ?? Vector2.Zero;
        Vector2 expected = _before is { } before ? last + (last - before) : last;
        TrackedPoint found = Find(frame, expected);
        _before = last;
        _last = found.Position;
        return found;
    }

    /// <summary>The best match within the search reach of a place, refined to a fraction of a pixel.</summary>
    public TrackedPoint Find(LumaImage frame, Vector2 expected)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int centreX = (int)MathF.Round(expected.X);
        int centreY = (int)MathF.Round(expected.Y);
        int span = (_search * 2) + 1;
        float[] scores = new float[span * span];
        float best = float.MinValue;
        int bestX = 0;
        int bestY = 0;

        for (int dy = -_search; dy <= _search; dy++)
        {
            for (int dx = -_search; dx <= _search; dx++)
            {
                float score = Correlation(frame, centreX + dx, centreY + dy);
                scores[((dy + _search) * span) + dx + _search] = score;
                if (score > best)
                {
                    best = score;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        float Score(int dx, int dy) => scores[((dy + _search) * span) + dx + _search];

        float offsetX = bestX > -_search && bestX < _search ? Vertex(Score(bestX - 1, bestY), best, Score(bestX + 1, bestY)) : 0;
        float offsetY = bestY > -_search && bestY < _search ? Vertex(Score(bestX, bestY - 1), best, Score(bestX, bestY + 1)) : 0;
        return new TrackedPoint(new Vector2(centreX + bestX + offsetX, centreY + bestY + offsetY), Math.Clamp(best, 0, 1));
    }

    /// <summary>Where a parabola through three equally spaced values peaks, from the middle one, within half a step.</summary>
    private static float Vertex(float left, float middle, float right)
    {
        float curvature = left - (2 * middle) + right;
        return curvature < 0 ? Math.Clamp((left - right) / (2 * curvature), -0.5f, 0.5f) : 0;
    }

    private static float Sample(LumaImage frame, float x, float y)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        float top = float.Lerp(frame.At(x0, y0), frame.At(x0 + 1, y0), fx);
        float bottom = float.Lerp(frame.At(x0, y0 + 1), frame.At(x0 + 1, y0 + 1), fx);
        return float.Lerp(top, bottom, fy);
    }

    /// <summary>Normalised cross correlation of the template with the square centred on a pixel.</summary>
    private float Correlation(LumaImage frame, int x, int y)
    {
        int side = (_half * 2) + 1;
        float sum = 0;
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                sum += frame.At(x - _half + column, y - _half + row);
            }
        }

        float mean = sum / _template.Length;
        float cross = 0;
        float variance = 0;
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                float value = frame.At(x - _half + column, y - _half + row) - mean;
                cross += value * _template[(row * side) + column];
                variance += value * value;
            }
        }

        float denominator = MathF.Sqrt(variance) * _templateDeviation;
        return denominator < 1e-3f ? 0 : cross / denominator;
    }
}
