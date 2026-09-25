using System.Globalization;
using JazzHands.Core.Time;
using Microsoft.Data.Sqlite;

namespace JazzHands.Media.Import;

/// <summary>Which parts of the cache an operation covers.</summary>
[Flags]
public enum CacheParts
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>What import learned about each file.</summary>
    Probes = 1,

    /// <summary>Keyframe indexes, for reverse play and stream copy.</summary>
    Keyframes = 2,

    /// <summary>Thumbnail pictures.</summary>
    Thumbnails = 4,

    /// <summary>Waveform peaks.</summary>
    Waveforms = 8,

    /// <summary>Everything in the database and its blob folder. Proxies are the proxy service's.</summary>
    All = Probes | Keyframes | Thumbnails | Waveforms,
}

/// <summary>What the cache holds, for <c>cache.stats</c>.</summary>
/// <param name="Probes">Cached probes.</param>
/// <param name="KeyframeIndexes">Cached keyframe indexes.</param>
/// <param name="Thumbnails">Cached thumbnails.</param>
/// <param name="Waveforms">Cached waveforms.</param>
/// <param name="BlobBytes">What the thumbnail and waveform files add up to.</param>
/// <param name="DatabaseBytes">The database file itself.</param>
public sealed record CacheUsage(
    int Probes,
    int KeyframeIndexes,
    int Thumbnails,
    int Waveforms,
    long BlobBytes,
    long DatabaseBytes);

/// <summary>A thumbnail as it was cached.</summary>
/// <param name="FrameTime">The source time of the frame it shows.</param>
/// <param name="Jpeg">The picture.</param>
public sealed record CachedThumbnail(Flicks FrameTime, byte[] Jpeg);

/// <content>
/// Blob files: thumbnails and waveforms, too big for the database and too many to keep forever.
/// </content>
/// <remarks>
/// Each blob is a file under <see cref="BlobFolder"/>, named for what it is and grouped by the
/// first two digits of its media hash so no folder holds everything. The database keeps a row per
/// blob with its size and when it was last read, and the running total is kept in memory so that
/// every write can evict to the cap before it returns: the cache is never over its cap for longer
/// than one write. The blob just written is never the one evicted.
///
/// A blob whose file has gone (deleted by hand, or by a disk cleaner) is a miss, and its row is
/// dropped when it is found.
/// </remarks>
public sealed partial class CacheManager
{
    private long _blobBytes;
    private long _capBytes;
    private long _clock;

