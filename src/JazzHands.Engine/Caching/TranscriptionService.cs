using System.Collections.Concurrent;
using System.Text.Json;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Speech;
using JazzHands.Core.Time;
using JazzHands.Engine.Models;
using JazzHands.Media.Audio;
using JazzHands.Media.Import;
using JazzHands.Speech;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>
/// Transcripts of media files (Phase 39): heard once per file content, sound stream, model and
/// language, then kept in the cache database and in memory, so every clip of a file, and every
/// later request, reads the same words at once.
/// </summary>
/// <remarks>
/// <para>
/// A transcript is of the whole stream, not a clip's stretch of it: clips share it and trims do
/// not make it stale. It is kept beside the project rather than in it (a ten minute talk is a few
/// thousand words), keyed to the file by its content hash like every other analysis, under the
/// kind <c>speech2:&lt;model&gt;:&lt;language&gt;</c>, and the latest of any model or language also
/// under <c>speech2</c>, which is what the words of a clip are read from.
/// </para>
/// <para>
/// One transcription runs at a time (the model takes gigabytes of video memory); two requests for
/// the same stream share one. <see cref="Made"/> tells the Transcript panel a new one is ready.
/// </para>
/// </remarks>
public sealed class TranscriptionService(CacheManager? cache = null)
{
    /// <summary>
    /// The cache kind of the latest transcript of a stream, whatever its model and language. Version 2
    /// keeps spoken fillers (the transcriber's verbatim prompt); version 1 transcripts dropped them and
    /// are not read.
    /// </summary>
    public const string LatestKind = "speech2";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private readonly ConcurrentDictionary<(string Hash, int Stream, string Kind), Transcript> _known = new();
    private readonly ConcurrentDictionary<(string Hash, int Stream, string Kind), Task<Transcript>> _running = new();
    private readonly ILogger _log = Log.ForContext<TranscriptionService>();

    /// <summary>Raised on the worker thread when a transcript has been made (not when one is read from the cache).</summary>
    public event Action<Transcript>? Made;

    /// <summary>The cache kind for a model and language; <c>auto</c> for a detected language.</summary>
    public static string KindOf(string model, string? language) => $"{LatestKind}:{model}:{(language is { Length: > 0 } given ? given.ToLowerInvariant() : "auto")}";

