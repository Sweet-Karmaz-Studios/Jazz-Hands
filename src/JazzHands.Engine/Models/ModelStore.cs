using System.Net.Http;
using System.Security.Cryptography;
using JazzHands.Core;
using Serilog;

namespace JazzHands.Engine.Models;

/// <summary>A machine learning model Jazz Hands can fetch: where from, what it weighs and its pinned hash.</summary>
/// <param name="Name">The name it is asked for by (<c>whisper-large-v3-turbo</c>).</param>
/// <param name="File">The file it is kept as in the models folder.</param>
/// <param name="Url">Its project's own release of it.</param>
/// <param name="Sha256">The SHA-256 it must have, upper case hex.</param>
/// <param name="Bytes">Its size, shown before asking to fetch it.</param>
/// <param name="License">Its licence.</param>
/// <param name="Purpose">What it is for, in a few words.</param>
public sealed record ModelFile(string Name, string File, string Url, string Sha256, long Bytes, string License, string Purpose);

/// <summary>
/// The models Phases 39 to 43 use and the folder they are kept in (Phase 39): the same list as
/// <c>tools/get-models.ps1</c>, each pinned by SHA-256 like FFmpeg. They live in
/// <c>%LOCALAPPDATA%\JazzHands\models</c> (<see cref="JazzFolders.Local"/>), not the cache:
/// clearing the cache must not throw away a 1.6 GB download.
/// </summary>
/// <remarks>
/// Nothing is ever fetched without being asked: a command that needs a model that is not here
/// refuses with <c>model-missing</c> and names <c>model.download</c> and the size, and the editor
/// asks the person first.
/// </remarks>
public static class ModelStore
{
    /// <summary>The speech model: whisper large-v3-turbo, whisper.cpp's ggml build (Docs/SPIKES.md S9).</summary>
    public static ModelFile Whisper { get; } = new(
        "whisper-large-v3-turbo",
        "ggml-large-v3-turbo.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
        "1FC70F774D38EB169993AC391EEA357EF47C88757EF72EE5943879B7E8E2BC69",
        1_624_555_275,
        "MIT",
        "speech to text");

    /// <summary>Every model there is.</summary>
    public static IReadOnlyList<ModelFile> All { get; } =
    [
        Whisper,
        new("rvm-mobilenetv3", "rvm_mobilenetv3_fp32.onnx",
            "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx",
            "88D4531297118F595BF2FD60F6F566AEC2E559393802D1F436C380F0CBBD2828", 14_975_696, "GPL-3.0", "background removal"),
        new("deepfilternet3", "DeepFilterNet3_onnx.tar.gz",
            "https://github.com/Rikorose/DeepFilterNet/raw/v0.5.6/models/DeepFilterNet3_onnx.tar.gz",
            "C94D91F70911001C946E0FABB4AA9ADC37045F45A03B56008CB0C8244CB63616", 7_983_136, "MIT or Apache-2.0", "speech enhancement"),
    ];

    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ModelStore));

    /// <summary>
    /// Where models are kept: <c>JAZZ_MODELS_DIR</c> when set (the tests share one download), else
    /// the models folder under <see cref="JazzFolders.Local"/>.
    /// </summary>
    public static string Folder => Environment.GetEnvironmentVariable("JAZZ_MODELS_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(JazzFolders.Local, "models");

    /// <summary>A model by name, or null.</summary>
    public static ModelFile? Find(string name) =>
        All.FirstOrDefault(model => string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a model's file is, or would be.</summary>
    public static string PathOf(ModelFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(Folder, model.File);
    }

    /// <summary>
    /// True when the model's file is here at its size. The hash was checked when it was fetched;
    /// reading 1.6 GB again on every use would cost seconds.
    /// </summary>
    public static bool IsPresent(ModelFile model) =>
        new FileInfo(PathOf(model)) is { Exists: true } file && file.Length == model.Bytes;

    /// <summary>
    /// Fetches a model into the folder, checking its SHA-256 before keeping it. A file of the wrong
    /// hash, or a fetch that stopped, leaves nothing behind.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="progress">Told the fraction fetched.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <param name="client">The client to fetch with; a new one when null.</param>
    public static async Task DownloadAsync(ModelFile model, IProgress<double>? progress = null, CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        string path = PathOf(model);
        Directory.CreateDirectory(Folder);
        string part = path + ".part";

        using HttpClient? owned = client is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        HttpClient http = client ?? owned!;
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(model.Url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? model.Bytes;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report(Math.Min(1.0, done / (double)Math.Max(1, total)));
                }
            }

            string got = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(got, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{model.Name} arrived with the SHA-256 {got}, not the pinned {model.Sha256}. Nothing was kept.");
            }

            File.Move(part, path, overwrite: true);
            Log.Information("Fetched {Model} to {Path}", model.Name, path);
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }
    }
}
