namespace JazzHands.Core.Commands;

/// <summary>
/// About how many tokens a language model spends on some text, estimated on the high side.
/// </summary>
/// <remarks>
/// Models split text into word pieces: a common word is one token, a long one two or three, digits
/// come in runs of up to three, and punctuation is a token of its own. Timecode and ids, which a
/// description is full of, are dear. This counts each run of letters as one token per four
/// letters or part of four, each run of digits as one per three, every other visible character as
/// one, and a line break as one. It has not been checked against a real tokenizer; it is meant to
/// err high, so a budget met here is met there, and to be the same number on every machine.
/// </remarks>
public static class TokenEstimate
{
    /// <summary>The estimate for a text.</summary>
    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int tokens = 0;
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (char.IsLetter(c))
            {
                int start = index;
                while (index < text.Length && char.IsLetter(text[index]))
                {
                    index++;
                }

                tokens += (index - start + 3) / 4;
            }
            else if (char.IsDigit(c))
            {
                int start = index;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }

                tokens += (index - start + 2) / 3;
            }
            else
            {
                if (c == '\n' || !char.IsWhiteSpace(c))
                {
                    tokens++;
                }

                index++;
            }
        }

        return tokens;
    }
}
