using System.Numerics;
using System.Text.Json.Serialization;

namespace JazzHands.Core.Stabilization;

/// <summary>
/// How the camera moved through a video, frame by frame: what stabilization analysis finds and
/// what the renderer corrects for.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is the camera's move from the frame before, in the analysed picture's pixels
/// (<see cref="Width"/> by <see cref="Height"/>) and radians: a camera that moves right makes the
/// picture move left. The first frame's move is zero. Summed, the moves are the camera's path.
/// </para>
/// <para>
/// Stabilizing keeps the path's slow movement and removes its shake: the path is smoothed with a
/// gaussian over <c>smoothing</c> frames either side, and each frame is shifted and turned by the
/// difference between where the camera was and where the smooth path says it should have been.
/// That depends only on the frame's neighbours, so any frame can be drawn on its own, which is what
/// scrubbing and seeking need.
/// </para>
/// </remarks>
public sealed class CameraMotion
{
    private readonly Dictionary<int, Corrections> _smoothed = [];
    private readonly Lock _gate = new();
    private double[]? _x;
    private double[]? _y;
    private double[]? _angle;

    /// <summary>Creates the motion of a video.</summary>
    /// <param name="width">The analysed picture's width.</param>
    /// <param name="height">The analysed picture's height.</param>
    /// <param name="dx">The camera's move right from the frame before, per frame.</param>
    /// <param name="dy">The camera's move down from the frame before, per frame.</param>
    /// <param name="da">The camera's turn clockwise from the frame before, per frame, in radians.</param>
    [JsonConstructor]
    public CameraMotion(int width, int height, float[] dx, float[] dy, float[] da)
    {
        ArgumentNullException.ThrowIfNull(dx);
        ArgumentNullException.ThrowIfNull(dy);
        ArgumentNullException.ThrowIfNull(da);
        if (dy.Length != dx.Length || da.Length != dx.Length)
        {
            throw new ArgumentException("Every frame needs a move in x, in y and a turn.", nameof(dx));
        }

        Width = width;
        Height = height;
        Dx = dx;
        Dy = dy;
        Da = da;
    }

    /// <summary>The version of the analysis this build writes; an older one is made again.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The version of the analysis that made this.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>The analysed picture's width.</summary>
    public int Width { get; }

    /// <summary>The analysed picture's height.</summary>
    public int Height { get; }

    /// <summary>The camera's move right from the frame before.</summary>
    public float[] Dx { get; }

    /// <summary>The camera's move down from the frame before.</summary>
    public float[] Dy { get; }

    /// <summary>The camera's turn clockwise from the frame before, in radians.</summary>
    public float[] Da { get; }

    /// <summary>How many frames were analysed.</summary>
    [JsonIgnore]
    public int Count => Dx.Length;

    /// <summary>
    /// The correction for one frame: how far to move the picture, in analysed pixels, and how far
    /// to turn it about its centre, in radians clockwise, to put the camera on its smooth path.
    /// </summary>
    /// <param name="frame">The source frame, from zero. Outside the analysis it is clamped.</param>
    /// <param name="smoothing">Frames either side the path is smoothed over; 0 leaves it as it is.</param>
    public StabilizeCorrection Correction(long frame, int smoothing)
    {
        if (Count == 0 || smoothing <= 0)
        {
            return default;
        }

        Corrections corrections = Smoothed(smoothing);
        int index = (int)Math.Clamp(frame, 0, Count - 1);
        return new StabilizeCorrection(new Vector2(corrections.X[index], corrections.Y[index]), corrections.Angle[index]);
    }

    /// <summary>
    /// The zoom that keeps the frame's edges covered through a stretch of frames: enough to hide
    /// the largest shift and turn the correction makes there.
    /// </summary>
    /// <param name="first">The first source frame the clip shows.</param>
    /// <param name="last">The last.</param>
    /// <param name="smoothing">As for <see cref="Correction"/>.</param>
    public float CoveringZoom(long first, long last, int smoothing)
    {
        if (Count == 0 || smoothing <= 0 || Width <= 0 || Height <= 0)
        {
            return 1.0f;
        }

        Corrections corrections = Smoothed(smoothing);
        int from = (int)Math.Clamp(Math.Min(first, last), 0, Count - 1);
        int to = (int)Math.Clamp(Math.Max(first, last), 0, Count - 1);
        float aspect = Math.Max((float)Width / Height, (float)Height / Width);
        float zoom = 1.0f;

        for (int index = from; index <= to; index++)
        {
            float angle = Math.Abs(corrections.Angle[index]);
            float turn = MathF.Cos(angle) + (MathF.Sin(angle) * aspect);
            float shift = 2.0f * Math.Max(Math.Abs(corrections.X[index]) / Width, Math.Abs(corrections.Y[index]) / Height);
            zoom = Math.Max(zoom, turn + shift);
        }

        return zoom;
    }

    /// <summary>The camera's path at a frame: its moves summed.</summary>
    public (double X, double Y, double Angle) PathAt(int frame)
    {
        EnsurePath();
        int index = Math.Clamp(frame, 0, Math.Max(0, Count - 1));
        return Count == 0 ? default : (_x![index], _y![index], _angle![index]);
    }

    private Corrections Smoothed(int smoothing)
    {
        lock (_gate)
        {
            if (_smoothed.TryGetValue(smoothing, out Corrections? known))
            {
                return known;
            }

            EnsurePath();
            double sigma = Math.Max(smoothing / 2.0, 0.5);
            double[] weights = new double[(2 * smoothing) + 1];
            for (int offset = -smoothing; offset <= smoothing; offset++)
            {
                weights[offset + smoothing] = Math.Exp(-(offset * offset) / (2.0 * sigma * sigma));
            }

            var result = new Corrections(new float[Count], new float[Count], new float[Count]);
            for (int index = 0; index < Count; index++)
            {
                double x = 0;
                double y = 0;
                double angle = 0;
                double total = 0;
                for (int offset = -smoothing; offset <= smoothing; offset++)
                {
                    // Past either end the path is held, so the ends are not pulled towards nothing.
                    int at = Math.Clamp(index + offset, 0, Count - 1);
                    double weight = weights[offset + smoothing];
                    x += _x![at] * weight;
                    y += _y![at] * weight;
                    angle += _angle![at] * weight;
                    total += weight;
                }

                // The camera went right of its smooth path, so the picture went left of where it
                // should be: move it back right by the difference, and turn it back the same way.
                result.X[index] = (float)(_x![index] - (x / total));
                result.Y[index] = (float)(_y![index] - (y / total));
                result.Angle[index] = (float)(_angle![index] - (angle / total));
            }

            _smoothed[smoothing] = result;
            return result;
        }
    }

    private void EnsurePath()
    {
        lock (_gate)
        {
            if (_x is not null)
            {
                return;
            }

            var x = new double[Count];
            var y = new double[Count];
            var angle = new double[Count];
            double sx = 0;
            double sy = 0;
            double sa = 0;
            for (int index = 0; index < Count; index++)
            {
                sx += Dx[index];
                sy += Dy[index];
                sa += Da[index];
                x[index] = sx;
                y[index] = sy;
                angle[index] = sa;
            }

            (_x, _y, _angle) = (x, y, angle);
        }
    }

    private sealed record Corrections(float[] X, float[] Y, float[] Angle);
}

/// <summary>How far to move and turn one frame to steady it.</summary>
/// <param name="Shift">The move, in analysed pixels, right and down.</param>
/// <param name="Angle">The turn about the picture's centre, in radians clockwise.</param>
public readonly record struct StabilizeCorrection(Vector2 Shift, float Angle);
