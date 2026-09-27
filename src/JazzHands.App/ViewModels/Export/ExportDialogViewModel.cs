using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Settings;
using Serilog;
using Path = System.IO.Path;

namespace JazzHands.App.ViewModels.Export;

/// <summary>
/// The export dialog: where the file goes, which preset and what to change about it, copy or
/// encode, and what the planner will do about it and why.
/// </summary>
/// <remarks>
/// Every change asks <c>export.plan</c> again and shows the answer, so the person sees "copying,
/// two cuts moved to keyframes" or "encoding, because the preset writes 480p", and about how big
/// and how long, before anything is written, in the planner's own words. The overrides are the
/// same text the command line takes (<c>--size 1280x720</c>, <c>--bitrate 8M</c>), passed through
/// as typed, so a value the planner will not take is refused in its words too. Add to queue sends
/// <c>export.enqueue</c>, the same command the CLI and MCP send; Export now sends it at high
/// priority, so it starts ahead of anything waiting. The dialog never plans or exports by itself.
///
/// A Quick Trim opens in Smart cut, exact and all but lossless, because that is what Quick Trim is
/// for; when the source cannot be smart cut it falls back to Copy with snapping on. Anything else
/// opens in Auto. The mode list says beside Smart cut and Copy whether each would work now, and
/// why not when it would not.
/// </remarks>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    private readonly ILogger _log = Log.ForContext<ExportDialogViewModel>();
    private readonly ISession _session;
    private readonly IFileDialogService _files;
    private readonly IUiDispatcher _ui;
    private readonly EditorSettings _defaults;
    private int _planVersion;
    private bool _trimFallback;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private ExportPresetSummary? _preset;

    [ObservableProperty]
    private ExportMode _mode = ExportMode.Auto;

    [ObservableProperty]
    private bool _snapToKeyframes;

    [ObservableProperty]
    private bool _useInOut;

    [ObservableProperty]
    private bool _useExternalFfmpeg;

    [ObservableProperty]
    private SubtitleDelivery _subtitles = SubtitleDelivery.Soft;

    [ObservableProperty]
    private bool _chapters = true;

    [ObservableProperty]
    private bool _showOverrides;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _frameRateText = string.Empty;

    [ObservableProperty]
    private string _qualityText = string.Empty;

    [ObservableProperty]
    private string _bitrateText = string.Empty;

    [ObservableProperty]
    private string _encodersText = string.Empty;

    [ObservableProperty]
    private string _pixelFormatText = string.Empty;

    [ObservableProperty]
    private string _audioEncoderText = string.Empty;

    [ObservableProperty]
    private string _audioBitrateText = string.Empty;

    [ObservableProperty]
    private ChannelChoice _channels = ChannelChoices[0];

    [ObservableProperty]
    private bool _normalise;

    /// <summary>Write the sound alone, in a sound file for the preset's encoder.</summary>
    [ObservableProperty]
    private bool _soundOnly;

    [ObservableProperty]
    private string _loudnessText = "-14";

    [ObservableProperty]
    private string _targetSizeText = string.Empty;

    [ObservableProperty]
    private string _startText = string.Empty;

    [ObservableProperty]
    private string _endText = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _estimate = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _isPlanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(Hints))]
    [NotifyCanExecuteChangedFor(nameof(ExportNowCommand))]
    private ExportPlan? _plan;

    /// <summary>What each empty override box stands for: the plan's own value, shown in the box.</summary>
    public ExportHints Hints => ExportHints.Of(Plan);

    /// <summary>Creates the dialog's viewmodel.</summary>
    /// <param name="session">The session whose sequence is exported.</param>
    /// <param name="files">The file pickers.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="editor">Settings, Export: the preset and folder to start from; the built-in defaults when null.</param>
    public ExportDialogViewModel(ISession session, IFileDialogService files, IUiDispatcher ui, SettingsSection<EditorSettings>? editor = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _files = files;
        _ui = ui;
        Presets = session.Query(new ListPresetsQuery());
        _defaults = editor?.Current ?? new EditorSettings();
        _preset = Presets.FirstOrDefault(preset => string.Equals(preset.Name, _defaults.ExportPreset, StringComparison.OrdinalIgnoreCase))
            ?? Presets.FirstOrDefault(preset => preset.Name == ExportPresets.Default)
            ?? Presets[0];
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>What subtitle tracks can become, in words, for the dialog's picker.</summary>
    public static IReadOnlyList<SubtitleChoice> SubtitleChoices { get; } =
    [
        new(SubtitleDelivery.Soft, "In the file, to turn on and off"),
        new(SubtitleDelivery.Burn, "Burned into the picture"),
        new(SubtitleDelivery.Sidecar, "As files beside it"),
        new(SubtitleDelivery.None, "Left out"),
    ];

    /// <summary>Channel counts the export can be written in.</summary>
    public static IReadOnlyList<ChannelChoice> ChannelChoices { get; } =
    [
        new(0, "The preset's"),
        new(1, "Mono"),
        new(2, "Stereo"),
        new(6, "5.1"),
    ];

    /// <summary>Sizes offered in the size box; any other can be typed.</summary>
    public static IReadOnlyList<string> SizeSuggestions { get; } = ["3840x2160", "2560x1440", "1920x1080", "1280x720", "854x480"];

    /// <summary>Rates offered in the rate box; any other can be typed.</summary>
    public static IReadOnlyList<string> FrameRateSuggestions { get; } = ["60", "50", "30", "30000/1001", "25", "24", "15"];

    /// <summary>Sound encoders offered in the encoder box.</summary>
    public static IReadOnlyList<string> AudioEncoderSuggestions { get; } = ["aac", "libopus", "flac", "eac3", "ac3", "libmp3lame", "pcm_s24le", "pcm_s16le"];

    /// <summary>Pixel formats offered in the colour box.</summary>
    public static IReadOnlyList<string> PixelFormatSuggestions { get; } = ["yuv420p", "yuv420p10le", "yuv422p10le"];

    /// <summary>The sequence being exported.</summary>
    public string? SequenceId { get; private set; }

    /// <summary>What the dialog is exporting, for its heading.</summary>
    public string Heading { get; private set; } = "Export";

    /// <summary>Every preset, built in and the person's own.</summary>
    public IReadOnlyList<ExportPresetSummary> Presets { get; }

    /// <summary>The four modes, in the order the dialog offers them.</summary>
    public IReadOnlyList<ExportMode> Modes { get; } = [ExportMode.Auto, ExportMode.Smart, ExportMode.Copy, ExportMode.Encode];

    /// <summary>The modes with whether each would work now and, when not, why: refreshed with every plan.</summary>
    public IReadOnlyList<ExportModeChoice> ModeChoices { get; } =
    [
        new(ExportMode.Auto, "Auto"),
        new(ExportMode.Smart, "Smart cut"),
        new(ExportMode.Copy, "Copy"),
        new(ExportMode.Encode, "Encode"),
    ];

    /// <summary>Why the planner chose what it chose, one sentence each.</summary>
    public ObservableCollection<string> Reasons { get; } = [];

    /// <summary>True for a Quick Trim, which is smart cut, or copied when it cannot be, unless told otherwise.</summary>
    public bool IsQuickTrim { get; private set; }

    /// <summary>What each mode means, for the dialog to show beside the choice.</summary>
    public string ModeExplanation => Mode switch
    {
        ExportMode.Smart => "Smart cut: the source's own packets between the cuts, and only the frames from each cut to the next keyframe encoded again, with an encoder matched to the source. Cuts are exact and almost nothing is lost.",
        ExportMode.Copy => "Copy: the source's own packets, untouched. No quality lost and very fast, but cuts can only fall on keyframes and nothing can be changed.",
        ExportMode.Encode => "Encode: every frame rendered and compressed again. Cuts are exact and anything on the timeline is kept, at the cost of time and a generation of quality.",
        _ => "Auto: copy when the timeline plays one file untouched and the preset would write what the source already is, smart cut when the cuts are off keyframes; encode otherwise.",
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
        // A Quick Trim opens in Smart cut; when the source cannot be smart cut, the first plan
        // says so and the dialog falls back to Copy with snapping.
        _trimFallback = IsQuickTrim;
        Mode = IsQuickTrim ? ExportMode.Smart : ExportMode.Auto;
        SnapToKeyframes = IsQuickTrim;

        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(IsQuickTrim));
        RefreshPlan();
    }

    /// <summary>Asks the planner again, off the UI thread: a first plan may scan a file for keyframes.</summary>
    public Task RefreshPlanAsync()
    {
        int version = ++_planVersion;
        IsPlanning = true;
        PlanExportQuery? query = null;
        string error = string.Empty;

        try
        {
            query = Query();
        }
        catch (CommandException refusal)
        {
            // A value that does not read, such as a size of "big", is said here and not planned.
            error = refusal.Message;
        }

        if (query is null)
        {
            Show(null, error);
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            (ExportPlan? plan, string failure, string code) = Ask(query);

            // Whether the two modes that depend on the source would work, for the mode list.
            var notes = new Dictionary<ExportMode, (bool Available, string Note)>();
            foreach (ExportMode other in (ExportMode[])[ExportMode.Smart, ExportMode.Copy])
            {
                (ExportPlan? tried, string why, _) = other == query.Mode ? (plan, failure, code) : Ask(query with { Mode = other });
                notes[other] = tried is null ? (false, why) : (true, Availability(tried));
            }

            _ui.Post(() =>
            {
                if (version != _planVersion)
                {
                    return;
                }

                foreach (ExportModeChoice choice in ModeChoices)
                {
                    (bool available, string note) = notes.TryGetValue(choice.Mode, out var known) ? known : (true, string.Empty);
                    choice.IsAvailable = available;
                    choice.Note = note;
                }

                bool fallBack = _trimFallback && plan is null && code == "cannot-smart-cut" && Mode == ExportMode.Smart;
                _trimFallback = false;
                if (fallBack)
                {
                    // Setting the mode plans again.
                    Mode = ExportMode.Copy;
                    return;
                }

                Show(plan, failure);
            });
        });
    }

    /// <summary>What the planner says to a query: a plan, or why not and the refusal's code.</summary>
    private (ExportPlan? Plan, string Failure, string Code) Ask(PlanExportQuery query)
    {
        try
        {
            return (_session.Query(query), string.Empty, string.Empty);
        }
        catch (CommandException refusal)
        {
            return (null, refusal.Message, refusal.Code);
        }
        catch (Exception problem) when (problem is not OutOfMemoryException)
        {
            // A file that cannot be read shows as a sentence in the dialog, not a crash.
            _log.Warning(problem, "Planning the export failed");
            return (null, problem.Message, string.Empty);
        }
    }

    /// <summary>A few words on what a mode would do, beside it in the list.</summary>
    private static string Availability(ExportPlan plan) => plan switch
    {
        { Smart: { } smart } => string.Create(CultureInfo.InvariantCulture, $"Exact cuts, {Words.Count(smart.EncodedFrames, "frame")} encoded again."),
        { Mode: ExportMode.Copy, Snaps.Length: > 0 } => string.Create(CultureInfo.InvariantCulture, $"{Words.Count(plan.Snaps.Length, "cut")} move to keyframes."),
        { Mode: ExportMode.Copy } => "Every cut is on a keyframe.",
        _ => string.Empty,
    };

    partial void OnOutputPathChanged(string value) => RefreshPlan();

    partial void OnPresetChanged(ExportPresetSummary? value)
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

    partial void OnSubtitlesChanged(SubtitleDelivery value) => RefreshPlan();

    partial void OnChaptersChanged(bool value) => RefreshPlan();

    partial void OnSizeTextChanged(string value) => RefreshPlan();

    partial void OnFrameRateTextChanged(string value) => RefreshPlan();

    partial void OnQualityTextChanged(string value) => RefreshPlan();

    partial void OnBitrateTextChanged(string value) => RefreshPlan();

    partial void OnEncodersTextChanged(string value) => RefreshPlan();

    partial void OnPixelFormatTextChanged(string value) => RefreshPlan();

    partial void OnAudioEncoderTextChanged(string value) => RefreshPlan();

    partial void OnAudioBitrateTextChanged(string value) => RefreshPlan();

    partial void OnChannelsChanged(ChannelChoice value) => RefreshPlan();

    partial void OnNormaliseChanged(bool value) => RefreshPlan();

    partial void OnSoundOnlyChanged(bool value) => RefreshPlan();

    partial void OnLoudnessTextChanged(string value) => RefreshPlan();

    partial void OnTargetSizeTextChanged(string value) => RefreshPlan();

    partial void OnStartTextChanged(string value) => RefreshPlan();

    partial void OnEndTextChanged(string value) => RefreshPlan();

    [RelayCommand]
    private void Browse()
    {
        if (_files.SaveExport(OutputPath) is { } chosen)
        {
            OutputPath = chosen;
        }
    }

    [RelayCommand]
    private void ResetOverrides()
    {
        SizeText = FrameRateText = QualityText = BitrateText = EncodersText = PixelFormatText = string.Empty;
        AudioEncoderText = AudioBitrateText = TargetSizeText = StartText = EndText = string.Empty;
        Channels = ChannelChoices[0];
        Normalise = false;
        SoundOnly = false;
        LoudnessText = "-14";
    }

    /// <summary>Adds the export to the queue, behind anything already waiting.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => EnqueueAsync(ExportPriority.Normal);

    /// <summary>Adds the export to the queue ahead of everything waiting, so it starts as soon as a slot is free.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportNowAsync() => EnqueueAsync(ExportPriority.High);

    private async Task EnqueueAsync(ExportPriority priority)
    {
        PlanExportQuery query;
        try
        {
            query = Query();
        }
        catch (CommandException refusal)
        {
            Error = refusal.Message;
            return;
        }

        CommandResult result = await _session.ExecuteAsync(new EnqueueExportCommand(
            query.Output,
            query.Preset,
            query.Mode,
            query.SequenceId,
            query.SnapToKeyframes,
            query.UseInOut,
            query.External,
            query.Subtitles,
            query.SidecarFormat,
            query.Chapters,
            query.Size,
            query.FrameRate,
            query.Quality,
            query.Bitrate,
            query.Encoders,
            query.AudioEncoder,
            query.AudioBitrate,
            query.Channels,
            query.Loudness,
            query.TargetSize,
            query.PixelFormat,
            query.AudioOnly,
            query.Start,
            query.End,
            priority)).ConfigureAwait(true);

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

    /// <summary>The plan query the dialog's fields make, reading each override as the command line would.</summary>
    /// <exception cref="CommandException">When an override does not read.</exception>
    private PlanExportQuery Query()
    {
        Rational rate = _session.Project.Sequence(SequenceId ?? string.Empty) is { } sequence
            ? _session.Project.SettingsFor(sequence).FrameRate
            : _session.Project.Settings.FrameRate;

        T? Read<T>(string text, string name) =>
            string.IsNullOrWhiteSpace(text) ? default : (T?)CommandValues.Parse(typeof(T), text.Trim(), rate, name);

        return new PlanExportQuery(
            OutputPath,
            Preset?.Name ?? ExportPresets.Default,
            Mode,
            SequenceId,
            SnapToKeyframes,
            UseInOut,
            UseExternalFfmpeg,
            Subtitles,
            Chapters: Chapters,
            Size: Read<FrameSize?>(SizeText, "size"),
            FrameRate: Read<Rational?>(FrameRateText, "fps"),
            Quality: Read<int?>(QualityText, "quality"),
            Bitrate: Blank(BitrateText),
            Encoders: string.IsNullOrWhiteSpace(EncodersText) ? default : Read<EquatableArray<string>>(EncodersText, "encoder"),
            AudioEncoder: Blank(AudioEncoderText),
            AudioBitrate: Blank(AudioBitrateText),
            Channels: Channels.Count > 0 ? Channels.Count : null,
            Loudness: Normalise ? Read<double?>(LoudnessText, "loudness") : null,
            TargetSize: Blank(TargetSizeText),
            PixelFormat: Blank(PixelFormatText),
            AudioOnly: SoundOnly,
            Start: Read<Flicks?>(StartText, "start"),
            End: Read<Flicks?>(EndText, "end"));
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Show(ExportPlan? plan, string error)
    {
        IsPlanning = false;
        Plan = plan;
        Error = error;
        Reasons.Clear();

        if (plan is null)
        {
            Summary = string.Empty;
            Estimate = string.Empty;
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
            ? $"Copy, {(plan.Copy!.AudioStreams.IsEmpty ? "no sound" : Words.Count(plan.Copy.AudioStreams.Length, "sound stream"))}"
            : plan.Smart is { } smart
                ? string.Create(CultureInfo.InvariantCulture, $"Smart cut, {Words.Count(smart.EncodedFrames, "frame")} encoded again with {smart.Encoders[0]}, {(smart.AudioStreams.IsEmpty ? "no sound" : Words.Count(smart.AudioStreams.Length, "sound stream"))}")
            : plan.Video is { } video
                ? string.Create(CultureInfo.InvariantCulture, $"Encode {video.Width}x{video.Height} {video.Codec} at {video.FrameRate.ToDouble():0.###} fps{(plan.Audio is null ? string.Empty : $" with {plan.Audio.Encoder}")}")
                : $"Sound only, {plan.Audio!.Encoder}";

        string extras = string.Concat(
            plan.Subtitles is { } subtitles
                ? subtitles.Delivery switch
                {
                    SubtitleDelivery.Burn => ", subtitles burned in",
                    SubtitleDelivery.Sidecar => $", {Words.Count(subtitles.Tracks.Length, "subtitle file")} beside it",
                    _ => $", {Words.Count(subtitles.Tracks.Length, "subtitle stream")}",
                }
                : string.Empty,
            plan.Chapters.IsEmpty ? string.Empty : $", {Words.Count(plan.Chapters.Length, "chapter")}");

        Summary = $"{what}, {Timecode.FormatClock(plan.Duration)}{extras}, to {Path.GetFileName(plan.OutputPath)}";
        Estimate = plan.Estimate is { } estimate
            ? $"{char.ToUpperInvariant(estimate.ToString()[0])}{estimate.ToString()[1..]}{(plan.TargetBytes > 0 ? $", under {ExportPresets.FormatBytes(plan.TargetBytes)}" : string.Empty)}. Colour: BT.709, SDR{(plan.Video?.TenBit == true ? ", ten bit" : string.Empty)}."
            : string.Empty;
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

        // Otherwise Settings, Export's folder; else beside the project; else Videos for one never saved.
        string name = sequence?.Name is { Length: > 0 } given ? given : project.Name;
        string folder = _defaults.ExportFolder is { Length: > 0 } chosen ? Environment.ExpandEnvironmentVariables(chosen)
            : _session.ProjectPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(_session.ProjectPath)) ?? string.Empty
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(folder, name + (Preset?.Extension ?? ".mp4"));
    }
}

/// <summary>A way to export subtitle tracks, with the words the dialog shows for it.</summary>
/// <param name="Value">The delivery.</param>
/// <param name="Label">What the picker says.</param>
public sealed record SubtitleChoice(SubtitleDelivery Value, string Label);

/// <summary>A channel count, with the words the dialog shows for it.</summary>
/// <param name="Count">1, 2 or 6, or 0 for the preset's own.</param>
/// <param name="Label">What the picker says.</param>
public sealed record ChannelChoice(int Count, string Label);

/// <summary>
/// What each of the Export dialog's override boxes gets when it is left empty, from the plan: the
/// preset's values for this sequence. The dialog shows them in the empty boxes.
/// </summary>
public sealed record ExportHints(
    string Size,
    string FrameRate,
    string Quality,
    string Bitrate,
    string Encoders,
    string PixelFormat,
    string Sound,
    string SoundBitrate,
    string Under,
    string From,
    string To)
{
    /// <summary>No plan yet: nothing to say.</summary>
    public static ExportHints None { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>The hints for a plan, or none without one.</summary>
    public static ExportHints Of(ExportPlan? plan)
    {
        if (plan is null)
        {
            return None;
        }

        // A copy or a smart cut takes picture and sound as the source has them; a sound-only
        // encode has no picture.
        bool passed = plan.Mode == ExportMode.Copy || plan.Smart is not null;
        string source = plan.Mode == ExportMode.Copy ? "copied" : "as the source";
        string picture = passed ? source : "no picture";
        ExportVideo? video = plan.Video;
        ExportAudio? audio = plan.Audio;
        return new ExportHints(
            Size: video is null ? picture : string.Create(CultureInfo.InvariantCulture, $"{video.Width}x{video.Height}"),
            FrameRate: video is null ? picture : video.FrameRate.ToString(),
            Quality: video is null ? picture : video.Bitrate > 0 ? "a bitrate instead" : video.Quality.ToString(CultureInfo.InvariantCulture),
            Bitrate: video is null ? picture : video.Bitrate > 0 ? Bits(video.Bitrate) : "constant quality",
            Encoders: video is null ? picture : string.Join(",", video.Encoders),
            PixelFormat: video is null ? picture : video.PixelFormat ?? "the encoder's own",
            Sound: audio?.Encoder ?? (passed ? source : "none"),
            SoundBitrate: audio is null ? (passed ? source : string.Empty) : audio.Bitrate > 0 ? Bits(audio.Bitrate) : "the encoder's own",
            Under: plan.TargetBytes > 0 ? ExportPresets.FormatBytes(plan.TargetBytes) : "no limit",
            From: plan.Ranges.IsEmpty ? string.Empty : Timecode.FormatClock(plan.Ranges[0].Start),
            To: plan.Ranges.IsEmpty ? string.Empty : Timecode.FormatClock(plan.Ranges[plan.Ranges.Length - 1].End));
    }

    private static string Bits(long perSecond) => perSecond % 1_000_000 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{perSecond / 1_000_000}M")
        : string.Create(CultureInfo.InvariantCulture, $"{perSecond / 1000}k");
}
