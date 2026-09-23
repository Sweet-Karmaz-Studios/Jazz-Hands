using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Audio;

namespace JazzHands.App.ViewModels.Audio;

/// <summary>
/// The master meters: a peak bar, an RMS bar and a held peak per channel.
/// </summary>
/// <remarks>
/// Polled thirty times a second rather than pushed, because the audio thread writes ninety
/// readings a second into a ring and must never wait on the UI. Each tick takes the newest
/// reading and drops the rest.
///
/// The bars fall at a fixed rate rather than jumping to each reading, which is what makes a
/// meter readable: a peak that vanishes in a thirtieth of a second is a peak nobody saw. When no
/// reading arrives (paused, stopped) the bars fall to the floor the same way.
/// </remarks>
public sealed partial class MetersPanelViewModel : ToolViewModel, IDisposable
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "meters";

    /// <summary>The bottom of the scale. Anything quieter shows as nothing.</summary>
    public const double FloorDb = -60.0;

    /// <summary>How fast a bar falls, in dB a second.</summary>
    public const double FallDbPerSecond = 24.0;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(33);

    private readonly IMeterFeed _feed;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private readonly double[] _peak;
    private readonly double[] _rms;
    private readonly double[] _hold;
    private long _lastTick;
    private int _ticking;

    /// <summary>Creates the panel.</summary>
    /// <param name="feed">Where readings come from.</param>
    /// <param name="ui">How to get back onto the UI thread.</param>
    /// <param name="time">The clock, so tests can move it by hand.</param>
    /// <param name="poll">False to leave ticking to the caller, for tests.</param>
    public MetersPanelViewModel(IMeterFeed feed, IUiDispatcher ui, TimeProvider? time = null, bool poll = true)
        : base(PanelId, "Meters")
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(ui);

        _feed = feed;
        _ui = ui;
        _time = time ?? TimeProvider.System;

        int channels = Math.Clamp(feed.Channels, 1, Dsp.MaxChannels);
        _peak = new double[channels];
        _rms = new double[channels];
        _hold = new double[channels];
        Array.Fill(_peak, FloorDb);
        Array.Fill(_rms, FloorDb);
        Array.Fill(_hold, FloorDb);

        Channels = [.. ChannelNames(channels).Select(name => new MeterChannelViewModel(name))];
        _lastTick = _time.GetTimestamp();

        if (poll)
        {
            _timer = _time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
        }
    }

    /// <summary>One entry per channel, in mix order.</summary>
    public ObservableCollection<MeterChannelViewModel> Channels { get; }

    /// <summary>Takes the newest reading, lets the bars fall, and posts the result to the UI.</summary>
    public void Tick()
    {
        // Timer callbacks can overlap if one is slow, and the ring has one reader.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            long now = _time.GetTimestamp();
            double seconds = _time.GetElapsedTime(_lastTick, now).TotalSeconds;
            _lastTick = now;

            double fall = FallDbPerSecond * seconds;
            bool fresh = _feed.TryReadLatest(out MeterReading reading);

            for (int channel = 0; channel < _peak.Length; channel++)
            {
                double peak = fresh && channel < reading.Channels ? ToDb(reading.Peak[channel]) : FloorDb;
                double rms = fresh && channel < reading.Channels ? ToDb(reading.Rms[channel]) : FloorDb;
                double hold = fresh && channel < reading.Channels ? ToDb(reading.Hold[channel]) : FloorDb;

                _peak[channel] = Math.Max(peak, _peak[channel] - fall);
                _rms[channel] = Math.Max(rms, _rms[channel] - fall);
                _hold[channel] = fresh ? hold : Math.Max(FloorDb, _hold[channel] - fall);
            }

            double[] peaks = [.. _peak];
            double[] levels = [.. _rms];
            double[] holds = [.. _hold];

            _ui.Post(() =>
            {
                for (int channel = 0; channel < Channels.Count; channel++)
                {
                    Channels[channel].Update(peaks[channel], levels[channel], holds[channel]);
                }
            });
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    /// <summary>Where a level sits on the bar, from 0 at the floor to 1 at full scale.</summary>
    public static double ToLevel(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0.0, 1.0);

    private static double ToDb(float linear) => linear <= 0 ? FloorDb : Math.Max(FloorDb, 20.0 * Math.Log10(linear));

    private static string[] ChannelNames(int channels) => channels switch
    {
        1 => ["M"],
        2 => ["L", "R"],
        6 => ["L", "R", "C", "LFE", "Ls", "Rs"],
        _ => [.. Enumerable.Range(1, channels).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))],
    };
}

/// <summary>One channel's bar.</summary>
/// <param name="name">Its label: L, R, C and so on.</param>
public sealed partial class MeterChannelViewModel(string name) : ObservableObject
{
    [ObservableProperty]
    private double _peakDb = MetersPanelViewModel.FloorDb;

    [ObservableProperty]
    private double _rmsDb = MetersPanelViewModel.FloorDb;

    [ObservableProperty]
    private double _holdDb = MetersPanelViewModel.FloorDb;

    /// <summary>Its label.</summary>
    public string Name { get; } = name;

    /// <summary>The peak bar's height, 0 to 1.</summary>
    public double PeakLevel => MetersPanelViewModel.ToLevel(PeakDb);

    /// <summary>The RMS bar's height, 0 to 1.</summary>
    public double RmsLevel => MetersPanelViewModel.ToLevel(RmsDb);

    /// <summary>The held peak, as the number under the bar.</summary>
    public string HoldText => HoldDb <= MetersPanelViewModel.FloorDb
        ? "-inf"
        : HoldDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when the held peak is at full scale, which the bar shows in red.</summary>
    public bool IsClipping => HoldDb >= -0.1;

    internal void Update(double peakDb, double rmsDb, double holdDb)
    {
        PeakDb = peakDb;
        RmsDb = rmsDb;
        HoldDb = holdDb;
    }

    partial void OnPeakDbChanged(double value) => OnPropertyChanged(nameof(PeakLevel));

    partial void OnRmsDbChanged(double value) => OnPropertyChanged(nameof(RmsLevel));

    partial void OnHoldDbChanged(double value)
    {
        OnPropertyChanged(nameof(HoldText));
        OnPropertyChanged(nameof(IsClipping));
    }
}
