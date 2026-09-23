using System.Windows;
using JazzHands.App.ViewModels.Media;
using JazzHands.App.Views.Media;
using Microsoft.Win32;

namespace JazzHands.App.Services;

/// <summary>Opens the application's dialogs. Views never open a window themselves.</summary>
public interface IDialogService
{
    /// <summary>Shows the import dialog for a set of chosen paths.</summary>
    Task ShowImportAsync(IReadOnlyList<string> paths);
}

/// <summary>Asks the user for files. Separate from <see cref="IDialogService"/> because it is the shell asking the operating system, not us.</summary>
public interface IFileDialogService
{
    /// <summary>Picks media files to import. Empty when the user cancelled.</summary>
    IReadOnlyList<string> OpenMedia();
}

/// <summary>The real dialogs.</summary>
/// <param name="importFactory">Makes an import viewmodel per dialog, since each one has its own list.</param>
public sealed class DialogService(Func<ImportViewModel> importFactory) : IDialogService
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
}

/// <summary>The real file picker.</summary>
public sealed class FileDialogService : IFileDialogService
{
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
}
