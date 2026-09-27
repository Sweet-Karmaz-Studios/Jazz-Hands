using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Speech;

/// <summary>One word heard in a recording (Phase 39).</summary>
/// <param name="Text">The word as written, punctuation kept ("devlog.").</param>
/// <param name="Start">When it begins, in the file's own time.</param>
/// <param name="End">When it ends, in the file's own time.</param>
/// <param name="Confidence">How sure the model was, 0 to 1.</param>
public sealed record TranscribedWord(string Text, Flicks Start, Flicks End, float Confidence) : IEquatable<TranscribedWord>
{
    /// <summary>The filler words a clean-up takes out unless told otherwise.</summary>
    public static IReadOnlyList<string> DefaultFillers { get; } = ["um", "uh", "er", "erm", "ah", "hmm", "mm"];

    /// <summary>The word without punctuation, in lower case, for matching.</summary>
    public string Bare => new([.. Text.Where(character => char.IsLetterOrDigit(character) || character == '\'').Select(char.ToLowerInvariant)]);

    /// <summary>True when the word ends a sentence.</summary>
    public bool EndsSentence => Text.TrimEnd().EndsWith('.') || Text.TrimEnd().EndsWith('?') || Text.TrimEnd().EndsWith('!');

    /// <summary>How long it lasts.</summary>
    public Flicks Duration => End - Start;
}

/// <summary>
/// What was said in one sound stream of a media file (Phase 39): every word with its time in the
/// file, in order. Kept beside the project, not in it (a ten minute talk is thousands of words),
/// and keyed to the file by its hash, so it survives a relink and is shared by every clip of it.
/// </summary>
/// <param name="MediaHash">The file's content hash.</param>
/// <param name="Stream">The sound stream's index.</param>
/// <param name="Model">The model that heard it.</param>
/// <param name="Language">The language it was heard as (ISO 639-1).</param>
/// <param name="Words">The words, in order.</param>
public sealed record Transcript(string MediaHash, int Stream, string Model, string Language, EquatableArray<TranscribedWord> Words) : IEquatable<Transcript>
{
    /// <summary>The words, joined as a reader would read them.</summary>
    public string Text => string.Join(' ', Words.Select(word => word.Text.Trim()));

    /// <summary>
    /// The words as sentences: a sentence ends at a full stop, question or exclamation mark, or at a
    /// pause longer than <paramref name="pause"/>.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TranscribedWord>> Sentences(Flicks pause)
    {
        var sentences = new List<IReadOnlyList<TranscribedWord>>();
        var current = new List<TranscribedWord>();
        for (int index = 0; index < Words.Length; index++)
        {
            TranscribedWord word = Words[index];
            current.Add(word);
            bool gap = index + 1 < Words.Length && Words[index + 1].Start - word.End > pause;
            if (word.EndsSentence || gap || index + 1 == Words.Length)
            {
                sentences.Add(current);
                current = [];
            }
        }

        return sentences;
    }
}
