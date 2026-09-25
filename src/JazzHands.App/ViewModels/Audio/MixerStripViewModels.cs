using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Audio;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Effects;

namespace JazzHands.App.ViewModels.Audio;

/// <summary>One track's strip: fader, pan, mute, solo, lock, meter and effect slots.</summary>
public sealed partial class MixerStripViewModel : ObservableObject
{
    private readonly MixerPanelViewModel _mixer;
    private bool _applying;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _color = "#3A6EA5";

    [ObservableProperty]
    private double _volumeDb;

    [ObservableProperty]
    private double _pan;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _isVolumeAnimated;

    [ObservableProperty]
    private bool _isPanAnimated;

    internal MixerStripViewModel(MixerPanelViewModel mixer, string trackId, int channels)
    {
        _mixer = mixer;
        TrackId = trackId;
        Meter = new MixerMeterViewModel(channels);
    }

    /// <summary>The track.</summary>
    public string TrackId { get; }

    /// <summary>The track's meter, after its volume and pan.</summary>
    public MixerMeterViewModel Meter { get; }

    /// <summary>The track's effects, in the order they run.</summary>
    public ObservableCollection<MixerEffectSlotViewModel> Effects { get; } = [];

    /// <summary>The effects a slot can add, for the menu.</summary>
    public IReadOnlyList<MixerEffectChoice> AvailableEffects => _mixer.AvailableEffects;

    /// <summary>The fader's label.</summary>
    public string VolumeText => MixerPanelViewModel.FormatDb(VolumeDb);

    /// <summary>The pan's label.</summary>
    public string PanText => MixerPanelViewModel.FormatPan(Pan);

    /// <summary>Reads the track into the strip without sending anything back.</summary>
    /// <param name="track">The track.</param>
    /// <param name="playhead">Where an animated volume or pan is read.</param>
    /// <param name="keepVolume">True while the fader's own value is on its way, so it is not pulled back mid-drag.</param>
    /// <param name="keepPan">The same for the pan.</param>
    internal void Apply(Track track, Flicks playhead, bool keepVolume, bool keepPan)
    {
        _applying = true;
        try
        {
            Name = track.Name;
            Color = track.Color;
            IsMuted = track.Muted;
            IsSolo = track.Solo;
            IsLocked = track.Locked;
            IsVolumeAnimated = track.Volume is KeyframedValue { IsAnimated: true };
            IsPanAnimated = track.Pan is KeyframedValue { IsAnimated: true };

            if (!keepVolume)
            {
                VolumeDb = MixerPanelViewModel.GainToFader(Level(track.Volume, 0, playhead));
            }

            if (!keepPan)
            {
                Pan = Level(track.Pan, 1, playhead);
            }

            SyncEffects(track);
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnVolumeDbChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (!_applying)
        {
            double db = MixerPanelViewModel.FaderToGain(value);
            _mixer.Send(MixerPanelViewModel.VolumeKey(TrackId), () => new SetTrackVolumeCommand(TrackId, db, _mixer.AnimatedAt(TrackId, "volume")));
        }
    }

    partial void OnPanChanged(double value)
    {
        OnPropertyChanged(nameof(PanText));
        if (!_applying)
        {
            double pan = Math.Round(Math.Clamp(value, -1.0, 1.0), 2);
            _mixer.Send(MixerPanelViewModel.PanKey(TrackId), () => new SetTrackPanCommand(TrackId, pan, _mixer.AnimatedAt(TrackId, "pan")));
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_applying)
        {
            _mixer.Run(new SetTrackMuteCommand(TrackId, value));
        }
    }

    partial void OnIsSoloChanged(bool value)
    {
        if (!_applying)
        {
            _mixer.Run(new SetTrackSoloCommand(TrackId, value));
        }
    }

    partial void OnIsLockedChanged(bool value)
    {
        if (!_applying)
        {
            _mixer.Run(new SetTrackLockCommand(TrackId, value));
        }
    }

    /// <summary>Puts the fader back to 0 dB, which a double-click on it does.</summary>
    [RelayCommand]
    private void ResetVolume() => VolumeDb = 0.0;

    /// <summary>Puts the pan back to the centre.</summary>
    [RelayCommand]
    private void ResetPan() => Pan = 0.0;

    /// <summary>Adds an effect at the end of the track's chain.</summary>
    [RelayCommand]
    private void AddEffect(string? typeId)
    {
        if (typeId is { Length: > 0 })
        {
            _mixer.Run(new AddEffectCommand(TrackId, typeId));
        }
    }

    private static double Level(AnimatedValue? value, int param, Flicks playhead) =>
        ParamEval.Eval(value, ParamTargets.Audio.Params[param], playhead) is ParamValue.Float level ? level.Value : 0.0;

    private void SyncEffects(Track track)
    {
        MixerEffectSlotViewModel[] slots = [.. track.Effects.Select(effect => new MixerEffectSlotViewModel(
            _mixer,
            effect.Id,
            EffectCatalog.Registry.Find(effect.TypeId)?.Name ?? effect.TypeId,
            effect.Enabled))];

        if (slots.SequenceEqual(Effects))
        {
            return;
        }

        Effects.Clear();
        foreach (MixerEffectSlotViewModel slot in slots)
        {
            Effects.Add(slot);
        }
    }
}

/// <summary>One effect in a strip's chain.</summary>
public sealed partial class MixerEffectSlotViewModel : ObservableObject, IEquatable<MixerEffectSlotViewModel>
{
    private readonly MixerPanelViewModel _mixer;

