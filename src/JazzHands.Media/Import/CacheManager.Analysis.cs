using Microsoft.Data.Sqlite;

namespace JazzHands.Media.Import;

/// <summary>What reading a whole file found, kept in the database by hash, stream and kind.</summary>
/// <remarks>
/// Phase 37 keeps scene-cut measurements here (kind <c>scenes</c>): a few hundred kilobytes for
/// ten minutes, small enough for a row, and read whole every time, so a blob file would only add a
/// second place for it to go missing from. A kind names its own format and version, so a change
/// to what is stored is a new kind and the old rows are simply never read again.
/// </remarks>
public sealed partial class CacheManager
{
    /// <summary>A stream's cached analysis of a kind, or null.</summary>
    public byte[]? GetAnalysis(string hash, int streamIndex, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT data FROM analysis WHERE hash = $hash AND stream = $stream AND kind = $kind";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$kind", kind);

            if (command.ExecuteScalar() is not byte[] data)
            {
                return null;
            }

            using SqliteCommand touch = _connection.CreateCommand();
            touch.CommandText = "UPDATE analysis SET lastUsed = $now WHERE hash = $hash AND stream = $stream AND kind = $kind";
            touch.Parameters.AddWithValue("$now", Now());
            touch.Parameters.AddWithValue("$hash", hash);
            touch.Parameters.AddWithValue("$stream", streamIndex);
            touch.Parameters.AddWithValue("$kind", kind);
            touch.ExecuteNonQuery();
            return data;
        }
    }

    /// <summary>Stores a stream's analysis of a kind, replacing any before.</summary>
    public void PutAnalysis(string hash, int streamIndex, string kind, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] bytes = data.ToArray();

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO analysis (hash, stream, kind, data, lastUsed) VALUES ($hash, $stream, $kind, $data, $now) "
                + "ON CONFLICT(hash, stream, kind) DO UPDATE SET data = $data, lastUsed = $now";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$stream", streamIndex);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$data", bytes);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }
}
