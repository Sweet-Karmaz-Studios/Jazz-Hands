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
    private SqliteConnection _connection = null!;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> Opening = new(StringComparer.OrdinalIgnoreCase);
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

        // One opening at a time per file in this process: a new database is switched to WAL and
        // given its tables while the others wait, rather than meeting its locks half way.
        lock (Opening.GetOrAdd(Path.GetFullPath(DatabasePath), _ => new Lock()))
        {
            OpenOrRepair();
        }

        _blobBytes = SumBlobBytes();
    }

    private void OpenOrRepair()
    {
        try
        {
            OpenPatiently();
        }
        catch (SqliteException damaged) when (damaged.SqliteErrorCode is Corrupt or NotADatabase)
        {
            // A cache can always be built again: a damaged one is set aside and started afresh,
            // rather than every import and thumbnail failing on it (2026-09-27, after a crash).
            try
            {
                SetAside();
            }
            catch (IOException)
            {
                _log.Warning(damaged, "The cache at {Path} is damaged, and in use elsewhere, so it is left as it is", DatabasePath);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(damaged).Throw();
            }

            _log.Warning(damaged, "The cache at {Path} was damaged; it is set aside and started again", DatabasePath);
            OpenPatiently();
        }
    }

    /// <summary>The environment variable that moves the cache elsewhere, for tests and CI.</summary>
    public const string FolderVariable = "JAZZ_CACHE_DIR";

    /// <summary>The per-user cache folder, or the one <see cref="FolderVariable"/> names.</summary>
    public static string DefaultFolder => Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } folder
        ? folder
        : Path.Combine(JazzHands.Core.JazzFolders.Local, "cache");

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

    /// <summary>The schema this build writes, kept in the database's <c>user_version</c>.</summary>
    /// <remarks>
    /// 1: thumbnails remember the frame they show (<c>frameFlicks</c>). 2: indexes for eviction
    /// and blob lookups. 3: thumbnails made again, HDR ones now tone mapped. A database from a newer
    /// build is used as it is; its tables are a superset.
    /// </remarks>
    public const int SchemaVersion = 3;

    /// <summary>The schema version of the open database.</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return UserVersion();
            }
        }
    }

    /// <summary>
    /// Moves everything known about one hash to another: for a file that was only touched, whose
    /// hash changed with its date while its content did not, so its thumbnails, waveform, probe
    /// and keyframes are kept rather than made again.
    /// </summary>
    public void Rekey(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            foreach (string table in new[] { "probe", "thumbs", "waveform", "keyframes", "analysis" })
            {
                // A probe is kept as hash#probeN; the suffix goes with it. Rows the new hash
                // already has win, and the old ones left over are dropped.
                using SqliteCommand move = _connection.CreateCommand();
                move.CommandText = $"UPDATE OR IGNORE {table} SET hash = $to || substr(hash, length($from) + 1) WHERE hash = $from OR hash LIKE $versioned";
                move.Parameters.AddWithValue("$from", from);
                move.Parameters.AddWithValue("$to", to);
                move.Parameters.AddWithValue("$versioned", from + "#%");
                move.ExecuteNonQuery();
            }
        }

        Forget(from);
    }

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

            if (parts.HasFlag(CacheParts.Analyses))
            {
                tables.Add("analysis");
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

    private const int Corrupt = 11;
    private const int NotADatabase = 26;

    /// <summary>
    /// <see cref="Open"/>, tried again for a few seconds while another process has the new
    /// database locked (switching it to WAL, making its tables).
    /// </summary>
    /// <remarks>
    /// A statement prepared while the schema is locked fails with SQLITE_BUSY, and SQLitePCL then
    /// throws <see cref="ArgumentOutOfRangeException"/> from its prepare rather than letting
    /// Microsoft.Data.Sqlite wait on the busy timeout, so the wait is here. Within one process the
    /// openings are one at a time already; this is for the editor and jazz starting together.
    /// </remarks>
    private void OpenPatiently()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Open();
                return;
            }
            catch (Exception busy) when (attempt < 50 && busy is ArgumentOutOfRangeException or SqliteException { SqliteErrorCode: Busy or Locked })
            {
                _log.Debug(busy, "The cache at {Path} is busy; trying again", DatabasePath);
                Thread.Sleep(100);
            }
        }
    }

    private const int Busy = 5;
    private const int Locked = 6;

    /// <summary>Opens the database, brings it up to date and checks it; throws when it is damaged.</summary>
    private void Open()
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // A private cache: in shared-cache mode two connections in one process that want the same
            // lock fail at once with SQLITE_LOCKED rather than waiting on the busy timeout, so two
            // sessions opening a new cache together crashed (2026-09-27). Private, they wait as two
            // processes do.
            Cache = SqliteCacheMode.Private,
        }.ToString());

        try
        {
            _connection.Open();
            Configure();
            CreateTables();
        }
        catch
        {
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    /// <summary>
    /// Moves a damaged database and its journal aside, and empties the blobs it indexed. Another
    /// process with the file open (the editor, jazz, jazz-mcp) keeps Windows from moving it; then
    /// the damage stands and is reported, rather than the file going from under that process.
    /// </summary>
    private void SetAside()
    {
        SqliteConnection.ClearAllPools();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (string suffix in (string[])["", "-wal", "-shm"])
        {
            string file = DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Move(file, $"{DatabasePath}.damaged-{stamp}{suffix}", overwrite: true);
            }
        }

        foreach (string blob in Directory.EnumerateFiles(BlobFolder, "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }
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

    /// <summary>
    /// Creates the tables and brings an older database up to date, as one exclusive transaction:
    /// the editor and jazz opening a new cache at the same moment otherwise raced, the second
    /// taking the first's half made thumbs table for an old one and dropping it.
    /// </summary>
    private void CreateTables()
    {
        Execute("BEGIN IMMEDIATE;");
        try
        {
            CreateAndMigrate();
            Execute("COMMIT;");
        }
        catch
        {
            try
            {
                Execute("ROLLBACK;");
            }
            catch (SqliteException)
            {
                // Already rolled back, or the database is too damaged to; the first error says why.
            }

            throw;
        }
    }

    private void CreateAndMigrate()
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

            CREATE TABLE IF NOT EXISTS analysis (
                hash     TEXT NOT NULL,
                stream   INTEGER NOT NULL,
                kind     TEXT NOT NULL,
                data     BLOB NOT NULL,
                lastUsed INTEGER NOT NULL,
                PRIMARY KEY (hash, stream, kind));

            CREATE TABLE IF NOT EXISTS blobs (
                id       TEXT PRIMARY KEY,
                bytes    INTEGER NOT NULL,
                lastUsed INTEGER NOT NULL);
            """;

        command.ExecuteNonQuery();

        Migrate();
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
