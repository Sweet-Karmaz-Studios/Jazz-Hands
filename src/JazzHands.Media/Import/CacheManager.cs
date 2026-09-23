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
/// Phase 06 fills in the probe table. Thumbnails, waveforms and keyframe indexes arrive in Phase
/// 14, and their tables are created now so that a cache written today is readable then.
/// </remarks>
public sealed class CacheManager : IDisposable
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

    /// <summary>Forgets everything known about a hash, which is what a replaced file needs.</summary>
    public void Forget(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            foreach (string table in new[] { "probe", "thumbs", "waveform", "keyframes" })
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE hash = $hash";
                command.Parameters.AddWithValue("$hash", hash);
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
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            foreach (string table in new[] { "probe", "thumbs", "waveform", "keyframes", "blobs" })
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table}";
                command.ExecuteNonQuery();
            }
        }

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

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
