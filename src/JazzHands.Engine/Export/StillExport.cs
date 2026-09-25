using System.Collections.Immutable;
using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Frames;
using JazzHands.Media.Encode;

namespace JazzHands.Engine.Export;

/// <summary>What a contact sheet shows.</summary>
/// <param name="Path">Where it was written.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="Times">The sequence time of each tile, left to right and top to bottom.</param>
public sealed record ContactSheetResult(string Path, int Width, int Height, EquatableArray<Flicks> Times);

/// <summary>
/// Pictures rather than films: one frame as a PNG, and a contact sheet of frames across a sequence.
/// </summary>
/// <remarks>
/// <para>
/// A still is an export of one frame through the PNG preset, so it is drawn exactly as a PNG
/// sequence or a delivery would draw that frame: the export renderer, the sRGB curve, subtitles
/// burned in as the preview shows them.
/// </para>
/// <para>
/// A contact sheet is the tool for looking at a whole cut at once, by a person or by Claude Code
/// reading one image instead of twenty: frames at even steps through what an export would play,
/// each the middle of its step, tiled with the sequence time under it. The frames come from the
/// still renderer, as the preview has them, and are shrunk by averaging so fine detail does not
/// shimmer into noise. The labels are drawn in a small built-in pixel font rather than through
/// DirectWrite, so a sheet is the same bytes on every machine and can be a golden.
/// </para>
/// </remarks>
public static class StillExport
{
    private const int Margin = 8;
    private const int Gap = 8;
    private const int LabelScale = 2;

    /// <summary>Writes the frame at a time as a PNG.</summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">Where it lives, or empty.</param>
    /// <param name="output">The file; relative to the project. It must end in .png.</param>
    /// <param name="at">The sequence time; the frame that holds it is drawn.</param>
    /// <param name="sequenceId">The sequence, or null for the active one.</param>
    /// <param name="size">Fit the picture inside this size, or null for the sequence's.</param>
    /// <param name="environment">Which device renders.</param>
    /// <param name="cancellationToken">Stops it.</param>
    public static ExportResult Still(
        Project project,
        string projectPath,
        string output,
        Flicks at,
        string? sequenceId = null,
        FrameSize? size = null,
        ExportEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException("unsupported-container", $"A still is written as a PNG; '{output}' is not a .png file.");
        }

