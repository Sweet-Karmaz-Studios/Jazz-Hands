using System.IO;
using System.Text.Json;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// The effect types a person has starred. A preference of the person, not of a project, so it is
/// kept beside the keymap rather than in any .jazz file and needs no command.
/// </summary>
public interface IEffectFavorites
{
    /// <summary>The starred type ids.</summary>
    IReadOnlySet<string> Ids { get; }

    /// <summary>Stars or unstars a type.</summary>
    void Set(string typeId, bool favorite);
}

/// <summary>Favorites in <c>%APPDATA%\JazzHands\effect-favorites.json</c>.</summary>
public sealed class FileEffectFavorites : IEffectFavorites
{
    private readonly string _path;
    private readonly HashSet<string> _ids;

    /// <summary>Reads the file, or starts empty when there is none or it cannot be read.</summary>
    public FileEffectFavorites(string? path = null)
    {
        _path = path ?? Path.Combine(JazzHands.Core.JazzFolders.Roaming, "effect-favorites.json");
        _ids = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) is { } stored)
            {
                _ids.UnionWith(stored);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.ForContext<FileEffectFavorites>().Warning(error, "Effect favorites at {Path} could not be read; starting with none", _path);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Ids => _ids;

    /// <inheritdoc />
    public void Set(string typeId, bool favorite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);

        if (!(favorite ? _ids.Add(typeId) : _ids.Remove(typeId)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_ids.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.ForContext<FileEffectFavorites>().Warning(error, "Effect favorites could not be saved to {Path}", _path);
        }
    }
}

/// <summary>Favorites kept in memory, for tests and for a session that should not write any file.</summary>
public sealed class MemoryEffectFavorites : IEffectFavorites
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlySet<string> Ids => _ids;

    /// <inheritdoc />
    public void Set(string typeId, bool favorite)
    {
        if (favorite)
        {
            _ids.Add(typeId);
        }
        else
        {
            _ids.Remove(typeId);
        }
    }
}
