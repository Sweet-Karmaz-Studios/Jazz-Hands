using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using Serilog;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Media;

/// <summary>One line in the import dialog's warning list.</summary>
/// <param name="File">Which file it is about.</param>
/// <param name="Code">The machine readable code, the same one <c>jazz media probe</c> prints.</param>
/// <param name="Message">What it means.</param>
public sealed record ImportWarningViewModel(string File, string Code, string Message)
{
    /// <summary>True for the codes that mean something will not work, rather than will look odd.</summary>
    public bool IsSerious => Code is "no-streams" or "cannot-read";
}

/// <summary>
/// The import dialog: what is about to come in, how it will be conformed, and what is odd about
/// it.
/// </summary>
/// <remarks>
/// The warnings are what <c>media.probe</c> says, not a second opinion computed here. Changing a
/// conform setting re-reads them, because a warning's wording depends on the setting: an
/// interlaced file reads differently when deinterlacing is off. The probe results are cached by
/// content hash, so re-reading after a settings change costs nothing.
/// </remarks>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly ILogger _log = Log.ForContext<ImportViewModel>();
    private readonly ISession _session;
    private readonly List<string> _paths = [];

    [ObservableProperty]
    private ConformPolicy _conform = ConformPolicy.Fit;

    [ObservableProperty]
    private AutoSetting _deinterlace = AutoSetting.Auto;

    [ObservableProperty]
    private AutoSetting _vfrConform = AutoSetting.Auto;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private bool _recursive;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasImported;

    /// <summary>Creates the dialog's viewmodel.</summary>
    public ImportViewModel(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The files about to be imported.</summary>
    public ObservableCollection<string> Files { get; } = [];

    /// <summary>What the probe found worth mentioning.</summary>
    public ObservableCollection<ImportWarningViewModel> Warnings { get; } = [];

    /// <summary>The conform policies, for the combo box.</summary>
    public static IReadOnlyList<ConformPolicy> ConformPolicies { get; } = Enum.GetValues<ConformPolicy>();

    /// <summary>The three-way settings, for the combo boxes.</summary>
    public static IReadOnlyList<AutoSetting> AutoSettings { get; } = Enum.GetValues<AutoSetting>();

    /// <summary>A one line summary of the warnings, for the header.</summary>
    public string WarningSummary => Warnings.Count switch
    {
        0 => "Nothing looks unusual.",
        1 => "One thing is worth knowing before importing.",
        int many => $"{many} things are worth knowing before importing.",
    };

    /// <summary>Takes the chosen paths and reads them.</summary>
    public void Load(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths.Clear();
        _paths.AddRange(paths);

        Files.Clear();
        foreach (string path in paths)
        {
            Files.Add(path);
        }

        Probe();
    }

    /// <summary>
    /// Re-reads the chosen files and rebuilds the warning list.
    /// </summary>
    /// <remarks>
    /// Goes through the <c>media.probe</c> query so the dialog and the command line agree about
    /// what a file is. A file that cannot be read becomes a warning rather than an exception: the
    /// point of the dialog is to say what will happen, and "this one will not import" is part of
    /// that.
    /// </remarks>
    public void Probe()
    {
        Warnings.Clear();

        foreach (string path in _paths)
        {
            try
            {
                MediaProbeInfo probe = _session.Query(
                    new ProbeMediaQuery(path, Conform, Deinterlace, VfrConform));

                foreach (ProbeWarning warning in probe.Warnings)
                {
                    Warnings.Add(new ImportWarningViewModel(Path.GetFileName(path), warning.Code, warning.Message));
                }
            }
            catch (CommandException error)
            {
                Warnings.Add(new ImportWarningViewModel(Path.GetFileName(path), error.Code, error.Message));
                _log.Information("{Path} could not be probed for the import dialog: {Message}", path, error.Message);
            }
        }

        OnPropertyChanged(nameof(WarningSummary));
    }

    /// <inheritdoc />
    partial void OnConformChanged(ConformPolicy value) => Probe();

    /// <inheritdoc />
    partial void OnDeinterlaceChanged(AutoSetting value) => Probe();

    /// <inheritdoc />
    partial void OnVfrConformChanged(AutoSetting value) => Probe();

    [RelayCommand]
    private async Task ImportAsync()
    {
        IsBusy = true;

        try
        {
            var command = new AddMediaCommand(
                new EquatableArray<string>([.. _paths]),
                Folder,
                new EquatableArray<string>([.. Tags
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]),
                Color: string.Empty,
                Conform: Conform,
                Deinterlace: Deinterlace,
                VfrConform: VfrConform,
                Recursive: Recursive);

            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);

            if (result.Ok)
            {
                HasImported = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Status = result.Error ?? "The import failed.";
        }
        catch (Exception error) when (error is CommandException or InvalidOperationException)
        {
            Status = error.Message;
            _log.Warning(error, "Import failed from the dialog");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
