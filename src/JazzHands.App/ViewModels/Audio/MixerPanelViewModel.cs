using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Audio;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.App.ViewModels.Audio;

/// <summary>
/// The Audio Mixer: a strip for every audio track of the active sequence, and the master.
/// </summary>
/// <remarks>
/// <para>
/// Every control is a command. The fader is <c>track.set-volume</c>, the pan knob
/// <c>track.set-pan</c>, the buttons <c>track.set-mute</c>, <c>-solo</c> and <c>-lock</c>, the
/// effect slots <c>effect.add</c>, <c>effect.set-enabled</c> and <c>effect.remove</c>, and the
/// master <c>audio.set-master-volume</c> and <c>audio.set-limiter</c>. A fader on a track whose
/// volume has keyframes writes a keyframe at the playhead, as the inspector does. While a value
/// is on its way only the newest waits behind it, and the engine folds a drag into one undo step.
/// </para>
/// <para>
/// The meters are polled thirty times a second, as the Meters panel's are, through a tap that
/// hands each tick every block mixed since the last one. The bars fall at a fixed rate, the held
/// peak is the audio thread's, and a clip lights the strip's indicator until it is clicked.
/// </para>
/// </remarks>
public sealed partial class MixerPanelViewModel : ToolViewModel, IDisposable, Shell.IQuietWhileHidden
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "mixer";

    /// <summary>The bottom of a fader, which is silence.</summary>
    public const double FaderFloorDb = -60.0;

    /// <summary>The top of a fader.</summary>
    public const double FaderTopDb = 12.0;

    /// <summary>What a fader at the bottom sends: the quietest gain a command takes.</summary>
    public const double SilenceDb = -144.0;

    internal const string MasterKey = "master:volume";

    internal const string CeilingKey = "master:ceiling";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);

    private readonly ISession _session;
    private readonly IMixerFeed _feed;
    private readonly IUiDispatcher _ui;
    private readonly IPreviewEngine? _preview;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private readonly CommandPump _pump;
    private ImmutableArray<string> _stripIds = [];
    private long _lastTick;
    private int _ticking;

    [ObservableProperty]
    private string _sequenceName = string.Empty;

    [ObservableProperty]
    private string? _sequenceId;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Creates the panel.</summary>
    /// <param name="session">The project, and where commands go.</param>
    /// <param name="feed">Where readings come from.</param>
    /// <param name="ui">How to get back onto the UI thread.</param>
    /// <param name="preview">The playhead, for the keyframes a fader writes; none in a test that does not need it.</param>
    /// <param name="time">The clock, so tests can move it by hand.</param>
    /// <param name="poll">False to leave ticking to the caller, for tests.</param>
    public MixerPanelViewModel(ISession session, IMixerFeed feed, IUiDispatcher ui, IPreviewEngine? preview = null, TimeProvider? time = null, bool poll = true)
        : base(PanelId, "Mixer")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _feed = feed;
        _ui = ui;
        _preview = preview;

        // Once a control's last value has gone, the model has it, or refused it: show the model.
        _pump = new CommandPump(session, ui);
        _pump.Refused += (_, message) => Status = message;
        _pump.Settled += (_, _) => Refresh();
        _time = time ?? TimeProvider.System;

        Channels = Math.Clamp(feed.Channels, 1, Dsp.MaxChannels);
        Master = new MasterStripViewModel(this, Channels);
        AvailableEffects = [.. EffectCatalog.Registry.All
            .Where(descriptor => descriptor.Kind == EffectKind.Audio && !typeof(JazzHands.Audio.Effects.IClipEffect).IsAssignableFrom(descriptor.Implementation))
            .OrderBy(descriptor => descriptor.Category, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .Select(descriptor => new MixerEffectChoice(descriptor.TypeId, descriptor.Name, descriptor.Category))];

        _session.ProjectChanged += (_, _) => _ui.Post(Refresh);
        _preview?.PlayheadMoved += (_, _) => _ui.Post(RefreshAnimated);
        Refresh();

        _lastTick = _time.GetTimestamp();
        if (poll)
        {
            _timer = _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
        }
    }

    /// <summary>A strip per audio track of the active sequence, in track order.</summary>
    public ObservableCollection<MixerStripViewModel> Strips { get; } = [];

    /// <summary>The master strip.</summary>
    public MasterStripViewModel Master { get; }

    /// <summary>The audio effects a slot can take, by category.</summary>
    public IReadOnlyList<MixerEffectChoice> AvailableEffects { get; }

    /// <summary>How many channels each meter shows.</summary>
    public int Channels { get; }

    /// <summary>Where the playhead is, which is where a fader on an animated volume writes.</summary>
    internal Flicks Playhead => _preview?.Position ?? Flicks.Zero;

    /// <summary>Reads the active sequence into the strips. UI thread.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        Sequence? sequence = project.ActiveSequence;
        SequenceName = sequence?.Name ?? string.Empty;
        SequenceId = sequence?.Id;

        Track[] tracks = sequence is null
            ? []
            : [.. sequence.Tracks.Where(track => track.Kind == TrackKind.Audio).OrderBy(track => track.Order)];

        // Strips are kept by track id, so a meter's fall and a drag in progress survive an edit.
        for (int index = Strips.Count - 1; index >= 0; index--)
        {
            if (!tracks.Any(track => string.Equals(track.Id, Strips[index].TrackId, StringComparison.Ordinal)))
            {
                Strips.RemoveAt(index);
            }
        }

        for (int index = 0; index < tracks.Length; index++)
        {
            Track track = tracks[index];
            int at = IndexOf(track.Id);
            MixerStripViewModel strip;
            if (at < 0)
            {
                strip = new MixerStripViewModel(this, track.Id, Channels);
                Strips.Insert(index, strip);
            }
            else
            {
                strip = Strips[at];
                if (at != index)
                {
                    Strips.Move(at, index);
                }
            }

            strip.Apply(track, Playhead, IsSending(VolumeKey(track.Id)), IsSending(PanKey(track.Id)), tracks, [.. Role.All(_session.Project)]);
        }

        _stripIds = [.. tracks.Select(track => track.Id)];
        IsEmpty = Strips.Count == 0;
        Master.Apply(sequence?.Master, Playhead, IsSending(MasterKey) || IsSending(CeilingKey));
    }

    /// <summary>Takes the newest readings and posts them to the strips. Any thread.</summary>
    public void Tick()
    {
        // Timer callbacks can overlap if one is slow, and each reader has one consumer.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            long now = _time.GetTimestamp();
            double seconds = _time.GetElapsedTime(_lastTick, now).TotalSeconds;
            _lastTick = now;

            MeterReading? master = _feed.TryReadMaster(out MeterReading reading) ? reading : null;
            ImmutableArray<string> ids = _stripIds;
            var tracks = new (string Id, MeterReading? Reading)[ids.Length];
            for (int index = 0; index < ids.Length; index++)
            {
                tracks[index] = (ids[index], _feed.TryReadTrack(ids[index], out MeterReading track) ? track : null);
            }

            _ui.Post(() =>
            {
                Master.Show(master, seconds);
                foreach ((string id, MeterReading? track) in tracks)
                {
                    int at = IndexOf(id);
                    if (at >= 0)
                    {
                        Strips[at].Meter.Show(track, seconds);
                    }
                }
            });
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <inheritdoc />
    /// <summary>Stops polling while the window is hidden, and starts again when it shows.</summary>
    public void SetQuiet(bool quiet) =>
        _timer?.Change(quiet ? Timeout.InfiniteTimeSpan : TickInterval, quiet ? Timeout.InfiniteTimeSpan : TickInterval);

    public void Dispose() => _timer?.Dispose();

    /// <summary>A fader's value as its label, with its unit: signed, one decimal, or -inf at the bottom.</summary>
    public static string FormatDb(double db) => db <= FaderFloorDb
        ? "-inf dB"
        : db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    /// <summary>A pan as its label: C, or L or R and how far, out of 100.</summary>
    public static string FormatPan(double pan) => Math.Abs(pan) < 0.005
        ? "C"
        : (pan < 0 ? "L" : "R") + Math.Round(Math.Abs(pan) * 100).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A loudness or a level for a readout: one decimal, or -inf for silence.</summary>
    public static string FormatLevel(double value) => double.IsFinite(value) && value > -100
        ? value.ToString("0.0", CultureInfo.InvariantCulture)
        : "-inf";

    internal static string VolumeKey(string trackId) => "volume:" + trackId;

    internal static string PanKey(string trackId) => "pan:" + trackId;

    /// <summary>The gain a fader position sends: rounded to a tenth, and silence at the bottom.</summary>
    internal static double FaderToGain(double fader) => fader <= FaderFloorDb ? SilenceDb : Math.Round(Math.Min(fader, FaderTopDb), 1);

    /// <summary>A gain as a fader position.</summary>
    internal static double GainToFader(double db) => Math.Clamp(db, FaderFloorDb, FaderTopDb);

    /// <summary>The playhead when a track's parameter has keyframes, so a change writes one there.</summary>
    internal Flicks? AnimatedAt(string ownerId, string name) =>
        ParamTargets.Find(_session.Project, ownerId) is { } owner && ParamTargets.Get(owner, name) is KeyframedValue { IsAnimated: true }
            ? Playhead
            : null;

    /// <summary>The playhead when the master's volume has keyframes.</summary>
    internal Flicks? MasterAnimatedAt() =>
        _session.Project.ActiveSequence?.Master?.Volume is KeyframedValue { IsAnimated: true } ? Playhead : null;

    /// <summary>Sends the newest command for a control; see <see cref="CommandPump"/>.</summary>
    internal void Send(string key, Func<ICommand> make) => _pump.Send(key, make);

    /// <summary>True while a control's value is on its way, when the model is behind the control.</summary>
    internal bool IsSending(string key) => _pump.IsSending(key);

    /// <summary>Runs a one-off command, reporting a refusal on the panel.</summary>
    internal void Run(ICommand command) => _pump.Send(Id.New(), () => command);

    /// <summary>Starts the master's integrated loudness again.</summary>
    internal void ResetLoudness() => _feed.ResetLoudness();

    private int IndexOf(string trackId)
    {
        for (int index = 0; index < Strips.Count; index++)
        {
            if (string.Equals(Strips[index].TrackId, trackId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Moves the faders whose values have keyframes to follow the playhead.</summary>
    private void RefreshAnimated()
    {
        if (Master.IsVolumeAnimated || Strips.Any(strip => strip.IsVolumeAnimated || strip.IsPanAnimated))
        {
            Refresh();
        }
    }
}

/// <summary>An audio effect a slot can add.</summary>
/// <param name="TypeId">What <c>effect.add</c> takes.</param>
/// <param name="Name">What the menu says.</param>
/// <param name="Category">The group it is listed under.</param>
public sealed record MixerEffectChoice(string TypeId, string Name, string Category);

/// <summary>A track a strip can be ducked under, for its menu.</summary>
/// <param name="TrackId">The track.</param>
/// <param name="Name">What it is called.</param>
public sealed record MixerDuckChoice(string TrackId, string Name);
