using JazzHands.Core.Speech;
using JazzHands.Core.Time;
using Serilog;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace JazzHands.Speech;

/// <summary>How a transcription runs.</summary>
/// <param name="ModelPath">The whisper.cpp model file (ggml).</param>
/// <param name="Language">The language, ISO 639-1; null to detect it.</param>
/// <param name="Gpu">Run on the GPU (Vulkan) when there is one; false for the CPU.</param>
/// <param name="Dtw">Time words by the model's cross-attention (DTW), which follows speech more closely than token timestamps.</param>
public sealed record TranscriberOptions(string ModelPath, string? Language = "en", bool Gpu = true, bool Dtw = true);

/// <summary>
/// Speech to text on this machine (Phase 39): whisper.cpp through Whisper.net, 16 kHz mono in,
/// words with their times out. The GPU is reached through Vulkan, which the graphics driver
/// provides; whisper.cpp's CUDA build needs NVIDIA's cuBLAS, which is not here (Docs/SPIKES.md S9).
/// </summary>
/// <remarks>
/// <para>
/// The audio is cut at its quietest moments into pieces of at most 28 seconds, each heard on its
/// own: whisper.cpp's DTW timing stops after the first 30 second window of a longer input, and a
/// piece a window long never has a second one.
/// </para>
/// <para>
/// Whisper writes text in tokens, a word being one or more of them, the first starting with a
/// space; the words are put back together from them. With DTW, a token's time marks where it ends
/// rather than where it begins (measured against the corpus: a word's DTW time falls near its end),
/// so a word ends at its last token's time and begins where the word before it ended, or, after a
/// pause, where the sound rises out of the silence. Without DTW, the tokens' own timestamps are
/// used, which are coarser. Special tokens (<c>[_BEG_]</c>, <c>&lt;|endoftext|&gt;</c>) are left out.
/// </para>
/// </remarks>
public static class Transcriber
{
    /// <summary>The rate whisper hears at.</summary>
    public const int SampleRate = 16000;

    /// <summary>How far after the word before a following word begins, in seconds: its DTW time's lead.</summary>
    private const double FollowOn = 0.04;

