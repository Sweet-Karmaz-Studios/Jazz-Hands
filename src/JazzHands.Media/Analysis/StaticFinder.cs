using JazzHands.Core.Time;

namespace JazzHands.Media.Analysis;

/// <summary>A part of a picture that barely changes across a clip: a HUD, a watermark, a label.</summary>
/// <param name="X">Its left edge, in source pixels.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
/// <param name="Score">How much of it is still detail, 0 to 1.</param>
public sealed record StaticRegion(int X, int Y, int Width, int Height, double Score);

/// <summary>
/// Finds what stays put while the rest of a picture moves: the overlays a game draws over its
/// world (health, ammo, a minimap frame, a debug line, an FPS counter), a channel's watermark, a
/// burned in label.
/// </summary>
/// <remarks>
/// <para>
/// Frames spread across the clip are read in luma at a quarter size or so. A cell is a candidate
/// when it barely changes across them (its range under <see cref="StillRange"/>) and has detail
/// in it (edges averaging over <see cref="EdgeStrength"/>): text and icons, not a flat sky or a
/// black bar, which are also still but are not what anyone wants hidden. A frame counter, a clock
/// or a changing number moves, so it is not found; nor is the moving picture.
/// </para>
/// <para>
/// Candidates are grown by a few cells so a word's letters join, grouped, and each group's box
/// becomes a region, padded a little. Dropped: tiny groups (a stray still pixel); groups over a
/// fifth of the frame (a paused scene); long thin ones, over <see cref="MaxAspect"/> to one (a
/// still edge in the picture, a pillar or a horizon) or thinner than <see cref="MinThickness"/> of the frame
/// thick (a line); and sparse ones, under
/// <see cref="MinDensity"/> still detail (the outline of a box round a changing number). Text and
/// icons are dense and compact.
/// </para>
/// </remarks>
public static class StaticFinder
{
    /// <summary>The most a cell may change across the frames, out of 255, to count as still.</summary>
    public const float StillRange = 10;

    /// <summary>The least edge strength, out of 255, a still cell needs to count as detail.</summary>
    public const float EdgeStrength = 14;

    /// <summary>The longest a group may be for its width, either way, before it is taken for an edge in the picture.</summary>
    public const double MaxAspect = 12;

    /// <summary>The least of a group's box that has to be still detail.</summary>
    public const double MinDensity = 0.4;

    /// <summary>The thinnest a group may be, as a fraction of the frame's height, before it is taken for a line: an edge in the picture, not text. 16 pixels at 1080.</summary>
    public const double MinThickness = 0.015;

    /// <summary>The widest the analysis works at.</summary>
    public const int AnalysisWidth = 480;

    /// <summary>The still regions of a file between two source times.</summary>
    public static IReadOnlyList<StaticRegion> Find(string path, Flicks from, Flicks length, int samples = 24, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = new LumaReader(path);
        var frames = new List<LumaImage>(samples);
        for (int index = 0; index < samples; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flicks at = from + new Flicks((long)((double)length.Value * index / Math.Max(1, samples - 1) * 0.999));
            if (reader.Read(at) is { } image)
            {
                frames.Add(image);
            }
        }

        return Find(frames);
    }