    /// <summary>
    /// The most the thumbnail and waveform files may add up to, or 0 for no limit. Setting it
    /// evicts at once.
    /// </summary>
    public long CapBytes
    {
        get => Interlocked.Read(ref _capBytes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_gate)
            {
                _capBytes = value;
                EvictToCap(keep: null);
            }
        }
    }

    /// <summary>What the blob files add up to now.</summary>
    public long BlobBytes => Interlocked.Read(ref _blobBytes);

    /// <summary>Blobs evicted to stay under the cap since the cache was opened.</summary>
    public long Evicted { get; private set; }

    /// <summary>Where proxy files are kept: beside the blobs, managed by the engine's proxy service.</summary>
    public string ProxyFolder => Path.Combine(Folder, "proxies");

    /// <summary>Stores a thumbnail, replacing any at the same time and size.</summary>
    /// <param name="hash">The media's content hash.</param>
    /// <param name="streamIndex">The video stream.</param>
    /// <param name="time">The time it was asked for, which is its key.</param>
    /// <param name="size">Its height in pixels.</param>
    /// <param name="frameTime">The time of the frame it actually shows.</param>
    /// <param name="jpeg">The picture.</param>
    public void PutThumbnail(string hash, int streamIndex, Flicks time, int size, Flicks frameTime, ReadOnlySpan<byte> jpeg)
    {
        byte[] bytes = jpeg.ToArray();
        PutThumbnailFile(hash, streamIndex, time, size, frameTime, path => File.WriteAllBytes(path, bytes));
    }

    /// <summary>A cached thumbnail, or null.</summary>
    public CachedThumbnail? GetThumbnail(string hash, int streamIndex, Flicks time, int size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? blob;
        long frame;

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "SELECT blobId, frameFlicks FROM thumbs WHERE hash = $hash AND stream = $stream AND timeFlicks = $time AND size = $size";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$time", time.Value);
            command.Parameters.AddWithValue("$size", size);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            blob = reader.GetString(0);
            frame = reader.GetInt64(1);
        }

        byte[]? data = ReadBlob(blob);
        return data is null ? null : new CachedThumbnail(new Flicks(frame), data);
    }

    /// <summary>
    /// Every cached thumbnail of a stream at a size: the time it was asked for, and the time of
    /// the frame it shows. For knowing what is there without reading any of it.
    /// </summary>
    public IReadOnlyDictionary<Flicks, Flicks> ThumbnailTimes(string hash, int streamIndex, int size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var times = new Dictionary<Flicks, Flicks>();

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT timeFlicks, frameFlicks FROM thumbs WHERE hash = $hash AND stream = $stream AND size = $size";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$size", size);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                times[new Flicks(reader.GetInt64(0))] = new Flicks(reader.GetInt64(1));
            }
        }

        return times;
    }

    /// <summary>Stores a stream's waveform peaks, replacing any before.</summary>
    public void PutWaveform(string hash, int streamIndex, ReadOnlySpan<byte> peaks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] bytes = peaks.ToArray();
        string blob = BlobId(hash, "w", streamIndex.ToString(CultureInfo.InvariantCulture));

        lock (_gate)
        {
            WriteBlob(blob, path => File.WriteAllBytes(path, bytes));

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO waveform (hash, stream, resolution, blobId, lastUsed) VALUES ($hash, $stream, 1000, $blob, $now) "
                + "ON CONFLICT(hash, stream, resolution) DO UPDATE SET blobId = $blob, lastUsed = $now";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$blob", blob);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();

            EvictToCap(keep: blob);
        }
    }

    /// <summary>A stream's cached waveform peaks, or null.</summary>
    public byte[]? GetWaveform(string hash, int streamIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? blob;

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT blobId FROM waveform WHERE hash = $hash AND stream = $stream AND resolution = 1000";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            blob = command.ExecuteScalar() as string;
        }

        return blob is null ? null : ReadBlob(blob);
    }

    /// <summary>What the cache holds.</summary>
    public CacheUsage Usage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            long database = 0;
            foreach (string file in new[] { DatabasePath, DatabasePath + "-wal" })
            {
                if (File.Exists(file))
                {
                    database += new FileInfo(file).Length;
                }
            }

            return new CacheUsage(
                Count("probe"),
                Count("keyframes"),
                Count("thumbs"),
                Count("waveform"),
                _blobBytes,
                database);
        }
    }

    /// <summary>Empties parts of the cache.</summary>
    public void Clear(CacheParts parts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (parts.HasFlag(CacheParts.Thumbnails))
            {
                DropBlobsOf("thumbs", where: null);
            }

            if (parts.HasFlag(CacheParts.Waveforms))
            {
                DropBlobsOf("waveform", where: null);
            }

            if (parts.HasFlag(CacheParts.Probes))
            {
                Execute("DELETE FROM probe");
            }

            if (parts.HasFlag(CacheParts.Keyframes))
            {
                Execute("DELETE FROM keyframes");
            }

            if (parts.HasFlag(CacheParts.Thumbnails) && parts.HasFlag(CacheParts.Waveforms))
            {
                // Anything left is a blob no row points at any more: gone with the rest.
                Execute("DELETE FROM blobs");
                _blobBytes = 0;

                try
                {
                    if (Directory.Exists(BlobFolder))
                    {
                        Directory.Delete(BlobFolder, recursive: true);
                    }

                    Directory.CreateDirectory(BlobFolder);
                }
                catch (IOException error)
                {
                    _log.Warning(error, "Could not empty the blob folder {Folder}", BlobFolder);
                }
            }
        }
    }

    /// <summary>
    /// Stores a thumbnail whose file is written by <paramref name="write"/>, which is handed the
    /// path. Its size is whatever the file is afterwards, which is what lets the size cap be tested
    /// with sparse files rather than fifty real gigabytes.
    /// </summary>
    internal void PutThumbnailFile(string hash, int streamIndex, Flicks time, int size, Flicks frameTime, Action<string> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string blob = BlobId(
            hash,
            "t",
            string.Create(CultureInfo.InvariantCulture, $"{streamIndex}_{size}_{time.Value}"));

        lock (_gate)
        {
            WriteBlob(blob, write);

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO thumbs (hash, stream, timeFlicks, size, frameFlicks, blobId, lastUsed) "
                + "VALUES ($hash, $stream, $time, $size, $frame, $blob, $now) "
                + "ON CONFLICT(hash, stream, timeFlicks, size) DO UPDATE SET frameFlicks = $frame, blobId = $blob, lastUsed = $now";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$time", time.Value);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$frame", frameTime.Value);
            command.Parameters.AddWithValue("$blob", blob);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();

            EvictToCap(keep: blob);
        }
    }

    /// <summary>Brings an older database up to <see cref="SchemaVersion"/>, one step at a time.</summary>
    private void Migrate()
    {
        int version = UserVersion();
        if (version > SchemaVersion)
        {
            _log.Warning("The cache was written by a newer Jazz Hands (schema {Version}); using it as it is", version);
            return;
        }

        if (version < 1)
        {
            MigrateThumbs();
        }

        if (version < 2)
        {
            Execute("""
                CREATE INDEX IF NOT EXISTS blobs_by_use ON blobs (lastUsed);
                CREATE INDEX IF NOT EXISTS thumbs_by_blob ON thumbs (blobId);
                CREATE INDEX IF NOT EXISTS waveform_by_blob ON waveform (blobId);
                """);
        }

        if (version < SchemaVersion)
        {
            Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {SchemaVersion}"));
            _log.Information("The cache database moved from schema {From} to {To}", version, SchemaVersion);
        }
    }

    private int UserVersion()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The thumbs table as Phase 06 created it had no column for the frame a thumbnail shows. It
    /// was never written to, so it is replaced rather than altered.
    /// </summary>
    private void MigrateThumbs()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('thumbs') WHERE name = 'frameFlicks'";

        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        Execute("""
            DROP TABLE thumbs;
            CREATE TABLE thumbs (
                hash        TEXT NOT NULL,
                stream      INTEGER NOT NULL,
                timeFlicks  INTEGER NOT NULL,
                size        INTEGER NOT NULL,
                frameFlicks INTEGER NOT NULL,
                blobId      TEXT NOT NULL,
                lastUsed    INTEGER NOT NULL,
                PRIMARY KEY (hash, stream, timeFlicks, size));
            """);
    }

    /// <summary>Writes a blob's file and its row, keeping the running total. Inside the gate.</summary>
    private void WriteBlob(string blob, Action<string> write)
    {
        string path = BlobPath(blob);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long before = File.Exists(path) ? new FileInfo(path).Length : 0;
        write(path);
        long bytes = new FileInfo(path).Length;

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO blobs (id, bytes, lastUsed) VALUES ($id, $bytes, $now) "
            + "ON CONFLICT(id) DO UPDATE SET bytes = $bytes, lastUsed = $now";
        command.Parameters.AddWithValue("$id", blob);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$now", Tick());
        command.ExecuteNonQuery();

        _blobBytes += bytes - before;
    }

    /// <summary>A blob's bytes, marking it used; null when its file has gone.</summary>
    private byte[]? ReadBlob(string blob)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(BlobPath(blob));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            lock (_gate)
            {
                DropBlob(blob);
            }

            return null;
        }

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE blobs SET lastUsed = $now WHERE id = $id";
            command.Parameters.AddWithValue("$now", Tick());
            command.Parameters.AddWithValue("$id", blob);
            command.ExecuteNonQuery();
        }

        return data;
    }

    /// <summary>Evicts the least recently used blobs until the total is under the cap. Inside the gate.</summary>
    private void EvictToCap(string? keep)
    {
        if (_capBytes <= 0 || _blobBytes <= _capBytes)
        {
            return;
        }

        var victims = new List<string>();
        long total = _blobBytes;

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, bytes FROM blobs ORDER BY lastUsed, rowid";
            using SqliteDataReader reader = command.ExecuteReader();

            while (total > _capBytes && reader.Read())
            {
                string id = reader.GetString(0);
                if (string.Equals(id, keep, StringComparison.Ordinal))
                {
                    continue;
                }

                victims.Add(id);
                total -= reader.GetInt64(1);
            }
        }

        Execute("BEGIN");
        foreach (string victim in victims)
        {
            DropBlob(victim);
        }

        Execute("COMMIT");
        Evicted += victims.Count;

        _log.Debug("Evicted {Count} cache blobs to stay under {Cap} bytes", victims.Count, _capBytes);
    }

    /// <summary>Deletes a blob's file, its row, and whatever row pointed at it. Inside the gate.</summary>
    private void DropBlob(string blob)
    {
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT bytes FROM blobs WHERE id = $id";
            command.Parameters.AddWithValue("$id", blob);
            if (command.ExecuteScalar() is long bytes)
            {
                _blobBytes -= bytes;
            }
        }

        foreach (string statement in new[]
        {
            "DELETE FROM blobs WHERE id = $id",
            "DELETE FROM thumbs WHERE blobId = $id",
            "DELETE FROM waveform WHERE blobId = $id",
        })
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = statement;
            command.Parameters.AddWithValue("$id", blob);
            command.ExecuteNonQuery();
        }

        try
        {
            File.Delete(BlobPath(blob));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Could not delete the cache blob {Blob}", blob);
        }
    }

    /// <summary>Drops every blob a table's rows point at, optionally only for one hash. Inside the gate.</summary>
    private void DropBlobsOf(string table, string? where)
    {
        var blobs = new List<string>();

        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = where is null
                ? $"SELECT blobId FROM {table}"
                : $"SELECT blobId FROM {table} WHERE hash = $hash";
            if (where is not null)
            {
                command.Parameters.AddWithValue("$hash", where);
            }

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                blobs.Add(reader.GetString(0));
            }
        }

        Execute("BEGIN");
        foreach (string blob in blobs)
        {
            DropBlob(blob);
        }

        Execute("COMMIT");
    }

    private long SumBlobBytes()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM blobs";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private int Count(string table)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A blob's name: what it is, for whom, with the hash's algorithm prefix dropped for the file system.</summary>
    private static string BlobId(string hash, string kind, string detail)
    {
        int colon = hash.IndexOf(':', StringComparison.Ordinal);
        string digits = colon >= 0 ? hash[(colon + 1)..] : hash;
        return $"{digits}_{kind}_{detail}";
    }

    private string BlobPath(string blob) =>
        Path.Combine(BlobFolder, blob.Length >= 2 ? blob[..2] : blob, blob);

    /// <summary>
    /// When a blob was last used, for ordering eviction: milliseconds, and never the same twice,
    /// so blobs written in one burst still leave in the order they came.
    /// </summary>
    private long Tick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _clock = Math.Max(_clock + 1, now);
        return _clock;
    }
}
