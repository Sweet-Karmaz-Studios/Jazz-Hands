using System.IO;
using System.Text.Json;
using JazzHands.Render.Color;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// What the monitor the preview is shown on expects. A preference of the machine, not of a
/// project, so it is kept beside the keymap and needs no command: exports and scopes never read it.
/// </summary>
public interface IDisplaySettings
{
    /// <summary>The monitor's transfer: sRGB for a desktop monitor, unless the person says otherwise.</summary>
    DisplayTransfer Transfer { get; set; }
}

/// <summary>The display preference in <c>%APPDATA%\JazzHands\display.json</c>.</summary>
public sealed class FileDisplaySettings : IDisplaySettings
{
    private readonly string _path;
    private DisplayTransfer _transfer;

    /// <summary>Reads the file, or starts at sRGB when there is none or it cannot be read.</summary>
    public FileDisplaySettings(string? path = null)
    {
        _path = path ?? Path.Combine(JazzHands.Core.JazzFolders.Roaming, "display.json");

        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path)) is { } stored
                && Enum.TryParse(stored.Transfer, ignoreCase: true, out DisplayTransfer transfer))
            {
                _transfer = transfer;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.ForContext<FileDisplaySettings>().Warning(error, "The display setting at {Path} could not be read; the preview assumes an sRGB monitor", _path);
        }
    }

    /// <inheritdoc />
    public DisplayTransfer Transfer
    {
        get => _transfer;
        set
        {
            if (value == _transfer)
            {
                return;
            }

            _transfer = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(value.ToString())));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.ForContext<FileDisplaySettings>().Warning(error, "The display setting could not be saved to {Path}", _path);
            }
        }
    }

    private sealed record Stored(string Transfer);
}

/// <summary>The display preference in memory, for tests and a machine that keeps nothing.</summary>
public sealed class MemoryDisplaySettings : IDisplaySettings
{
    /// <inheritdoc />
    public DisplayTransfer Transfer { get; set; }
}