    internal MixerEffectSlotViewModel(MixerPanelViewModel mixer, string effectId, string name, bool enabled)
    {
        _mixer = mixer;
        EffectId = effectId;
        Name = name;
        IsEnabled = enabled;
    }

    /// <summary>The effect.</summary>
    public string EffectId { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>False while it is bypassed.</summary>
    public bool IsEnabled { get; }

    /// <inheritdoc />
    public bool Equals(MixerEffectSlotViewModel? other) =>
        other is not null
        && string.Equals(other.EffectId, EffectId, StringComparison.Ordinal)
        && string.Equals(other.Name, Name, StringComparison.Ordinal)
        && other.IsEnabled == IsEnabled;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MixerEffectSlotViewModel);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(EffectId, Name, IsEnabled);

    /// <summary>Bypasses it, or runs it again.</summary>
    [RelayCommand]
    private void ToggleEnabled() => _mixer.Run(new SetEffectEnabledCommand(EffectId, !IsEnabled));

    /// <summary>Takes it off the track.</summary>
    [RelayCommand]
    private void Remove() => _mixer.Run(new RemoveEffectCommand(EffectId));
}

/// <summary>A strip's meter: a bar per channel with its held peak, and a clip light that stays lit.</summary>
public sealed partial class MixerMeterViewModel : ObservableObject
{
    private readonly double[] _peak;
    private readonly double[] _rms;
    private readonly double[] _hold;

    [ObservableProperty]
    private bool _isClipped;

    internal MixerMeterViewModel(int channels)
    {
        _peak = new double[channels];
        _rms = new double[channels];
        _hold = new double[channels];
        Array.Fill(_peak, MetersPanelViewModel.FloorDb);
        Array.Fill(_rms, MetersPanelViewModel.FloorDb);
        Array.Fill(_hold, MetersPanelViewModel.FloorDb);
        Channels = [.. Enumerable.Range(1, channels).Select(channel => new MeterChannelViewModel(channel.ToString(CultureInfo.InvariantCulture)))];
    }

    /// <summary>One bar per channel.</summary>
    public IReadOnlyList<MeterChannelViewModel> Channels { get; }