    /// <summary>The still regions of a set of frames of one size.</summary>
    public static IReadOnlyList<StaticRegion> Find(IReadOnlyList<LumaImage> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
        {
            return [];
        }

        int sourceWidth = frames[0].Width;
        int sourceHeight = frames[0].Height;
        int step = Math.Max(1, (int)Math.Ceiling(sourceWidth / (double)AnalysisWidth));
        int width = sourceWidth / step;
        int height = sourceHeight / step;
        int cells = width * height;

        var low = new float[cells];
        var high = new float[cells];
        var edges = new float[cells];
        Array.Fill(low, float.MaxValue);
        Array.Fill(high, float.MinValue);
        var cell = new float[cells];
        foreach (LumaImage frame in frames)
        {
            Shrink(frame, step, width, height, cell);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width) + x;
                    float value = cell[at];
                    low[at] = MathF.Min(low[at], value);
                    high[at] = MathF.Max(high[at], value);
                    float across = x + 1 < width ? MathF.Abs(cell[at + 1] - value) : 0;
                    float down = y + 1 < height ? MathF.Abs(cell[at + width] - value) : 0;
                    edges[at] += across + down;
                }
            }
        }

        var candidate = new bool[cells];
        for (int at = 0; at < cells; at++)
        {
            candidate[at] = high[at] - low[at] <= StillRange && edges[at] / frames.Count >= EdgeStrength;
        }

        bool[] grown = Grow(candidate, width, height, radius: Math.Max(2, 16 / step));
        return Groups(grown, candidate, width, height, step, sourceWidth, sourceHeight);
    }

    /// <summary>A frame box-averaged down by a whole step.</summary>
    private static void Shrink(LumaImage frame, int step, int width, int height, float[] into)
    {
        float area = step * step;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int row = ((y * step) + dy) * frame.Width;
                    for (int dx = 0; dx < step; dx++)
                    {
                        sum += frame.Pixels[row + (x * step) + dx];
                    }
                }

                into[(y * width) + x] = sum / area;
            }
        }
    }

    /// <summary>Every cell within a square of a radius of a set one.</summary>
    private static bool[] Grow(bool[] set, int width, int height, int radius)
    {
        // Across, then down: a square in two passes.
        var across = new bool[set.Length];
        for (int y = 0; y < height; y++)
        {
            int last = int.MinValue / 2;
            for (int x = 0; x < width; x++)
            {
                if (set[(y * width) + x])
                {
                    last = x;
                }

                across[(y * width) + x] = x - last <= radius;
            }

            last = int.MaxValue / 2;
            for (int x = width - 1; x >= 0; x--)
            {
                if (set[(y * width) + x])
                {
                    last = x;
                }

                across[(y * width) + x] |= last - x <= radius;
            }
        }

        var grown = new bool[set.Length];
        for (int x = 0; x < width; x++)
        {
            int last = int.MinValue / 2;
            for (int y = 0; y < height; y++)
            {
                if (across[(y * width) + x])
                {
                    last = y;
                }

                grown[(y * width) + x] = y - last <= radius;
            }

            last = int.MaxValue / 2;
            for (int y = height - 1; y >= 0; y--)
            {
                if (across[(y * width) + x])
                {
                    last = y;
                }

                grown[(y * width) + x] |= last - y <= radius;
            }
        }

        return grown;
    }

    /// <summary>The joined groups as boxes in source pixels, trimmed to their candidates and padded.</summary>
    private static List<StaticRegion> Groups(bool[] grown, bool[] candidate, int width, int height, int step, int sourceWidth, int sourceHeight)
    {
        var label = new int[grown.Length];
        var regions = new List<StaticRegion>();
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < grown.Length; start++)
        {
            if (!grown[start] || label[start] != 0)
            {
                continue;
            }

            next++;
            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;
            int count = 0;
            int area = 0;
            stack.Push(start);
            label[start] = next;
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                area++;
                int x = at % width;
                int y = at / width;
                if (candidate[at])
                {
                    count++;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }

                foreach (int near in (ReadOnlySpan<int>)[x > 0 ? at - 1 : -1, x + 1 < width ? at + 1 : -1, y > 0 ? at - width : -1, y + 1 < height ? at + width : -1])
                {
                    if (near >= 0 && grown[near] && label[near] == 0)
                    {
                        label[near] = next;
                        stack.Push(near);
                    }
                }
            }

            int boxWidth = right - left + 1;
            int boxHeight = bottom - top + 1;
            double density = (double)count / (boxWidth * boxHeight);
            double aspect = Math.Max((double)boxWidth / boxHeight, (double)boxHeight / boxWidth);
            if (count < 12 || boxWidth * boxHeight > width * height / 5 || aspect > MaxAspect || density < MinDensity || Math.Min(boxWidth, boxHeight) * step < MinThickness * sourceHeight)
            {
                continue;
            }

            // Padded by a cell and a half each way, which covers a glyph's antialiased edge.
            int pad = (int)Math.Ceiling(step * 1.5);
            int x0 = Math.Max(0, (left * step) - pad);
            int y0 = Math.Max(0, (top * step) - pad);
            int x1 = Math.Min(sourceWidth, ((right + 1) * step) + pad);
            int y1 = Math.Min(sourceHeight, ((bottom + 1) * step) + pad);
            regions.Add(new StaticRegion(x0, y0, x1 - x0, y1 - y0, Math.Round(density, 3)));
        }

        return [.. regions.OrderBy(region => region.Y).ThenBy(region => region.X)];
    }
}
