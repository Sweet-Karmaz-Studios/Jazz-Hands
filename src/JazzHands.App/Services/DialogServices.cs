using System.Windows;
using JazzHands.App.ViewModels.Export;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.Views.Export;
using JazzHands.App.Views.Media;
using Microsoft.Win32;

namespace JazzHands.App.Services;

/// <summary>Opens the application's dialogs. Views never open a window themselves.</summary>
public interface IDialogService
{
    /// <summary>Shows the import dialog for a set of chosen paths.</summary>
    Task ShowImportAsync(IReadOnlyList<string> paths);

    /// <summary>Shows the export dialog for a sequence, or the active one.</summary>
    Task ShowExportAsync(string? sequenceId);
}

/// <summary>Asks the user for files. Separate from <see cref="IDialogService"/> because it is the shell asking the operating system, not us.</summary>
public interface IFileDialogService
{
    /// <summary>Picks media files to import. Empty when the user cancelled.</summary>
    IReadOnlyList<string> OpenMedia();

    /// <summary>Picks one recording to Quick Trim, or null when the user cancelled.</summary>
    string? OpenMovie();

    /// <summary>Asks where an export goes, starting from a suggestion, or null when the user cancelled.</summary>
    string? SaveExport(string suggested);

    /// <summary>Picks a subtitle file to import, or null when the user cancelled.</summary>
    string? OpenSubtitles();

    /// <summary>Asks where a subtitle file goes, starting from a suggestion, or null when the user cancelled.</summary>
    string? SaveSubtitles(string suggested);
}

/// <summary>The real dialogs.</summary>
/// <param name="importFactory">Makes an import viewmodel per dialog, since each one has its own list.</param>
/// <param name="exportFactory">Makes an export viewmodel per dialog.</param>
public sealed class DialogService(Func<ImportViewModel> importFactory, Func<ExportDialogViewModel>? exportFactory = null) : IDialogService
{
    /// <inheritdoc />
    public Task ShowImportAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ImportViewModel viewModel = importFactory();
        viewModel.Load(paths);

        var window = new ImportWindow
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowExportAsync(string? sequenceId)
    {
        if (exportFactory is null)
        {
            return Task.CompletedTask;
        }

        ExportDialogViewModel viewModel = exportFactory();
        viewModel.Load(sequenceId);

        var window = new ExportWindow
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();

        return Task.CompletedTask;
    }
}

/// <summary>The real file picker.</summary>
public sealed class FileDialogService : IFileDialogService
{
    private const string Movies = "Recordings|*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.mxf|Everything|*.*";

    /// <inheritdoc />
    public IReadOnlyList<string> OpenMedia()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import media",
            Multiselect = true,
            CheckFileExists = true,
            Filter =
                "Media|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mxf;*.wav;*.mp3;*.flac;*.aac;*.png;*.jpg;*.jpeg;*.exr;*.tif;*.tiff;*.dpx" +
                "|Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mxf" +
                "|Audio|*.wav;*.mp3;*.flac;*.aac;*.m4a" +
                "|Images|*.png;*.jpg;*.jpeg;*.exr;*.tif;*.tiff;*.dpx" +
                "|Everything|*.*",
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    /// <inheritdoc />
    public string? OpenMovie()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Quick Trim a recording",
            CheckFileExists = true,
            Filter = Movies,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SaveExport(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export to",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            Filter = "MP4|*.mp4|Matroska|*.mkv|QuickTime|*.mov",
            FilterIndex = System.IO.Path.GetExtension(suggested).ToLowerInvariant() switch
            {
                ".mkv" => 2,
                ".mov" => 3,
                _ => 1,
            },
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? OpenSubtitles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import subtitles",
            CheckFileExists = true,
            Filter = "Subtitles|*.srt;*.vtt;*.ass;*.ssa|SubRip|*.srt|WebVTT|*.vtt|ASS|*.ass;*.ssa",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SaveSubtitles(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export subtitles to",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            Filter = "SubRip|*.srt|WebVTT|*.vtt|ASS|*.ass",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
