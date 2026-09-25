using System.Collections.Concurrent;
using JazzHands.Media.SmartCut;

namespace JazzHands.Engine.Export;

/// <summary>
/// Whether a file can be smart cut, remembered: what its picture is, which matched encoder writes
/// parameter sets that describe the same picture, and whether its sound can be cut to the sample.
/// </summary>
/// <remarks>
/// Checking an encoder means opening it (an NVENC session, briefly) and reading what it would
/// write, and the export dialog plans again on every change. The answer depends only on the file,
/// so it is kept by path, stream and the file's time and size.
/// </remarks>
internal static class SmartCutChecks
{
    private static readonly ConcurrentDictionary<string, Answer> Answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source's description and the encoders to use, or why it cannot be smart cut.</summary>
    /// <param name="path">The file.</param>
    /// <param name="stream">Its picture stream.</param>
    /// <param name="audioStreams">The sound streams the cut carries.</param>
    /// <param name="gopFrames">Frames between its keyframes.</param>
    /// <param name="encoders">The encoders asked for, in order, or empty for the codec's own chain, NVENC first.</param>
    public static Answer For(string path, int stream, IReadOnlyList<int> audioStreams, int gopFrames, IReadOnlyList<string> encoders)
    {
        var file = new FileInfo(path);
        string key = $"{path}|{stream}|{string.Join(',', audioStreams)}|{gopFrames}|{string.Join(',', encoders)}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        return Answers.GetOrAdd(key, _ => Check(path, stream, audioStreams, gopFrames, encoders));
    }

    private static Answer Check(string path, int stream, IReadOnlyList<int> audioStreams, int gopFrames, IReadOnlyList<string> asked)
    {
        (MatchSource? source, string? reason) = SmartCutter.Describe(path, stream, gopFrames);
        if (source is null)
        {
            return new Answer(null, [], reason);
        }

        foreach (int audio in audioStreams)
        {
            if (SmartCutter.SoundProblem(path, audio) is { } problem)
            {
                return new Answer(null, [], problem);
            }
        }

        // Asked for encoders narrow the codec's chain, in the order asked; one that cannot write the
        // source's codec is not a choice.
        IReadOnlyList<string> own = MatchedEncoder.ChainFor(source.Codec);
        IReadOnlyList<string> chain = asked.Count == 0 ? own : [.. asked.Where(name => own.Contains(name, StringComparer.Ordinal))];
        if (chain.Count == 0)
        {
            return new Answer(null, [], $"None of {string.Join(", ", asked)} can match a {source.Codec} source; {string.Join(" or ", own)} can.");
        }

        (string? encoder, string? why) = MatchedEncoder.Check(source, chain);
        if (encoder is null)
        {
            return new Answer(null, [], $"No encoder writes a picture this source's decoder would take: {why}");
        }

        // The one that matched first, then the rest of the chain after it as fallbacks.
        string[] encoders = [.. chain.SkipWhile(name => name != encoder)];
        return new Answer(source, encoders, null, why is null ? null : $"Passed over for the smart cut: {why}.");
    }

    /// <summary>What the check found.</summary>
    /// <param name="Source">The source's description, or null when it cannot be smart cut.</param>
    /// <param name="Encoders">The matched encoders to try, in order.</param>
    /// <param name="Reason">Why not, in a sentence.</param>
    /// <param name="Skipped">When it can, the encoders ahead of the one chosen that did not match, and why.</param>
    public sealed record Answer(MatchSource? Source, IReadOnlyList<string> Encoders, string? Reason, string? Skipped = null);
}
