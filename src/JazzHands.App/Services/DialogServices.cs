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

    /// <summary>Asks whether to save a project with unsaved changes before it is closed.</summary>
    Task<SaveChoice> AskToSaveAsync(string projectName);

    /// <summary>Offers unsaved work a crash left: true to bring it back, false to keep what was saved and set it aside.</summary>
    Task<bool> AskToRecoverAsync(string description);

    /// <summary>Asks what a new project should be: its name and its frame rate and size.</summary>
    Task<NewProjectChoice?> ShowNewProjectAsync();

    /// <summary>Shows the Settings dialog, on a page when one is named (keymap).</summary>
    Task ShowSettingsAsync(string? page = null);

    /// <summary>Asks for a line of text, such as a name; null when cancelled.</summary>
    Task<string?> AskForTextAsync(string title, string prompt, string initial);

    /// <summary>Asks what to do about exports still running when the person quits.</summary>
    Task<Shell.QuitChoice> AskToQuitWithExportsAsync(int running);

    /// <summary>Shows the missing media, with the files that might be them, to find or relink.</summary>
    Task ShowMissingMediaAsync();

    /// <summary>Asks where and how to gather the project and its media: a folder, or a zip when archiving.</summary>
    Task ShowConsolidateAsync(bool archive);
}

/// <summary>What to do with unsaved changes.</summary>
public enum SaveChoice
{
    /// <summary>Save them first.</summary>
    Save,

    /// <summary>Throw them away.</summary>
    Discard,

    /// <summary>Do not go on.</summary>
    Cancel,
}

/// <summary>A new project, as the New Project dialog chose it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Fps">Its frame rate.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
public sealed record NewProjectChoice(string Name, JazzHands.Core.Time.Rational Fps, int Width, int Height);

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

    /// <summary>Asks where a <c>jazz apply</c> script goes, starting from a suggestion, or null when the user cancelled.</summary>
    string? SaveScript(string suggested);

    /// <summary>Picks a keymap file to import, or null when the user cancelled.</summary>
    string? OpenKeymap();

    /// <summary>Asks where to export the keymap, or null when the user cancelled.</summary>
    string? SaveKeymap(string suggested);

    /// <summary>Picks a project to open, or null when the user cancelled.</summary>
    string? OpenProject();

    /// <summary>Asks where to save a project, starting from a suggestion, or null when the user cancelled.</summary>
    string? SaveProject(string suggested);

    /// <summary>Picks a folder, or null when the user cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Asks where an archive zip goes, or null when the user cancelled.</summary>
    string? SaveArchive(string suggested);
}

/// <summary>The real dialogs.</summary>
/// <param name="importFactory">Makes an import viewmodel per dialog, since each one has its own list.</param>
/// <param name="exportFactory">Makes an export viewmodel per dialog.</param>
/// <param name="settingsFactory">Makes the Settings dialog's viewmodel, fresh each time so it reads the files again.</param>
/// <param name="missingFactory">Makes the missing media dialog's viewmodel.</param>
/// <param name="consolidateFactory">Makes the Consolidate and Archive dialog's viewmodel.</param>
public sealed class DialogService(
    Func<ImportViewModel> importFactory,
    Func<ExportDialogViewModel>? exportFactory = null,
    Func<ViewModels.Settings.SettingsViewModel>? settingsFactory = null,
    Func<MissingMediaViewModel>? missingFactory = null,
    Func<ConsolidateViewModel>? consolidateFactory = null) : IDialogService
{
    /// <inheritdoc />
    public async Task ShowMissingMediaAsync()
    {
        if (missingFactory is null)
        {
            return;
        }

        MissingMediaViewModel viewModel = missingFactory();
        var window = new MissingMediaWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Loaded += async (_, _) => await viewModel.LoadAsync().ConfigureAwait(true);
        window.ShowDialog();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task ShowConsolidateAsync(bool archive)
    {
        if (consolidateFactory is null)
        {
            return Task.CompletedTask;
        }

        ConsolidateViewModel viewModel = consolidateFactory();
        viewModel.Load(archive);
        var window = new ConsolidateWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
        return Task.CompletedTask;
    }

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

    /// <inheritdoc />
    public Task<SaveChoice> AskToSaveAsync(string projectName)
    {
        MessageBoxResult answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"Save the changes to {projectName} first?",
            "Jazz Hands",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return Task.FromResult(answer switch
        {
            MessageBoxResult.Yes => SaveChoice.Save,
            MessageBoxResult.No => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        });
    }

    /// <inheritdoc />
    public Task<bool> AskToRecoverAsync(string description)
    {
        MessageBoxResult answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{description}\n\nBring the unsaved work back? It opens unsaved; the file is untouched until you save.\n\n"
            + @"No keeps the project as it was saved, and sets the unsaved work aside in the project's .jazz.d\recovered folder.",
            "Recover unsaved work",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    /// <inheritdoc />
    public Task<Shell.QuitChoice> AskToQuitWithExportsAsync(int running)
    {
        var window = new Views.Shell.QuitWindow(running) { Owner = Application.Current?.MainWindow };
        _ = window.ShowDialog();
        return Task.FromResult(window.Choice);
    }

    /// <inheritdoc />
    public Task<string?> AskForTextAsync(string title, string prompt, string initial)
    {
        var window = new Views.Settings.TextPromptWindow(title, prompt, initial) { Owner = Application.Current?.MainWindow };
        return Task.FromResult(window.ShowDialog() == true ? window.Answer : null);
    }

    /// <inheritdoc />
    public Task<NewProjectChoice?> ShowNewProjectAsync()
    {
        var viewModel = new ViewModels.Settings.NewProjectViewModel();
        var window = new Views.Settings.NewProjectWindow
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
        return Task.FromResult(viewModel.Choice);
    }

    /// <inheritdoc />
    public Task ShowSettingsAsync(string? page = null)
    {
        if (settingsFactory is null)
        {
            return Task.CompletedTask;
        }

        ViewModels.Settings.SettingsViewModel viewModel = settingsFactory();
        var window = new Views.Settings.SettingsWindow
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        if (page == "keymap")
        {
            window.ShowKeymap();
        }

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

    /// <inheritdoc />
    public string? OpenProject()
    {
        var dialog = new OpenFileDialog { Title = "Open a project", CheckFileExists = true, Filter = "Jazz Hands project|*.jazz|Everything|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SaveProject(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the project as",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            DefaultExt = ".jazz",
            Filter = "Jazz Hands project|*.jazz",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? SaveArchive(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Archive the project to",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            DefaultExt = ".zip",
            Filter = "Zip archive|*.zip",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? OpenKeymap()
    {
        var dialog = new OpenFileDialog { Title = "Import a keymap", Filter = "Keymap|*.json" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SaveKeymap(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export the keymap",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            Filter = "Keymap|*.json",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? SaveScript(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the commands as a script",
            FileName = System.IO.Path.GetFileName(suggested),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggested),
            Filter = "Jazz script|*.json",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