    /// <summary>Shows a reading, or lets the bars fall when there is none. UI thread.</summary>
    internal void Show(MeterReading? reading, double seconds)
    {
        double fall = MetersPanelViewModel.FallDbPerSecond * seconds;
        for (int channel = 0; channel < _peak.Length; channel++)
        {
            bool fresh = reading is { } value && channel < value.Channels;
            double peak = fresh ? ToDb(reading!.Value.Peak[channel]) : MetersPanelViewModel.FloorDb;
            double rms = fresh ? ToDb(reading!.Value.Rms[channel]) : MetersPanelViewModel.FloorDb;
            double hold = fresh ? ToDb(reading!.Value.Hold[channel]) : MetersPanelViewModel.FloorDb;

            _peak[channel] = Math.Max(peak, _peak[channel] - fall);
            _rms[channel] = Math.Max(rms, _rms[channel] - fall);
            _hold[channel] = fresh ? Math.Max(hold, _peak[channel]) : Math.Max(MetersPanelViewModel.FloorDb, _hold[channel] - fall);
            Channels[channel].Update(_peak[channel], _rms[channel], _hold[channel]);
        }

        if (reading is { Clipped: true })
        {
            IsClipped = true;
        }
    }

    /// <summary>Puts the clip light out.</summary>
    [RelayCommand]
    private void ClearClip() => IsClipped = false;

    private static double ToDb(float linear) =>
        linear <= 0 ? MetersPanelViewModel.FloorDb : Math.Max(MetersPanelViewModel.FloorDb, 20.0 * Math.Log10(linear));
}

/// <summary>The master strip: fader, limiter, meter and loudness.</summary>
public sealed partial class MasterStripViewModel : ObservableObject
{
    /// <summary>The most gain reduction the reduction bar shows.</summary>
    public const double ReductionRangeDb = 20.0;

    private readonly MixerPanelViewModel _mixer;
    private bool _applying;
    private double _maxTruePeakDb = double.NegativeInfinity;

    [ObservableProperty]
    private double _volumeDb;

    [ObservableProperty]
    private bool _isVolumeAnimated;

    [ObservableProperty]
    private bool _isLimiterOn = true;

    [ObservableProperty]
    private double _ceilingDb = MasterBus.DefaultCeiling;

    [ObservableProperty]
    private double _momentary = double.NegativeInfinity;

    [ObservableProperty]
    private double _shortTerm = double.NegativeInfinity;

    [ObservableProperty]
    private double _integrated = double.NegativeInfinity;

    [ObservableProperty]
    private double _reductionDb;

    [ObservableProperty]
    private string _truePeakText = "-inf";

    [ObservableProperty]
    private bool _isOverCeiling;

    internal MasterStripViewModel(MixerPanelViewModel mixer, int channels)
    {
        _mixer = mixer;
        Meter = new MixerMeterViewModel(channels);
    }

    /// <summary>The master meter, after the limiter.</summary>
    public MixerMeterViewModel Meter { get; }

    /// <summary>The fader's label.</summary>
    public string VolumeText => MixerPanelViewModel.FormatDb(VolumeDb);

    /// <summary>The ceiling's label.</summary>
    public string CeilingText => CeilingDb.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Momentary loudness, LUFS.</summary>
    public string MomentaryText => MixerPanelViewModel.FormatLevel(Momentary);

    /// <summary>Short-term loudness, LUFS.</summary>
    public string ShortTermText => MixerPanelViewModel.FormatLevel(ShortTerm);

    /// <summary>Integrated loudness since the last reset, LUFS.</summary>
    public string IntegratedText => MixerPanelViewModel.FormatLevel(Integrated);

    /// <summary>How far the limiter is turning the mix down, as the length of its bar from 0 to 1.</summary>
    public double ReductionLevel => Math.Clamp(-ReductionDb / ReductionRangeDb, 0.0, 1.0);

