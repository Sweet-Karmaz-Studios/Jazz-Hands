using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using Serilog;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Export;

/// <summary>
/// The export dialog: where the file goes, which preset, copy or encode, and what the planner
/// will do about it and why.
/// </summary>
/// <remarks>
/// Every change asks <c>export.plan</c> again and shows the answer, so the person sees "copying,
/// two cuts moved to keyframes" or "encoding, because the preset writes 480p" before anything is
/// written, in the planner's own words. Export queues the job with <c>export.enqueue</c>, the
/// same command the CLI and MCP send; the dialog never plans or exports by itself.
///
/// A Quick Trim opens in Copy with snapping on, because that is what Quick Trim is for; anything
/// else opens in Auto.
/// </remarks>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    private readonly ILogger _log = Log.ForContext<ExportDialogViewModel>();
    private readonly ISession _session;
    private readonly IFileDialogService _files;
    private readonly IUiDispatcher _ui;
    private int _planVersion;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private ExportPresetInfo? _preset;

    [ObservableProperty]
    private ExportMode _mode = ExportMode.Auto;

    [ObservableProperty]
    private bool _snapToKeyframes;

    [ObservableProperty]
    private bool _useInOut;

    [ObservableProperty]
    private bool _useExternalFfmpeg;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _isPlanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private ExportPlan? _plan;

    /// <summary>Creates the dialog's viewmodel.</summary>
    public ExportDialogViewModel(ISession session, IFileDialogService files, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _files = files;
        _ui = ui;
        _preset = Presets[0];
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The sequence being exported.</summary>
    public string? SequenceId { get; private set; }

    /// <summary>What the dialog is exporting, for its heading.</summary>
    public string Heading { get; private set; } = "Export";

    /// <summary>Every preset.</summary>
    public IReadOnlyList<ExportPresetInfo> Presets { get; } = [.. ExportPresets.All];

    /// <summary>The three modes, in the order the dialog offers them.</summary>
    public IReadOnlyList<ExportMode> Modes { get; } = [ExportMode.Auto, ExportMode.Copy, ExportMode.Encode];

    /// <summary>Why the planner chose what it chose, one sentence each.</summary>
    public ObservableCollection<string> Reasons { get; } = [];

    /// <summary>True for a Quick Trim, which is exported by copy unless told otherwise.</summary>
    public bool IsQuickTrim { get; private set; }

    /// <summary>What each mode means, for the dialog to show beside the choice.</summary>
    public string ModeExplanation => Mode switch
    {
        ExportMode.Copy => "Copy: the source's own packets, untouched. No quality lost and very fast, but cuts can only fall on keyframes and nothing can be changed.",
        ExportMode.Encode => "Encode: every frame rendered and compressed again. Cuts are exact and anything on the timeline is kept, at the cost of time and a generation of quality.",
        _ => "Auto: copy when the timeline plays one file untouched and the preset would write what the source already is; encode otherwise.",
    };

    /// <summary>Fills the dialog for a sequence, or the active one.</summary>
    public void Load(string? sequenceId)
    {
        Project project = _session.Project;
        Sequence? sequence = (sequenceId is null ? null : project.Sequence(sequenceId)) ?? project.ActiveSequence;
        SequenceId = sequence?.Id;
        IsQuickTrim = sequence?.QuickTrim is not null;
        Heading = sequence is null ? "Export" : $"Export {sequence.Name}";

        OutputPath = SuggestedPath(project, sequence);
        Mode = IsQuickTrim ? ExportMode.Copy : ExportMode.Auto;
        SnapToKeyframes = IsQuickTrim;

        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(IsQuickTrim));
        RefreshPlan();
    }

    /// <summary>Asks the planner again, off the UI thread: a first plan may scan a file for keyframes.</summary>
    public Task RefreshPlanAsync()
    {
        int version = ++_planVersion;
        var query = new PlanExportQuery(OutputPath, Preset?.Name ?? ExportPresets.Default, Mode, SequenceId, SnapToKeyframes, UseInOut, UseExternalFfmpeg);
        IsPlanning = true;

        return Task.Run(() =>
        {
            ExportPlan? plan = null;
            string error = string.Empty;

            try
            {
                plan = _session.Query(query);
            }
            catch (CommandException refusal)
            {
                error = refusal.Message;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A file that cannot be read shows as a sentence in the dialog, not a crash.
                _log.Warning(failure, "Planning the export failed");
                error = failure.Message;
            }

            _ui.Post(() =>
            {
                if (version != _planVersion)
                {
                    return;
                }

                Show(plan, error);
            });
        });
    }

    partial void OnOutputPathChanged(string value) => RefreshPlan();

    partial void OnPresetChanged(ExportPresetInfo? value)
    {
        // Keep the file name and change the extension to the one the preset writes.
        if (value is not null && OutputPath.Length > 0 && !string.Equals(Path.GetExtension(OutputPath), value.Extension, StringComparison.OrdinalIgnoreCase))
        {
            OutputPath = Path.ChangeExtension(OutputPath, value.Extension);
            return;
        }

        RefreshPlan();
    }

    partial void OnModeChanged(ExportMode value)
    {
        OnPropertyChanged(nameof(ModeExplanation));
        RefreshPlan();
    }

    partial void OnSnapToKeyframesChanged(bool value) => RefreshPlan();

    partial void OnUseInOutChanged(bool value) => RefreshPlan();

    partial void OnUseExternalFfmpegChanged(bool value) => RefreshPlan();

    [RelayCommand]
    private void Browse()
    {
        if (_files.SaveExport(OutputPath) is { } chosen)
        {
            OutputPath = chosen;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        CommandResult result = await _session.ExecuteAsync(new EnqueueExportCommand(
            OutputPath,
            Preset?.Name ?? ExportPresets.Default,
            Mode,
            SequenceId,
            SnapToKeyframes,
            UseInOut,
            UseExternalFfmpeg)).ConfigureAwait(true);

        if (result.Ok)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Error = result.Error ?? result.Code ?? "The export could not be queued.";
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private bool CanExport() => Plan is not null;

    private void RefreshPlan() => _ = RefreshPlanAsync();

    private void Show(ExportPlan? plan, string error)
    {
        IsPlanning = false;
        Plan = plan;
        Error = error;
        Reasons.Clear();

        if (plan is null)
        {
            Summary = string.Empty;
            return;
        }

        foreach (string reason in plan.Reasons)
        {
            Reasons.Add(reason);
        }

        foreach (KeyframeSnap snap in plan.Snaps)
        {
            Reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The {snap.Edge} at {Timecode.FormatClock(snap.Requested)} moves to the keyframe at {Timecode.FormatClock(snap.Snapped)} (frame {snap.Frame})."));
        }

        string what = plan.Mode == ExportMode.Copy
            ? $"Copy, {(plan.Copy!.AudioStreams.IsEmpty ? "no sound" : $"{plan.Copy.AudioStreams.Length} sound stream(s)")}"
            : $"Encode {plan.Video!.Width}x{plan.Video.Height} {plan.Video.Codec}{(plan.Audio is null ? string.Empty : $" with {plan.Audio.Encoder}")}";

        Summary = $"{what}, {Timecode.FormatClock(plan.Duration)}, to {Path.GetFileName(plan.OutputPath)}";
    }

    private string SuggestedPath(Project project, Sequence? sequence)
    {
        // Beside the recording for a Quick Trim, so the cut lands where the capture is.
        if (sequence?.QuickTrim is { } trim && project.MediaItem(trim.MediaId) is { } media)
        {
            string file = Path.IsPathRooted(media.RelativePath) || _session.ProjectPath.Length == 0
                ? Path.GetFullPath(media.RelativePath)
                : ProjectPaths.Resolve(_session.ProjectPath, media.RelativePath);
            return Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, $"{media.Name} trim.mp4");
        }

        string name = sequence?.Name is { Length: > 0 } given ? given : project.Name;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), $"{name}.mp4");
    }
}