    /// <summary>A stream's transcript already made: by that model and language when given, else the latest. Null when there is none.</summary>
    public Transcript? Cached(string hash, int stream, string? model = null, string? language = null)
    {
        if (hash.Length == 0)
        {
            return null;
        }

        string kind = model is null ? LatestKind : KindOf(model, language);
        if (_known.TryGetValue((hash, stream, kind), out Transcript? known))
        {
            return known;
        }

        try
        {
            if (cache?.GetAnalysis(hash, stream, kind) is { } bytes && Read(bytes) is { } stored)
            {
                _known[(hash, stream, kind)] = stored;
                return stored;
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            _log.Warning(exception, "Could not read a transcript from the cache");
        }

        return null;
    }

    /// <summary>
    /// A media item's sound stream as words: the cached transcript, or a new one heard from its
    /// file. Throws <see cref="FileNotFoundException"/> when the model is not downloaded.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="path">Where its file is.</param>
    /// <param name="stream">The sound stream's index.</param>
    /// <param name="language">The language (ISO 639-1), or null to detect it.</param>
    /// <param name="again">Hear it again even when it is cached.</param>
    /// <param name="progress">Told the fraction done.</param>
    /// <param name="cancellationToken">Stops it.</param>
    public Task<Transcript> TranscribeAsync(MediaItem item, string path, int stream, string? language = "en", bool again = false, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelFile model = ModelStore.Whisper;

        if (!again && Cached(item.Hash, stream, model.Name, language) is { } known)
        {
            progress?.Report(1.0);
            return Task.FromResult(known);
        }

        if (!ModelStore.IsPresent(model))
        {
            throw new FileNotFoundException($"The speech model is not downloaded. `jazz model download {model.Name}` fetches it ({model.Bytes / 1_000_000_000.0:0.0} GB).", ModelStore.PathOf(model));
        }

        var key = (item.Hash, stream, KindOf(model.Name, language));
        if (!again && _running.TryGetValue(key, out Task<Transcript>? running))
        {
            return running;
        }

        Task<Transcript> task = Task.Run(() => HearAsync(item, path, stream, language, model, progress, cancellationToken), cancellationToken);
        _running[key] = task;
        _ = task.ContinueWith(_ => _running.TryRemove(key, out Task<Transcript>? _), TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// The sound a clip plays as words, as <c>speech.transcribe &lt;clip&gt;</c> hears it, with
    /// progress: for the Transcript panel, which hears in the background rather than holding the
    /// command queue. Null when the clip plays no sound from a file.
    /// </summary>
    public async Task<Transcript?> TranscribeClipAsync(Project project, string clipId, string projectPath, string? language = "en", IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FindClip(clipId) is not { } found || Handlers.SpeechHelp.SoundOf(project, found) is not { } sound)
        {
            return null;
        }

        string path = Handlers.HandlerHelp.Resolve(projectPath, sound.Item.RelativePath);
        return await TranscribeAsync(sound.Item, path, sound.Stream, language, again: false, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a transcript made elsewhere (a corrected one, or a test's) as the latest of its stream,
    /// under its model and language.
    /// </summary>
    public void Remember(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        Keep(transcript, KindOf(transcript.Model, transcript.Language));
    }

    /// <summary>Forgets what is held in memory about one file content.</summary>
    public void Forget(string hash)
    {
        foreach (var key in _known.Keys.Where(key => string.Equals(key.Hash, hash, StringComparison.Ordinal)))
        {
            _known.TryRemove(key, out _);
        }
    }

    /// <summary>Forgets what is held in memory, after the cache is cleared.</summary>
    public void Clear() => _known.Clear();

    /// <summary>A transcript as the bytes the cache keeps: its JSON.</summary>
    internal static byte[] Write(Transcript transcript) => JsonSerializer.SerializeToUtf8Bytes(transcript, JazzJson.Options);

    /// <summary>A transcript from the bytes the cache keeps, or null when they are not one.</summary>
    internal static Transcript? Read(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<Transcript>(bytes, JazzJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Transcript> HearAsync(MediaItem item, string path, int stream, string? language, ModelFile model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await OneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Reading the file is a tenth of the work, hearing it the rest.
            Flicks length = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == stream)?.Duration is { } known && known > Flicks.Zero
                ? known
                : item.Duration;
            float[] samples = MonoReader.Read(path, stream, Flicks.Zero, length, Transcriber.SampleRate, cancellationToken);
            progress?.Report(0.1);

            var hearing = new Progress<double>(fraction => progress?.Report(0.1 + (0.9 * fraction)));
            HeardSpeech heard = await Transcriber.HearAsync(
                samples,
                new TranscriberOptions(ModelStore.PathOf(model), language),
                progress is null ? null : hearing,
                cancellationToken).ConfigureAwait(false);

            var transcript = new Transcript(item.Hash, stream, model.Name, heard.Language, [.. heard.Words]);
            Keep(transcript, KindOf(model.Name, language));
            _log.Information("Transcribed {Media} stream {Stream}: {Words} words in {Language}", item.Name, stream, transcript.Words.Length, transcript.Language);
            Made?.Invoke(transcript);
            return transcript;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private void Keep(Transcript transcript, string kind)
    {
        if (transcript.MediaHash.Length == 0)
        {
            return;
        }

        _known[(transcript.MediaHash, transcript.Stream, kind)] = transcript;
        _known[(transcript.MediaHash, transcript.Stream, LatestKind)] = transcript;
        try
        {
            byte[] bytes = Write(transcript);
            cache?.PutAnalysis(transcript.MediaHash, transcript.Stream, kind, bytes);
            cache?.PutAnalysis(transcript.MediaHash, transcript.Stream, LatestKind, bytes);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            // Losing the cache costs the next request a transcription, nothing more.
            _log.Warning(exception, "Could not keep the transcript of {Hash} in the cache", transcript.MediaHash);
        }
    }
}
