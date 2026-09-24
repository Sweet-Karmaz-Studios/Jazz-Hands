using System.Runtime.InteropServices;
using System.Windows;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>Where copied clips go: the Windows clipboard in the app, a field in tests.</summary>
public interface IClipboardService
{
    /// <summary>Puts copied clips on the clipboard, as the JSON <c>clipboard.copy</c> returns.</summary>
    void SetClips(string json);

    /// <summary>The copied clips on the clipboard, or null when there are none.</summary>
    string? GetClips();
}

/// <summary>
/// The Windows clipboard, holding copied clips under a format of our own and as text.
/// </summary>
/// <remarks>
/// The text copy is what lets a person paste clips into a chat, or Claude Code hand them to
/// <c>clip.paste --data</c>; the private format is what a paste looks for first, so text that
/// merely looks like JSON is not mistaken for clips. Another program can hold the clipboard open
/// for a moment, which Windows reports as an exception; that is logged and treated as empty.
/// </remarks>
public sealed class WindowsClipboardService : IClipboardService
{
    /// <summary>The clipboard format name for copied clips.</summary>
    public const string Format = "JazzHands.Clips";

    private readonly ILogger _log = Log.ForContext<WindowsClipboardService>();

    /// <inheritdoc />
    public void SetClips(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var data = new DataObject();
        data.SetData(Format, json);
        data.SetText(json);

        try
        {
            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        catch (COMException error)
        {
            _log.Warning(error, "The clipboard was busy; the clips were not copied");
        }
    }

    /// <inheritdoc />
    public string? GetClips()
    {
        try
        {
            if (System.Windows.Clipboard.GetData(Format) is string clips)
            {
                return clips;
            }

            // Clips copied as text, by a script or from another Jazz Hands.
            string text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            return text.TrimStart().StartsWith('{') && text.Contains("\"clips\"", StringComparison.Ordinal) ? text : null;
        }
        catch (COMException error)
        {
            _log.Warning(error, "The clipboard was busy; nothing was pasted");
            return null;
        }
    }
}

/// <summary>A clipboard in memory, for tests and headless use.</summary>
public sealed class MemoryClipboardService : IClipboardService
{
    private string? _clips;

    /// <inheritdoc />
    public void SetClips(string json) => _clips = json;

    /// <inheritdoc />
    public string? GetClips() => _clips;
}
