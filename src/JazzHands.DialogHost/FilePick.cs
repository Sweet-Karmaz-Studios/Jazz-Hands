using System.Text.Json;
using Microsoft.Win32;

namespace JazzHands.DialogHost;

/// <summary>What kind of file dialog.</summary>
public enum FilePickKind
{
    /// <summary>Open one file.</summary>
    Open,

    /// <summary>Open one file or several.</summary>
    OpenMany,

    /// <summary>Choose where to save a file.</summary>
    Save,

    /// <summary>Choose a folder.</summary>
    Folder,
}

/// <summary>
/// One Windows file dialog, as the editor asks <c>jazz-dialog.exe</c> for it: the same settings
/// Microsoft.Win32's dialogs take, carried as JSON on the command line.
/// </summary>
/// <param name="Kind">Open, several, save or a folder.</param>
/// <param name="Title">The dialog's title.</param>
/// <param name="Filter">The file types, in the Win32 filter form (<c>MP4|*.mp4|Everything|*.*</c>).</param>
/// <param name="FilterIndex">Which of them is chosen at first, from 1.</param>
/// <param name="InitialDirectory">The folder it opens in.</param>
/// <param name="FileName">The name it suggests.</param>
/// <param name="DefaultExt">The extension added to a name typed without one.</param>
/// <param name="OverwritePrompt">For a save, ask before replacing a file.</param>
/// <param name="CheckFileExists">For an open, only files that exist.</param>
public sealed record FilePick(
    FilePickKind Kind,
    string Title,
    string? Filter = null,
    int FilterIndex = 1,
    string? InitialDirectory = null,
    string? FileName = null,
    string? DefaultExt = null,
    bool OverwritePrompt = true,
    bool CheckFileExists = true)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The exit code for a choice made; the paths are on standard output as a JSON array.</summary>
    public const int Chosen = 0;

    /// <summary>The exit code for a dialog cancelled.</summary>
    public const int Cancelled = 1;

    /// <summary>The exit code for a request that could not be read.</summary>
    public const int BadRequest = 2;

    /// <summary>The request as one command line argument.</summary>
    public string ToArgument() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, Json));

    /// <summary>A request from its command line argument, or null.</summary>
    public static FilePick? FromArgument(string argument)
    {
        try
        {
            return JsonSerializer.Deserialize<FilePick>(Convert.FromBase64String(argument), Json);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The chosen paths as the helper writes them.</summary>
    public static string WritePaths(IReadOnlyList<string> paths) => JsonSerializer.Serialize(paths, Json);

    /// <summary>The chosen paths as the editor reads them.</summary>
    public static IReadOnlyList<string> ReadPaths(string text) =>
        JsonSerializer.Deserialize<string[]>(text, Json) ?? [];

    /// <summary>
    /// Shows the dialog in this process, owned by <paramref name="owner"/>: what the helper does,
    /// and what the editor does itself when the helper is not there. Null when cancelled.
    /// </summary>
    public IReadOnlyList<string>? Show(System.Windows.Window? owner)
    {
        switch (Kind)
        {
            case FilePickKind.Folder:
            {
                var folder = new OpenFolderDialog { Title = Title, InitialDirectory = InitialDirectory ?? string.Empty };
                return Owned(folder, owner) ? [folder.FolderName] : null;
            }

            case FilePickKind.Save:
            {
                var save = new SaveFileDialog
                {
                    Title = Title,
                    Filter = Filter ?? string.Empty,
                    FilterIndex = FilterIndex,
                    InitialDirectory = InitialDirectory ?? string.Empty,
                    FileName = FileName ?? string.Empty,
                    DefaultExt = DefaultExt ?? string.Empty,
                    OverwritePrompt = OverwritePrompt,
                };
                return Owned(save, owner) ? [save.FileName] : null;
            }

            default:
            {
                var open = new OpenFileDialog
                {
                    Title = Title,
                    Filter = Filter ?? string.Empty,
                    FilterIndex = FilterIndex,
                    InitialDirectory = InitialDirectory ?? string.Empty,
                    FileName = FileName ?? string.Empty,
                    Multiselect = Kind == FilePickKind.OpenMany,
                    CheckFileExists = CheckFileExists,
                };
                return Owned(open, owner) ? open.FileNames : null;
            }
        }
    }

    private static bool Owned(CommonDialog dialog, System.Windows.Window? owner) =>
        (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true;
}
