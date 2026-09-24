using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;

namespace JazzHands.Media.Import;

/// <summary>
/// The cache that survives between sessions, keyed by media content hash.
/// </summary>
/// <remarks>
/// One SQLite file under <c>%LOCALAPPDATA%\JazzHands\cache</c>, with the large values written as
/// blob files beside it rather than inside it. Probes are small and go in the database; thumbnail
/// strips and waveform peaks are not, and a multi-gigabyte SQLite file is slow to open and
/// miserable to evict from.
///
/// Keyed by content hash rather than by path, so a file that moves keeps everything that was ever
/// computed about it, and a file that is replaced in place loses it, which is exactly right both
/// times.
///
/// Phase 06 filled in the probe table and Phase 07 the keyframe indexes. Phase 14 adds thumbnails
/// and waveforms, whose bytes are blob files, and the size cap that evicts them (see
/// CacheManager.Blobs.cs).
/// </remarks>
public sealed partial class CacheManager : IDisposable
{
    private readonly ILogger _log = Log.ForContext<CacheManager>();
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Opens or creates a cache.</summary>
    /// <param name="folder">Where it lives. Defaults to the per-user cache folder.</param>
    public CacheManager(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(BlobFolder);

        DatabasePath = Path.Combine(Folder, "cache.db");

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

        _connection.Open();
        Configure();
        CreateTables();
        _blobBytes = SumBlobBytes();
    }

    /// <summary>The per-user cache folder.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JazzHands",
        "cache");

    /// <summary>Where this cache lives.</summary>
    public string Folder { get; }

    /// <summary>The database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Where blob files are written.</summary>
    public string BlobFolder => Path.Combine(Folder, "blobs");

    /// <summary>The cached probe for a hash, as the JSON it was stored as, or null.</summary>
    public string? GetProbe(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT json FROM probe WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);

            object? value = command.ExecuteScalar();

            if (value is string json)
            {
                Touch("probe", hash);
                return json;
            }

            return null;
        }
    }

    /// <summary>Stores a probe against a hash, replacing any previous one.</summary>
    public void PutProbe(string hash, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(json);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO probe (hash, json, lastUsed) VALUES ($hash, $json, $now) "
                + "ON CONFLICT(hash) DO UPDATE SET json = $json, lastUsed = $now";

            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The cached keyframe index for a hash and stream, as stored JSON, or null.</summary>
    public string? GetKeyframes(string hash, int streamIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT ptsJson FROM keyframes WHERE hash = $hash AND stream = $stream";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);

            if (command.ExecuteScalar() is string json)
            {
                TouchKeyframes(hash, streamIndex);
                return json;
            }

            return null;
        }
    }

    /// <summary>Stores a keyframe index against a hash and stream, replacing any previous one.</summary>
    public void PutKeyframes(string hash, int streamIndex, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(json);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO keyframes (hash, stream, ptsJson, lastUsed) VALUES ($hash, $stream, $json, $now) "
                + "ON CONFLICT(hash, stream) DO UPDATE SET ptsJson = $json, lastUsed = $now";

            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>How many keyframe indexes are cached, for `jazz cache stats` and the tests.</summary>
    public int KeyframeIndexCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_gate)
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM keyframes";
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Forgets everything known about a hash, which is what a replaced file needs.</summary>
    public void Forget(string hash) => Forget(hash, CacheParts.All);

    /// <summary>Forgets parts of what is known about a hash.</summary>
    public void Forget(string hash, CacheParts parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            // The blob files first, while the rows still say which they are.
            if (parts.HasFlag(CacheParts.Thumbnails))
            {
                DropBlobsOf("thumbs", hash);
            }

            if (parts.HasFlag(CacheParts.Waveforms))
            {
                DropBlobsOf("waveform", hash);
            }

            var tables = new List<string>();
            if (parts.HasFlag(CacheParts.Probes))
            {
                tables.Add("probe");
            }

            if (parts.HasFlag(CacheParts.Keyframes))
            {
                tables.Add("keyframes");
            }

            foreach (string table in tables)
            {
                using SqliteCommand command = _connection.CreateCommand();
                // A probe is kept under the hash and the version of the prober that made it
                // (hash#probe2). Hashes are hex, so the prefix needs no escaping.
                command.CommandText = $"DELETE FROM {table} WHERE hash = $hash OR hash LIKE $versioned";
                command.Parameters.AddWithValue("$hash", hash);
                command.Parameters.AddWithValue("$versioned", hash + "#%");
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>How many probes are cached, for `jazz cache stats` and the tests.</summary>
    public int ProbeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_gate)
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM probe";
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Empties the cache.</summary>
    public void Clear() => Clear(CacheParts.All);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void Configure()
    {
        using SqliteCommand command = _connection.CreateCommand();

        // WAL so a background thumbnail worker writing does not block the UI thread reading.
        // Normal synchronous is right for a cache: losing the last write to a power cut costs a
        // recomputation, and full synchronous costs a disk flush on every thumbnail.
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
    }

    private void CreateTables()
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS probe (
                hash     TEXT PRIMARY KEY,
                json     TEXT NOT NULL,
                lastUsed INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS thumbs (
                hash      TEXT NOT NULL,
                stream    INTEGER NOT NULL,
                timeFlicks INTEGER NOT NULL,
                size      INTEGER NOT NULL,
                blobId    TEXT NOT NULL,
                lastUsed  INTEGER NOT NULL,
                PRIMARY KEY (hash, stream, timeFlicks, size));

            CREATE TABLE IF NOT EXISTS waveform (
                hash       TEXT NOT NULL,
                stream     INTEGER NOT NULL,
                resolution INTEGER NOT NULL,
                blobId     TEXT NOT NULL,
                lastUsed   INTEGER NOT NULL,
                PRIMARY KEY (hash, stream, resolution));

            CREATE TABLE IF NOT EXISTS keyframes (
                hash     TEXT NOT NULL,
                stream   INTEGER NOT NULL,
                ptsJson  TEXT NOT NULL,
                lastUsed INTEGER NOT NULL,
                PRIMARY KEY (hash, stream));

            CREATE TABLE IF NOT EXISTS blobs (
                id       TEXT PRIMARY KEY,
                bytes    INTEGER NOT NULL,
                lastUsed INTEGER NOT NULL);
            """;

        command.ExecuteNonQuery();

        MigrateThumbs();
    }

    /// <summary>Marks an entry as used, so eviction takes the oldest rather than the unluckiest.</summary>
    private void Touch(string table, string hash)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET lastUsed = $now WHERE hash = $hash";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
    }

    /// <summary>Marks one stream's keyframe index as used.</summary>
    private void TouchKeyframes(string hash, int streamIndex)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "UPDATE keyframes SET lastUsed = $now WHERE hash = $hash AND stream = $stream";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$stream", streamIndex);
        command.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
