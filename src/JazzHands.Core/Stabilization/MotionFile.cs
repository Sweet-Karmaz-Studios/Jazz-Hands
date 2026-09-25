using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace JazzHands.Core.Stabilization;

/// <summary>
/// Reads the transforms file vid.stab's detect pass writes in its text form
/// (<c>vidstabdetect=fileformat=ascii</c>) and turns its local motions into the camera's move
/// at each frame.
/// </summary>
/// <remarks>
/// <para>
/// The file has a line per frame, <c>Frame N (List M [(LM vx vy fx fy size contrast match),...])</c>,
/// one local motion per measuring field: the field at <c>fx, fy</c> in the frame before was found
/// <c>vx, vy</c> away, which is how far the camera moved there. Frame 1 has none.
/// </para>
/// <para>
/// The camera's move is the median of the fields' moves, which ignores something moving through
/// the shot where a mean would follow it. Its turn is the median of how far each field far enough
/// from the centre to show one turned about the centre once that move is taken away. Fields vid.stab
/// matched badly (<c>match</c> above <see cref="WorstMatch"/>) are left out when enough others remain.
/// </para>
/// </remarks>
public static partial class MotionFile
{
    /// <summary>The worst match (vid.stab's error, lower is better) a field may have and still count.</summary>
    public const double WorstMatch = 0.5;

    /// <summary>Reads a transforms file into the camera's motion.</summary>
    /// <param name="reader">The file's text.</param>
    /// <param name="width">The analysed picture's width.</param>
    /// <param name="height">The analysed picture's height.</param>
    /// <exception cref="FormatException">It is not a vid.stab text transforms file.</exception>
    public static CameraMotion Read(TextReader reader, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? header = reader.ReadLine();
        if (header is null || !header.StartsWith("VID.STAB", StringComparison.Ordinal))
        {
            throw new FormatException("Not a vid.stab transforms file: it does not start VID.STAB. Run the detect pass with fileformat=ascii.");
        }

        var frames = new SortedDictionary<int, List<LocalMotion>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            Match frame = FramePattern().Match(line);
            if (!frame.Success)
            {
                continue;
            }

            var motions = new List<LocalMotion>();
            foreach (Match field in FieldPattern().Matches(line))
            {
                motions.Add(new LocalMotion(
                    new Vector2(Number(field, 1), Number(field, 2)),
                    new Vector2(Number(field, 3), Number(field, 4)),
                    Number(field, 7)));
            }

            frames[int.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture)] = motions;
        }

        int count = frames.Count == 0 ? 0 : frames.Keys.Max();
        float[] dx = new float[count];
        float[] dy = new float[count];
        float[] da = new float[count];
        var centre = new Vector2(width / 2.0f, height / 2.0f);

        foreach ((int number, List<LocalMotion> motions) in frames)
        {
            if (number < 1 || number > count || motions.Count == 0)
            {
                continue;
            }

            (Vector2 move, float turn) = Global(motions, centre, Math.Min(width, height));
            dx[number - 1] = move.X;
            dy[number - 1] = move.Y;
            da[number - 1] = turn;
        }

        return new CameraMotion(width, height, dx, dy, da);
    }

    /// <summary>The camera's move and turn at one frame from its fields.</summary>
    internal static (Vector2 Move, float Turn) Global(IReadOnlyList<LocalMotion> motions, Vector2 centre, float size)
    {
        List<LocalMotion> good = [.. motions.Where(motion => motion.Match <= WorstMatch)];
        if (good.Count < Math.Max(3, motions.Count / 4))
        {
            good = [.. motions];
        }

        var move = new Vector2(Median(good.Select(motion => motion.Move.X)), Median(good.Select(motion => motion.Move.Y)));

        // The picture moves the other way to the camera, so a field at f is now at f - v; with the
        // common move taken out what is left is the turn about the centre.
        var turns = new List<float>();
        foreach (LocalMotion motion in good)
        {
            Vector2 before = motion.Field - centre;
            if (before.Length() < size / 6.0f)
            {
                continue;
            }

            Vector2 after = before - (motion.Move - move);
            float turned = MathF.Atan2(after.Y, after.X) - MathF.Atan2(before.Y, before.X);
            turned = MathF.IEEERemainder(turned, 2.0f * MathF.PI);
            turns.Add(-turned);
        }

        return (move, turns.Count < 3 ? 0.0f : Median(turns));
    }

    private static float Median(IEnumerable<float> values)
    {
        float[] sorted = [.. values.Order()];
        if (sorted.Length == 0)
        {
            return 0.0f;
        }

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0f;
    }

    private static float Number(Match match, int group) =>
        float.Parse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^Frame\s+(\d+)\s")]
    private static partial Regex FramePattern();

    [GeneratedRegex(@"\(LM\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.eE+-]+)\s+(-?[\d.eE+-]+)\)")]
    private static partial Regex FieldPattern();

    /// <summary>One measuring field's finding.</summary>
    /// <param name="Move">How far the camera moved there.</param>
    /// <param name="Field">The field's centre in the frame before.</param>
    /// <param name="Match">vid.stab's matching error; lower is better.</param>
    internal readonly record struct LocalMotion(Vector2 Move, Vector2 Field, double Match);
}
