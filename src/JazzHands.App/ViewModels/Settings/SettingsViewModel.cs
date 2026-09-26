using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Settings;

namespace JazzHands.App.ViewModels.Settings;

/// <summary>A choice in a list: what is stored, and what the person reads.</summary>
/// <param name="Value">What is stored.</param>
/// <param name="Label">What is shown.</param>
public sealed record SettingsChoice(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The Settings dialog: playback, caches, GPU, proxies, export defaults, the control server and
/// the keymap, each applied as soon as it can be.
/// </summary>
/// <remarks>
/// <para>
/// Settings live in <c>%APPDATA%\JazzHands\settings.json</c>, a section per concern. What the
/// engine can change while running changes on Apply: the speakers and scrub sound (the transport),
/// the preview quality, the cache's size limit and playing proxies (through the commands the CLI
/// and RPC send: <c>playback.set-quality</c>, <c>cache.configure</c>, <c>proxy.set-enabled</c>). The
/// GPU, the cache's folder and the control server take effect at the next start, and the dialog
/// says so rather than pretending.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly SettingsSection<EditorSettings> _editor;
    private readonly CacheSettingsStore _cache;
    private readonly ControlSettingsStore _control;
    private readonly IPlaybackPreferences _playback;
    private EditorSettings _appliedEditor;
    private CacheSettings _appliedCache;
    private ControlSettings _appliedControl;
    private bool _appliedProxies;

    [ObservableProperty]
    private SettingsChoice _audioDevice;

    [ObservableProperty]
    private bool _scrubAudio;

    [ObservableProperty]
    private SettingsChoice _previewQuality;

    [ObservableProperty]
    private string _cacheLocation;

    [ObservableProperty]
    private double _cacheCapGb;

    [ObservableProperty]
    private SettingsChoice _gpu;

    [ObservableProperty]
    private bool _playProxies;

    [ObservableProperty]
    private SettingsChoice _exportPreset;

    [ObservableProperty]
    private string _exportFolder;

    [ObservableProperty]
    private bool _openLastProject;

    /// <summary>Closing the window keeps Jazz Hands in the notification area (true) or quits.</summary>
    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _windowsNotifications;

    [ObservableProperty]
    private bool _notifyClientAttached;

    [ObservableProperty]
    private bool _controlTcp;

    [ObservableProperty]
    private int _controlPort;

    [ObservableProperty]
    private string _controlAddress;

    [ObservableProperty]
    private bool _controlAllowRemote;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the dialog's viewmodel over the stores and the running engine.</summary>
    public SettingsViewModel(
        ISession session,
        SettingsSection<EditorSettings> editor,
        CacheSettingsStore cache,
        ControlSettingsStore control,
        IPlaybackPreferences playback,
        Keys.KeymapEditorViewModel? keymap = null,
        IReadOnlyList<string>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(playback);

        _session = session;
        _editor = editor;
        _cache = cache;
        _control = control;
        _playback = playback;
        Keymap = keymap;

        AudioDevices = [new SettingsChoice(string.Empty, "Windows default"), .. playback.Devices().Select(device => new SettingsChoice(device.Id, device.Name))];
        Qualities = [new("auto", "Auto: half while scrubbing"), new("full", "Full"), new("half", "Half"), new("quarter", "Quarter")];
        Gpus = [new("auto", "Automatic: the fastest"), new("warp", "Software (WARP)"), .. (adapters ?? []).Select((name, index) => new SettingsChoice(index.ToString(CultureInfo.InvariantCulture), name))];
        Presets = [.. session.Query(new ListPresetsQuery()).Select(preset => new SettingsChoice(preset.Name, preset.Label.Length > 0 ? preset.Label : preset.Name))];

        _appliedEditor = editor.Current;
        _appliedCache = cache.Current;
        _appliedControl = control.Current;
        _appliedProxies = playback.ProxiesEnabled;

        _audioDevice = Choose(AudioDevices, playback.Device ?? _appliedEditor.AudioDevice ?? string.Empty);
        _scrubAudio = _appliedEditor.ScrubAudio;
        _previewQuality = Choose(Qualities, _appliedEditor.PreviewQuality);
        _cacheLocation = _appliedCache.Location ?? string.Empty;
        _cacheCapGb = Math.Round(_appliedCache.CapBytes / (1024.0 * 1024 * 1024), 1);
        _gpu = Choose(Gpus, _appliedEditor.Gpu);
        _playProxies = _appliedProxies;
        _exportPreset = Choose(Presets, _appliedEditor.ExportPreset);
        _exportFolder = _appliedEditor.ExportFolder ?? string.Empty;
        _openLastProject = _appliedEditor.OpenLastProject;
        _closeToTray = _appliedEditor.CloseToTray;
        _startWithWindows = _appliedEditor.StartWithWindows;
        _windowsNotifications = _appliedEditor.WindowsNotifications;
        _notifyClientAttached = _appliedEditor.NotifyClientAttached;
        _controlTcp = _appliedControl.Tcp;
        _controlPort = _appliedControl.Port;
        _controlAddress = _appliedControl.Address;
        _controlAllowRemote = _appliedControl.AllowRemote;
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The speakers to choose from, the Windows default first.</summary>
    public IReadOnlyList<SettingsChoice> AudioDevices { get; }

    /// <summary>The preview resolutions.</summary>
    public IReadOnlyList<SettingsChoice> Qualities { get; }

    /// <summary>The adapters to render on.</summary>
    public IReadOnlyList<SettingsChoice> Gpus { get; }

    /// <summary>The export presets.</summary>
    public IReadOnlyList<SettingsChoice> Presets { get; }

    /// <summary>The keymap page, when the host has one.</summary>
    public Keys.KeymapEditorViewModel? Keymap { get; }

    /// <summary>Writes every section and applies what can be applied now.</summary>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        var later = new List<string>();
        var problems = new List<string>();

        EditorSettings editor = _appliedEditor with
        {
            AudioDevice = AudioDevice.Value.Length > 0 ? AudioDevice.Value : null,
            ScrubAudio = ScrubAudio,
            PreviewQuality = PreviewQuality.Value,
            Gpu = Gpu.Value,
            ExportPreset = ExportPreset.Value,
            ExportFolder = ExportFolder.Trim().Length > 0 ? ExportFolder.Trim() : null,
            OpenLastProject = OpenLastProject,
            CloseToTray = CloseToTray,
            StartWithWindows = StartWithWindows,
            WindowsNotifications = WindowsNotifications,
            NotifyClientAttached = NotifyClientAttached,
        };

        // The speakers and the scrub sound: the transport, straight away.
        _playback.Device = editor.AudioDevice;
        _playback.ScrubAudio = editor.ScrubAudio;

        if (editor.PreviewQuality != _appliedEditor.PreviewQuality
            && Enum.TryParse(editor.PreviewQuality, ignoreCase: true, out PreviewQuality quality))
        {
            await Run(new SetQualityCommand(quality), problems).ConfigureAwait(true);
        }

        if (editor.Gpu != _appliedEditor.Gpu)
        {
            later.Add("the GPU");
        }

        // Merged with what is there now: the window keeps its place and workspace in the same section.
        editor = _editor.Update(current => current with
        {
            AudioDevice = editor.AudioDevice,
            ScrubAudio = editor.ScrubAudio,
            PreviewQuality = editor.PreviewQuality,
            Gpu = editor.Gpu,
            ExportPreset = editor.ExportPreset,
            ExportFolder = editor.ExportFolder,
            OpenLastProject = editor.OpenLastProject,
            CloseToTray = editor.CloseToTray,
            StartWithWindows = editor.StartWithWindows,
            WindowsNotifications = editor.WindowsNotifications,
            NotifyClientAttached = editor.NotifyClientAttached,
        });
        _appliedEditor = editor;

        // The cache: its limit now, through the command; a new folder from the next start.
        string? location = CacheLocation.Trim().Length > 0 ? CacheLocation.Trim() : null;
        long capBytes = (long)(Math.Max(0, CacheCapGb) * 1024 * 1024 * 1024);
        bool moved = !string.Equals(location, _appliedCache.Location, StringComparison.OrdinalIgnoreCase);
        bool capped = capBytes != _appliedCache.CapBytes;
        if (capped || (moved && location is not null))
        {
            await Run(new ConfigureCacheCommand(moved ? location : null, capped ? Math.Max(0, CacheCapGb) : null), problems).ConfigureAwait(true);
        }

        if (moved && location is null)
        {
            // Back to the default folder: the command only sets one, so the store is told directly.
            _cache.Save(_cache.Current with { Location = null });
        }

        if (moved)
        {
            later.Add("the cache folder");
        }

        _appliedCache = _cache.Current;

        if (PlayProxies != _appliedProxies && await Run(new SetProxiesEnabledCommand(PlayProxies), problems).ConfigureAwait(true))
        {
            _appliedProxies = PlayProxies;
        }

        var control = new ControlSettings(ControlTcp, ControlPort is > 0 and < 65536 ? ControlPort : ControlServerOptions.DefaultTcpPort, ControlAddress.Trim().Length > 0 ? ControlAddress.Trim() : "127.0.0.1", ControlAllowRemote);
        if (control != _appliedControl)
        {
            _control.Save(control);
            _appliedControl = control;
            later.Add("the control server");
        }

        if (Keymap is { } keys && !keys.Save())
        {
            problems.Add(keys.Status);
        }

        Status = problems.Count > 0
            ? string.Join(" ", problems)
            : later.Count > 0
                ? $"Saved. {Capitalised(string.Join(", ", later))} change{(later.Count > 1 ? string.Empty : "s")} when Jazz Hands next starts; the rest is in use now."
                : "Saved, and in use now.";
    }

    /// <summary>Applies, then closes.</summary>
    [RelayCommand]
    public async Task OkAsync()
    {
        await ApplyAsync().ConfigureAwait(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Takes the .jazz association, the Explorer verbs and start with Windows back from Windows
    /// (<c>app.unregister</c>), the step before uninstalling.
    /// </summary>
    [RelayCommand]
    public async Task RemoveFromWindowsAsync()
    {
        var problems = new List<string>();
        if (await Run(new UnregisterAppCommand(), problems).ConfigureAwait(true))
        {
            StartWithWindows = false;
            _appliedEditor = _appliedEditor with { StartWithWindows = false };
            Status = "Removed from Windows. Uninstall Jazz Hands now; if it starts again first, it registers .jazz files and the Explorer verbs again.";
        }
        else
        {
            Status = string.Join(" ", problems);
        }
    }

    private async Task<bool> Run(ICommand command, List<string> problems)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            problems.Add(result.Error ?? $"{CommandRegistry.NameOf(command)} was refused.");
        }

        return result.Ok;
    }

    private static SettingsChoice Choose(IReadOnlyList<SettingsChoice> choices, string value) =>
        choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
