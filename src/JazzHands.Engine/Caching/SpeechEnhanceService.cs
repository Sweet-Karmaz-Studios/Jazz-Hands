using System.Buffers.Binary;
using System.Collections.Concurrent;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Models;
using JazzHands.Inference;
using JazzHands.Media.Audio;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>
/// Speech enhancement made ahead (Phase 43): a media file's sound stream run once through
/// DeepFilterNet 3, every channel at 48 kHz, and kept in the cache folder as a 32-bit float WAV,
/// which the sample server decodes in place of the original, mixed with it by the
/// <c>audio.enhance-speech</c> effect's amount.
/// </summary>
/// <remarks>
/// A network cannot run on the audio thread (it allocates and takes milliseconds a block), and
/// the published model hears whole sequences, so the file is enhanced in one pass, about twenty
/// times faster than it plays on the CPU, and kept like any other analysis.
/// </remarks>
public sealed class SpeechEnhanceService(CacheManager? cache = null)
{
    /// <summary>The model speech is enhanced with.</summary>
    public static ModelFile Model { get; } = ModelStore.Find("deepfilternet3")!;

    private static readonly ILogger Log = Serilog.Log.ForContext<SpeechEnhanceService>();
    private static readonly ConcurrentDictionary<string, SpeechEnhanceService> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>The service for a cache: one a folder, shared by the session and every sample server.</summary>
    public static SpeechEnhanceService For(CacheManager? cache) => Shared.GetOrAdd(cache?.Folder ?? CacheManager.DefaultFolder, _ => new SpeechEnhanceService(cache));

    /// <summary>Where enhanced sound is kept: in the cache folder (the default one when none is given).</summary>
    public string Folder => Path.Combine(cache?.Folder ?? CacheManager.DefaultFolder, "speech");

    /// <summary>The file a stream's enhanced sound is kept in.</summary>
    public string PathFor(string hash, int stream) =>
        Path.Combine(Folder, $"{(hash.StartsWith("xxh64:", StringComparison.Ordinal) ? hash[6..] : hash)}_{stream}_dfn.wav");

    /// <summary>True when a stream's enhanced sound has been made.</summary>
    public bool IsMade(string hash, int stream) => hash.Length > 0 && File.Exists(PathFor(hash, stream));

    /// <summary>
    /// Makes a stream's enhanced sound, unless it is made. Throws <see cref="FileNotFoundException"/>
    /// when the model is not downloaded.
    /// </summary>
    public string Enhance(MediaItem item, string path, MediaStream stream, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stream);
        string target = PathFor(item.Hash, stream.Index);
        if (File.Exists(target))
        {
            progress?.Report(1.0);
            return target;
        }

        if (!ModelStore.IsPresent(Model))
        {
            throw new FileNotFoundException($"The speech enhancement model is not downloaded. `model.download {Model.Name}` fetches it ({Model.Bytes / 1e6:0} MB).", ModelStore.PathOf(Model));
        }

        OneAtATime.Wait(cancellationToken);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            int channels = Math.Max(1, stream.Channels);
            Flicks length = stream.Duration > Flicks.Zero ? stream.Duration : item.Duration;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            float[][] planes = PlanarReader.Read(path, stream.Index, channels, length, DeepFilter.SampleRate, cancellationToken);
            using DeepFilter filter = DeepFilter.LoadArchive(ModelStore.PathOf(Model));
            for (int channel = 0; channel < channels; channel++)
            {
                int done = channel;
                var each = new Progress<double>(fraction => progress?.Report((done + fraction) / channels));
                planes[channel] = filter.Enhance(planes[channel], progress is null ? null : each, cancellationToken);
            }

            Directory.CreateDirectory(Folder);
            string part = target + ".part";
            try
            {
                WriteWav(part, planes);
                File.Move(part, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }

            Log.Information("Enhanced the speech of {Media} stream {Stream}: {Seconds:F1} s in {Elapsed:F1} s on {Provider}", item.Name, stream.Index, length.ToSeconds(), clock.Elapsed.TotalSeconds, filter.Provider);
            progress?.Report(1.0);
            return target;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Deletes the enhanced sound of one file content, or all of it when no hash is given (<c>cache.clear --analyses</c>).</summary>
    public void Delete(string? hash = null)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        string pattern = hash is { Length: > 0 } given ? $"{(given.StartsWith("xxh64:", StringComparison.Ordinal) ? given[6..] : given)}_*_dfn.wav" : "*_dfn.wav";
        foreach (string file in Directory.EnumerateFiles(Folder, pattern))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException exception)
            {
                Log.Warning(exception, "Could not delete enhanced sound {Path}", file);
            }
        }
    }

    /// <summary>Planar float samples as a 48 kHz IEEE float WAV, interleaved.</summary>
    private static void WriteWav(string path, float[][] planes)
    {
        int channels = planes.Length;
        int frames = planes[0].Length;
        long dataBytes = (long)frames * channels * 4;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 3);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], DeepFilter.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], DeepFilter.SampleRate * channels * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * 4));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 32);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)Math.Min(uint.MaxValue, dataBytes));
        file.Write(header);

        byte[] row = new byte[channels * 4 * 4096];
        for (int start = 0; start < frames; start += 4096)
        {
            int count = Math.Min(4096, frames - start);
            for (int i = 0; i < count; i++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(((i * channels) + channel) * 4), planes[channel][start + i]);
                }
            }

            file.Write(row, 0, count * channels * 4);
        }
    }
}
