using System.Globalization;
using System.Numerics;

namespace JazzHands.Render.Color;

/// <summary>
/// A 3D LUT read from an Adobe/Resolve <c>.cube</c> file: its size, its input domain and its
/// entries, red changing fastest.
/// </summary>
/// <remarks>
/// The format is text: optional <c>TITLE</c>, <c>LUT_3D_SIZE n</c>, optional <c>DOMAIN_MIN</c> and
/// <c>DOMAIN_MAX</c>, then n³ lines of three numbers. Comments start with #. A 1D LUT
/// (<c>LUT_1D_SIZE</c>) is refused with a sentence rather than misread; sizes from 2 to 129 are
/// taken, which covers the 17, 33 and 65 every tool writes.
/// </remarks>
/// <param name="Size">Entries along each axis.</param>
/// <param name="DomainMin">The input that maps to the first entry on each axis.</param>
/// <param name="DomainMax">The input that maps to the last.</param>
/// <param name="Entries">Size³ outputs, red fastest, then green, then blue.</param>
public sealed record CubeLut(int Size, Vector3 DomainMin, Vector3 DomainMax, Vector3[] Entries)
{
    /// <summary>The identity of a given size: every entry its own coordinate.</summary>
    public static CubeLut Identity(int size)
    {
        var entries = new Vector3[size * size * size];
        float step = 1.0f / (size - 1);
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    entries[(((b * size) + g) * size) + r] = new Vector3(r * step, g * step, b * step);
                }
            }
        }

        return new CubeLut(size, Vector3.Zero, Vector3.One, entries);
    }

    /// <summary>Reads a .cube file.</summary>
    /// <exception cref="FormatException">The file is not a 3D .cube LUT this reader can take; the message says why.</exception>
    public static CubeLut Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int size = 0;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.One;
        var entries = new List<Vector3>();
        int lineNumber = 0;

        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            switch (words[0].ToUpperInvariant())
            {
                case "TITLE":
                    continue;
                case "LUT_1D_SIZE":
                    throw new FormatException("This is a 1D LUT; only 3D LUTs (LUT_3D_SIZE) are supported.");
                case "LUT_3D_SIZE":
                    size = words.Length == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 2 and <= 129
                        ? parsed
                        : throw new FormatException($"Line {lineNumber}: LUT_3D_SIZE must be a whole number from 2 to 129.");
                    continue;
                case "DOMAIN_MIN":
                    min = Triple(words, 1, lineNumber);
                    continue;
                case "DOMAIN_MAX":
                    max = Triple(words, 1, lineNumber);
                    continue;
                case "LUT_3D_INPUT_RANGE":
                    // Resolve's older spelling of the domain: one low and one high for all channels.
                    if (words.Length != 3
                        || !float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float low)
                        || !float.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float high))
                    {
                        throw new FormatException($"Line {lineNumber}: LUT_3D_INPUT_RANGE takes two numbers.");
                    }

                    min = new Vector3(low);
                    max = new Vector3(high);
                    continue;
            }

            if (char.IsLetter(words[0][0]))
            {
                // A keyword this reader does not know, from a newer or vendor dialect; skip it.
                continue;
            }

            entries.Add(Triple(words, 0, lineNumber));
        }

        if (size == 0)
        {
            throw new FormatException("There is no LUT_3D_SIZE line, so this is not a 3D .cube LUT.");
        }

        if (entries.Count != size * size * size)
        {
            throw new FormatException($"A {size}-point cube has {size * size * size} entries; this file has {entries.Count}.");
        }

        if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
        {
            throw new FormatException("DOMAIN_MIN has to be below DOMAIN_MAX on every channel.");
        }

        return new CubeLut(size, min, max, [.. entries]);
    }

    /// <summary>Reads a .cube file from disk.</summary>
    public static CubeLut Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    private static Vector3 Triple(string[] words, int start, int lineNumber)
    {
        if (words.Length < start + 3)
        {
            throw new FormatException($"Line {lineNumber}: three numbers were expected.");
        }

        float Number(int index) =>
            float.TryParse(words[start + index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
                ? value
                : throw new FormatException($"Line {lineNumber}: '{words[start + index]}' is not a number.");

        return new Vector3(Number(0), Number(1), Number(2));
    }
}