    /// <summary>The longest piece whisper hears at once, a little under its 30 second window.</summary>
    private const double PieceSeconds = 28.0;

    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(Transcriber));
    private static readonly Lock Gate = new();
    private static bool _ordered;

    /// <summary>Which runtime whisper.cpp loaded: Vulkan, or the CPU; null before the first transcription.</summary>
    public static string? Runtime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>The words in 16 kHz mono samples, in order, with times from the first sample.</summary>
    public static async Task<IReadOnlyList<TranscribedWord>> TranscribeAsync(
        float[] samples,
        TranscriberOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException($"There is no speech model at {options.ModelPath}.", options.ModelPath);
        }

        lock (Gate)
        {
            if (!_ordered)
            {
                // Vulkan first (the GPU through the graphics driver), the CPU after; never CUDA,
                // whose build here would need NVIDIA's cuBLAS installed.
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                _ordered = true;
            }
        }

        var factoryOptions = new WhisperFactoryOptions
        {
            UseGpu = options.Gpu,
            UseDtwTimeStamps = options.Dtw,
            HeadsPreset = options.Dtw ? HeadsFor(options.ModelPath) : WhisperAlignmentHeadsPreset.None,
        };

        using WhisperFactory factory = WhisperFactory.FromPath(options.ModelPath, factoryOptions);
        var tokens = new List<HeardToken>();
        IReadOnlyList<(int Start, int Length)> pieces = Pieces(samples);
        string? language = options.Language is { Length: > 0 } given ? given : null;
        foreach ((int start, int length) in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WhisperProcessorBuilder builder = factory.CreateBuilder()
                .WithTokenTimestamps()
                .WithProbabilities()
                .WithProgressHandler(percent => progress?.Report((start + (length * percent / 100.0)) / samples.Length));
            builder = language is not null ? builder.WithLanguage(language) : builder.WithLanguageDetection();

            await using WhisperProcessor processor = builder.Build();
            double offset = start / (double)SampleRate;
            await foreach (SegmentData segment in processor.ProcessAsync(samples.AsMemory(start, length), cancellationToken).ConfigureAwait(false))
            {
                language ??= segment.Language;
                foreach (WhisperToken token in segment.Tokens)
                {
                    if (!IsSpecial(token.Text))
                    {
                        tokens.Add(new HeardToken(
                            token.Text ?? string.Empty,
                            token.Probability,
                            offset + (token.Start / 100.0),
                            offset + (token.End / 100.0),
                            token.DtwTimestamp >= 0 ? offset + (token.DtwTimestamp / 100.0) : null));
                    }
                }
            }
        }

        progress?.Report(1.0);
        Log.Information("Transcribed {Seconds:F1} s in {Pieces} pieces on {Runtime} into {Tokens} tokens", samples.Length / (double)SampleRate, pieces.Count, Runtime, tokens.Count);
        return Words(tokens, options.Dtw ? Loudness(samples) : null);
    }

    /// <summary>
    /// The audio cut into pieces of at most <see cref="PieceSeconds"/>, each cut at the quietest
    /// fifth of a second in its last eight seconds, so no word is split.
    /// </summary>
    internal static IReadOnlyList<(int Start, int Length)> Pieces(float[] samples)
    {
        int most = (int)(PieceSeconds * SampleRate);
        int window = SampleRate / 5;
        var pieces = new List<(int Start, int Length)>();
        int at = 0;
        while (samples.Length - at > most)
        {
            int cut = at + most;
            double quietest = double.MaxValue;
            for (int candidate = at + most - (8 * SampleRate); candidate + window <= at + most; candidate += window / 4)
            {
                double energy = 0;
                for (int sample = candidate; sample < candidate + window; sample++)
                {
                    energy += samples[sample] * samples[sample];
                }

                if (energy < quietest)
                {
                    quietest = energy;
                    cut = candidate + (window / 2);
                }
            }

            pieces.Add((at, cut - at));
            at = cut;
        }

        pieces.Add((at, samples.Length - at));
        return pieces;
    }

    /// <summary>The loudness, RMS in 10 ms frames, for finding where speech starts after a pause.</summary>
    internal static float[] Loudness(float[] samples)
    {
        int frame = SampleRate / 100;
        var loudness = new float[samples.Length / frame];
        for (int index = 0; index < loudness.Length; index++)
        {
            double sum = 0;
            for (int sample = index * frame; sample < (index + 1) * frame; sample++)
            {
                sum += samples[sample] * samples[sample];
            }

            loudness[index] = (float)Math.Sqrt(sum / frame);
        }

        return loudness;
    }

    /// <summary>Tokens put back together as words; with <paramref name="loudness"/>, timed by DTW as the remarks say.</summary>
    internal static IReadOnlyList<TranscribedWord> Words(IReadOnlyList<HeardToken> tokens, float[]? loudness)
    {
        var groups = new List<List<HeardToken>>();
        foreach (HeardToken token in tokens)
        {
            if (groups.Count == 0 || token.Text.StartsWith(' '))
            {
                groups.Add([]);
            }

            groups[^1].Add(token);
        }

        groups.RemoveAll(group => string.Concat(group.Select(token => token.Text)).Trim().Length == 0);

        float silence = loudness is null ? 0 : Silence(loudness);
        var words = new List<TranscribedWord>(groups.Count);
        double previousEnd = 0;
        for (int index = 0; index < groups.Count; index++)
        {
            List<HeardToken> group = groups[index];
            string text = string.Concat(group.Select(token => token.Text)).Trim();
            double start, end;
            if (loudness is not null && group[^1].Dtw is { } dtwEnd)
            {
                end = Math.Max(dtwEnd, previousEnd);
                // A word's DTW time falls a little before it truly ends, so a start taken from the
                // word before is moved on by 40 ms (the corpus measure: a median 31 ms early, the
                // tenth percentile 91 ms). A start found where sound rises out of silence is exact.
                start = Onset(loudness, silence, previousEnd, end) ?? (end - previousEnd > 1.5 ? end - 0.4 : Math.Min(previousEnd + FollowOn, end));
            }
            else
            {
                start = group[0].Start;
                end = index + 1 < groups.Count
                    ? Math.Min(Math.Max(group[^1].End, start), Math.Max(groups[index + 1][0].Start, start))
                    : group[^1].End;
            }

            end = Math.Max(end, start);

            // A word must be heard: whisper writes words into silence (a "Thank you." at the end of
            // a quiet stretch is the classic one), and a word with no sound under it is one of those.
            if (loudness is not null && !Heard(loudness, silence, start, end))
            {
                continue;
            }

            words.Add(new TranscribedWord(text, Flicks.FromSeconds(start), Flicks.FromSeconds(end), group.Min(token => token.Probability)));
            previousEnd = end;
        }

        return words;
    }

    /// <summary>True when there is sound above silence somewhere in a word's span, or a tenth of a second either side.</summary>
    private static bool Heard(float[] loudness, float silence, double start, double end)
    {
        int first = Math.Max(0, (int)(start * 100) - 10);
        int last = Math.Min(loudness.Length - 1, (int)(end * 100) + 10);
        for (int frame = first; frame <= last; frame++)
        {
            if (loudness[frame] >= silence)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What counts as silence: a twentieth of the loud speech's level, and never below a floor.</summary>
    private static float Silence(float[] loudness)
    {
        if (loudness.Length == 0)
        {
            return 0.001f;
        }

        float[] sorted = [.. loudness.Order()];
        return Math.Max(0.001f, sorted[(int)(sorted.Length * 0.95)] / 20);
    }

    /// <summary>
    /// The last place between two times where the sound rises out of at least a tenth of a second
    /// of silence, or null if it never does.
    /// </summary>
    private static double? Onset(float[] loudness, float silence, double from, double to)
    {
        int first = Math.Max(0, (int)(from * 100));
        int last = Math.Min(loudness.Length - 1, (int)(to * 100));
        for (int frame = last; frame >= first + 10; frame--)
        {
            if (loudness[frame] < silence || loudness[frame - 1] >= silence)
            {
                continue;
            }

            bool quietBefore = true;
            for (int before = frame - 10; before < frame; before++)
            {
                quietBefore &= loudness[before] < silence;
            }

            if (quietBefore)
            {
                return frame / 100.0;
            }
        }

        return null;
    }

    /// <summary>A token that is not text: the timestamp and control tokens.</summary>
    private static bool IsSpecial(string? text) =>
        text is null || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal);

    /// <summary>The DTW alignment heads for a model, from its file name.</summary>
    private static WhisperAlignmentHeadsPreset HeadsFor(string modelPath)
    {
        string name = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("large-v3-turbo", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.LargeV3Turbo,
            _ when name.Contains("large-v3", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.LargeV3,
            _ when name.Contains("large-v2", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.LargeV2,
            _ when name.Contains("medium.en", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.MediumEn,
            _ when name.Contains("medium", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.Medium,
            _ when name.Contains("small.en", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.SmallEn,
            _ when name.Contains("small", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.Small,
            _ when name.Contains("base.en", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.BaseEn,
            _ when name.Contains("base", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.Base,
            _ when name.Contains("tiny.en", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.TinyEn,
            _ when name.Contains("tiny", StringComparison.Ordinal) => WhisperAlignmentHeadsPreset.Tiny,
            _ => WhisperAlignmentHeadsPreset.None,
        };
    }

    /// <summary>A token as heard, with times in seconds from the first sample.</summary>
    internal readonly record struct HeardToken(string Text, float Probability, double Start, double End, double? Dtw);
}