        Sequence sequence = Require(project, sequenceId);
        Rational rate = project.SettingsFor(sequence).FrameRate;
        Flicks start = Flicks.FromFrames(at.ToFrames(rate, RoundingMode.Floor), rate);
        if (at < Flicks.Zero || start >= sequence.Duration)
        {
            throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(at)} is outside '{sequence.Name}', which is {Timecode.FormatClock(sequence.Duration)} long.");
        }

        ExportOverrides? overrides = size is { } fit ? new ExportOverrides(fit.Width, fit.Height) : null;
        ExportPlan plan = ExportPlanner.Plan(
            project,
            projectPath,
            new ExportRequest(output, "png-sequence", ExportMode.Encode, sequence.Id, Subtitles: SubtitleDelivery.Burn, Chapters: false, Overrides: overrides, Range: new TimeRange(start, Flicks.FromFrames(1, rate))),
            new KeyframeLookup(),
            cancellationToken);

        // One frame to the name asked for, not name_00001.png.
        return Exporter.Run(plan with { OutputPath = FullPath(output, projectPath) }, project, projectPath, environment, cancellationToken: cancellationToken);
    }

    /// <summary>Writes a contact sheet: frames at even steps through what an export would play, tiled and labelled.</summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">Where it lives, or empty.</param>
    /// <param name="output">The file; relative to the project. It must end in .png.</param>
    /// <param name="renderer">Draws the frames.</param>
    /// <param name="columns">Tiles across.</param>
    /// <param name="rows">Tiles down.</param>
    /// <param name="width">The sheet's width in pixels.</param>
    /// <param name="sequenceId">The sequence, or null for the active one.</param>
    /// <param name="range">Only this stretch, or null for all of it.</param>
    /// <param name="cancellationToken">Stops it between frames.</param>
    public static ContactSheetResult ContactSheet(
        Project project,
        string projectPath,
        string output,
        StillRenderer renderer,
        int columns = 4,
        int rows = 4,
        int width = 1920,
        string? sequenceId = null,
        TimeRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(renderer);
        if (!string.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException("unsupported-container", $"A contact sheet is written as a PNG; '{output}' is not a .png file.");
        }

        if (columns is < 1 or > 16 || rows is < 1 or > 16)
        {
            throw new CommandException("invalid-value", "A contact sheet has 1 to 16 columns and 1 to 16 rows.");
        }

        Sequence sequence = Require(project, sequenceId);
        ProjectSettings settings = project.SettingsFor(sequence);
        ImmutableArray<TimeRange> ranges = ExportPlanner.Ranges(sequence, false, range);
        if (ranges.IsEmpty)
        {
            throw new CommandException("nothing-to-export", $"'{sequence.Name}' has nothing to show there.");
        }

        int tileWidth = (width - (2 * Margin) - ((columns - 1) * Gap)) / columns;
        if (tileWidth < 32)
        {
            throw new CommandException("invalid-value", $"{width} pixels is too narrow for {columns} columns.");
        }

        int tileHeight = Math.Max(2, (int)Math.Round(tileWidth * (double)settings.Height / settings.Width));
        int label = (PixelFont.Height * LabelScale) + 6;
        int height = (2 * Margin) + (rows * (tileHeight + label)) + ((rows - 1) * Gap);

        byte[] sheet = new byte[width * height * 4];
        Fill(sheet, 0x1E, 0x1E, 0x1E);

        Flicks[] times = Times(ranges, columns * rows, settings.FrameRate);
        for (int index = 0; index < times.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StillFrame frame = renderer.Render(project, sequence, times[index], projectPath);

            int x = Margin + ((index % columns) * (tileWidth + Gap));
            int y = Margin + ((index / columns) * (tileHeight + label + Gap));
            Shrink(frame.Bgra, frame.Width, frame.Height, sheet, width, x, y, tileWidth, tileHeight);
            PixelFont.Draw(sheet, width, x, y + tileHeight + 3, Timecode.FormatClock(times[index]), LabelScale);
        }

        string path = FullPath(output, projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        PngWriter.Write(path, width, height, sheet);
        return new ContactSheetResult(path, width, height, [.. times]);
    }

    /// <summary>
    /// The sequence times of <paramref name="count"/> even steps through the stretches played back
    /// to back, each the middle of its step, on a frame.
    /// </summary>
    public static Flicks[] Times(IReadOnlyList<TimeRange> ranges, int count, Rational rate)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        Flicks total = Flicks.Zero;
        foreach (TimeRange stretch in ranges)
        {
            total += stretch.Duration;
        }

        var times = new Flicks[count];
        for (int index = 0; index < count; index++)
        {
            Flicks into = new((long)(total.Value * ((index + 0.5) / count)));
            foreach (TimeRange stretch in ranges)
            {
                if (into < stretch.Duration)
                {
                    Flicks time = stretch.Start + into;
                    times[index] = Flicks.FromFrames(time.ToFrames(rate, RoundingMode.Floor), rate);
                    break;
                }

                into -= stretch.Duration;
            }
        }

        return times;
    }

    private static Sequence Require(Project project, string? sequenceId) =>
        (sequenceId is { } id ? project.Sequence(id) : project.ActiveSequence)
        ?? throw new CommandException("sequence-not-found", sequenceId is null ? "The project has no sequence." : $"No sequence with id '{sequenceId}'.");

    private static string FullPath(string output, string projectPath) =>
        Path.GetFullPath(!Path.IsPathRooted(output) && projectPath.Length > 0
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".", output)
            : output);

    private static void Fill(byte[] bgra, byte red, byte green, byte blue)
    {
        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = blue;
            bgra[index + 1] = green;
            bgra[index + 2] = red;
            bgra[index + 3] = 255;
        }
    }

    /// <summary>Shrinks a picture into a box of the sheet by averaging the source pixels each target pixel covers.</summary>
    private static void Shrink(byte[] source, int sourceWidth, int sourceHeight, byte[] sheet, int sheetWidth, int left, int top, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int y0 = y * sourceHeight / height;
            int y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * sourceWidth / width;
                int x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / width);
                int blue = 0, green = 0, red = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * sourceWidth * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int at = row + (sx * 4);
                        blue += source[at];
                        green += source[at + 1];
                        red += source[at + 2];
                        count++;
                    }
                }

                int target = (((top + y) * sheetWidth) + left + x) * 4;
                sheet[target] = (byte)(blue / count);
                sheet[target + 1] = (byte)(green / count);
                sheet[target + 2] = (byte)(red / count);
                sheet[target + 3] = 255;
            }
        }
    }

    /// <summary>A five by seven pixel font for timecodes: digits, a colon, a point and a minus.</summary>
    internal static class PixelFont
    {
        /// <summary>Rows in a glyph.</summary>
        public const int Height = 7;

        private const int Width = 5;

        // Each glyph is seven rows of five bits, the leftmost pixel the highest bit.
        private static readonly Dictionary<char, byte[]> Glyphs = new()
        {
            ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
            ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['2'] = [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
            ['3'] = [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
            ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
            [':'] = [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
            ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        };

        /// <summary>Draws text in light grey at a place, each font pixel a <paramref name="scale"/> square; unknown characters are gaps.</summary>
        public static void Draw(byte[] bgra, int stride, int left, int top, string text, int scale)
        {
            int rows = bgra.Length / 4 / stride;
            for (int index = 0; index < text.Length; index++)
            {
                if (!Glyphs.TryGetValue(text[index], out byte[]? glyph))
                {
                    continue;
                }

                int origin = left + (index * (Width + 1) * scale);
                for (int row = 0; row < Height; row++)
                {
                    for (int column = 0; column < Width; column++)
                    {
                        if ((glyph[row] & (1 << (Width - 1 - column))) == 0)
                        {
                            continue;
                        }

                        for (int dy = 0; dy < scale; dy++)
                        {
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int x = origin + (column * scale) + dx;
                                int y = top + (row * scale) + dy;
                                if (x < 0 || x >= stride || y < 0 || y >= rows)
                                {
                                    continue;
                                }

                                int at = ((y * stride) + x) * 4;
                                bgra[at] = 0xD8;
                                bgra[at + 1] = 0xD8;
                                bgra[at + 2] = 0xD8;
                                bgra[at + 3] = 255;
                            }
                        }
                    }
                }
            }
        }
    }
}
