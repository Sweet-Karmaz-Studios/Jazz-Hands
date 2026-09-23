using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;

namespace JazzHands.Media.Import;

/// <summary>
/// The content hash a media file is cached and recognised by.
/// </summary>
/// <remarks>
/// xxHash64 over the first four megabytes, the last four megabytes, the length and the write
/// time, rather than over the whole file. Hashing a 200 GB camera card in full would take minutes
/// per file at import, and the thing this has to be good at is telling two files apart, not
/// resisting an attacker. Two different recordings that agree on eight megabytes, their exact
/// byte count and their timestamp do not happen by accident.
///
/// Including the write time is what makes a re-encode of the same length noticed. Excluding the
/// path is what lets a file that moves keep its thumbnails, its waveform and its proxy.
/// </remarks>
public static class MediaHasher
{
    /// <summary>How much is read from each end of the file.</summary>
    public const int SampleBytes = 4 * 1024 * 1024;

    /// <summary>The hash of a file, as a lower-case hex string prefixed with the algorithm.</summary>
    /// <exception cref="FileNotFoundException">The file is not there.</exception>
    public static string Hash(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"'{path}' is not there, so it cannot be hashed.", path);
        }

        var hash = new XxHash64();

        using (FileStream stream = file.OpenRead())
        {
            byte[] buffer = new byte[Math.Min(SampleBytes, Math.Max(file.Length, 1))];

            Read(stream, buffer, hash);

            // Only read the tail when the file is big enough for it to be a different part of it.
            if (file.Length > SampleBytes * 2L)
            {
                stream.Seek(-SampleBytes, SeekOrigin.End);
                Read(stream, new byte[SampleBytes], hash);
            }
        }

        Span<byte> trailer = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(trailer, file.Length);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[8..], file.LastWriteTimeUtc.Ticks);
        hash.Append(trailer);

        return Format(hash.GetCurrentHashAsUInt64());
    }

    /// <summary>
    /// The hash of a run of numbered image files.
    /// </summary>
    /// <remarks>
    /// A sequence is one media item, so it needs one hash. Hashing every frame of a thousand
    /// frame render would defeat the point, so this takes the first, the middle and the last,
    /// which is enough to notice a re-render.
    /// </remarks>
    public static string HashSequence(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            throw new ArgumentException("A sequence needs at least one file to hash.", nameof(paths));
        }

        var hash = new XxHash64();

        foreach (int index in new[] { 0, paths.Count / 2, paths.Count - 1 }.Distinct())
        {
            hash.Append(System.Text.Encoding.UTF8.GetBytes(Hash(paths[index])));
        }

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(trailer, paths.Count);
        hash.Append(trailer);

        return Format(hash.GetCurrentHashAsUInt64());
    }

    /// <summary>The folder a blob for this hash lives under, which keeps any one folder small.</summary>
    public static string BlobPrefix(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        int colon = hash.IndexOf(':', StringComparison.Ordinal);
        string digits = colon >= 0 ? hash[(colon + 1)..] : hash;

        return digits.Length >= 2 ? digits[..2] : digits;
    }

    private static void Read(FileStream stream, byte[] buffer, XxHash64 hash)
    {
        int read;
        int total = 0;

        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        hash.Append(buffer.AsSpan(0, total));
    }

    private static string Format(ulong value) =>
        "xxh64:" + value.ToString("x16", CultureInfo.InvariantCulture);
}
