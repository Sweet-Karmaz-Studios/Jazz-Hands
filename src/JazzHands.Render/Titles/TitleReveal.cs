using System.Globalization;

namespace JazzHands.Render.Titles;

/// <summary>
/// How far each letter, word or line of a title shows part way through a reveal.
/// </summary>
/// <remarks>
/// The reveal runs over the units in order. With no softness it is a count: at 0.5 of ten letters,
/// five show. With softness s, each unit fades in over s units' worth of the reveal, starting as
/// the one s before it finishes, so words overlap the way a word reveal's stagger does. At 0 none
/// shows and at 1 all do, whatever the softness.
/// </remarks>
public static class TitleReveal
{
    /// <summary>How much of unit <paramref name="index"/> of <paramref name="count"/> shows.</summary>
    public static float Alpha(int index, int count, float reveal, float soft)
    {
        if (count <= 0 || reveal >= 1.0f)
        {
            return 1.0f;
        }

        if (reveal <= 0.0f)
        {
            return 0.0f;
        }

        if (soft <= 1e-4f)
        {
            int shown = (int)MathF.Floor((reveal * count) + 1e-4f);
            return index < shown ? 1.0f : 0.0f;
        }

        float at = reveal * (count + soft);
        return Math.Clamp((at - index) / soft, 0.0f, 1.0f);
    }

    /// <summary>
    /// The stretches a reveal counts: every letter that is not a space (a letter being what a
    /// reader sees as one, accents and all), or every word.
    /// </summary>
    public static (int Start, int Length)[] Units(string text, bool words)
    {
        ArgumentNullException.ThrowIfNull(text);
        var units = new List<(int Start, int Length)>();

        if (words)
        {
            int start = -1;
            for (int index = 0; index <= text.Length; index++)
            {
                bool space = index == text.Length || char.IsWhiteSpace(text[index]);
                if (!space && start < 0)
                {
                    start = index;
                }
                else if (space && start >= 0)
                {
                    units.Add((start, index - start));
                    start = -1;
                }
            }

            return [.. units];
        }

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element))
            {
                units.Add((elements.ElementIndex, element.Length));
            }
        }

        return [.. units];
    }

    /// <summary>The opacity of every UTF-16 position of a text for a reveal over its units; 1 between units.</summary>
    public static float[] Alphas((int Start, int Length)[] units, int length, float reveal, float soft)
    {
        ArgumentNullException.ThrowIfNull(units);
        float[] alphas = new float[length];
        Array.Fill(alphas, 1.0f);
        for (int unit = 0; unit < units.Length; unit++)
        {
            float alpha = Alpha(unit, units.Length, reveal, soft);
            (int start, int count) = units[unit];
            for (int index = start; index < Math.Min(length, start + count); index++)
            {
                alphas[index] = alpha;
            }
        }

        return alphas;
    }
}