    /// <summary>The reduction's label.</summary>
    public string ReductionText => ReductionDb > -0.05 ? "0.0" : ReductionDb.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Reads the sequence's master bus without sending anything back.</summary>
    internal void Apply(MasterBus? master, Flicks playhead, bool keepValues)
    {
        _applying = true;
        try
        {
            IsVolumeAnimated = master?.Volume is KeyframedValue { IsAnimated: true };
            IsLimiterOn = master?.LimiterEnabled ?? true;
            if (!keepValues)
            {
                CeilingDb = master?.CeilingDb ?? MasterBus.DefaultCeiling;
                double db = ParamEval.Eval(master?.Volume, ParamTargets.Audio.Params[0], playhead) is ParamValue.Float level ? level.Value : 0.0;
                VolumeDb = MixerPanelViewModel.GainToFader(db);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Shows a reading, or lets the meter fall when there is none. UI thread.</summary>
    internal void Show(MeterReading? reading, double seconds)
    {
        Meter.Show(reading, seconds);
        if (reading is not { } value)
        {
            // Nothing is playing: the reduction lets go the way a bar falls.
            ReductionDb = Math.Min(0.0, ReductionDb + (MetersPanelViewModel.FallDbPerSecond * seconds));
            return;
        }

        Momentary = value.Momentary;
        ShortTerm = value.ShortTerm;
        Integrated = value.Integrated;
        ReductionDb = value.ReductionDb;

        if (value.TruePeak > 0)
        {
            _maxTruePeakDb = Math.Max(_maxTruePeakDb, 20.0 * Math.Log10(value.TruePeak));
            TruePeakText = MixerPanelViewModel.FormatLevel(_maxTruePeakDb);
            IsOverCeiling = _maxTruePeakDb > (IsLimiterOn ? CeilingDb : 0.0) + 0.05;
        }
    }

    partial void OnVolumeDbChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (!_applying && _mixer.SequenceId is { } sequence)
        {
            double db = MixerPanelViewModel.FaderToGain(value);
            _mixer.Send(MixerPanelViewModel.MasterKey, () => new SetMasterVolumeCommand(db, _mixer.MasterAnimatedAt(), sequence));
        }
    }

    partial void OnIsLimiterOnChanged(bool value)
    {
        if (!_applying && _mixer.SequenceId is { } sequence)
        {
            _mixer.Run(new SetMasterLimiterCommand(On: value, SequenceId: sequence));
        }
    }

    partial void OnCeilingDbChanged(double value)
    {
        OnPropertyChanged(nameof(CeilingText));
        if (!_applying && _mixer.SequenceId is { } sequence)
        {
            double ceiling = Math.Round(Math.Clamp(value, -24.0, 0.0), 1);
            _mixer.Send(MixerPanelViewModel.CeilingKey, () => new SetMasterLimiterCommand(Ceiling: ceiling, SequenceId: sequence));
        }
    }

    partial void OnMomentaryChanged(double value) => OnPropertyChanged(nameof(MomentaryText));

    partial void OnShortTermChanged(double value) => OnPropertyChanged(nameof(ShortTermText));

    partial void OnIntegratedChanged(double value) => OnPropertyChanged(nameof(IntegratedText));

    partial void OnReductionDbChanged(double value)
    {
        OnPropertyChanged(nameof(ReductionLevel));
        OnPropertyChanged(nameof(ReductionText));
    }

    /// <summary>Puts the fader back to 0 dB.</summary>
    [RelayCommand]
    private void ResetVolume() => VolumeDb = 0.0;

    /// <summary>Starts the integrated loudness and the true peak again, and puts the clip light out.</summary>
    [RelayCommand]
    private void ResetLoudness()
    {
        _mixer.ResetLoudness();
        _maxTruePeakDb = double.NegativeInfinity;
        TruePeakText = "-inf";
        IsOverCeiling = false;
        Integrated = double.NegativeInfinity;
        Meter.ClearClipCommand.Execute(null);
    }
}
