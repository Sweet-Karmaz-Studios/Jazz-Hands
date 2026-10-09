using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Drivers;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Selection;
using Serilog;

namespace JazzHands.App.ViewModels.Curves;

/// <summary>One keyframe of a channel as the curve editor draws it.</summary>
/// <param name="Time">When, from the clip's start.</param>
/// <param name="Value">The channel's value there.</param>
/// <param name="Interp">How the curve leaves it.</param>
/// <param name="InHandle">The bezier handle arriving, in segment space.</param>
/// <param name="OutHandle">The bezier handle leaving, in segment space.</param>
public sealed record CurveKey(Flicks Time, double Value, Interp Interp, Vector2? InHandle, Vector2? OutHandle);

/// <summary>A keyframe by channel and position, for selecting.</summary>
/// <param name="Channel">Which channel, by its place in <see cref="CurveEditorPanelViewModel.Channels"/>.</param>
/// <param name="Index">Which keyframe of it.</param>
public readonly record struct KeyRef(int Channel, int Index);

/// <summary>
/// One number that changes over time: a parameter, or one part of one (position's x), with its
/// keyframes and its curve.
/// </summary>
public sealed partial class CurveChannel : ObservableObject
{
    private readonly AnimatedValue _value;

    /// <summary>Shown in the graph.</summary>
    [ObservableProperty]
    private bool _isShown = true;

    internal CurveChannel(string ownerId, string ownerName, ParamDescriptor descriptor, int component, string color, AnimatedValue value)
    {
        OwnerId = ownerId;
        OwnerName = ownerName;
        Descriptor = descriptor;
        Component = component;
        Color = color;
        _value = value;
        Keys = value is KeyframedValue keyed
            ? [.. keyed.Keyframes.Select(keyframe => new CurveKey(keyframe.Time, Part(keyframe.Value), keyframe.Interp, keyframe.InHandle, keyframe.OutHandle))]
            : [];
    }

    /// <summary>The clip, effect or mask the parameter belongs to.</summary>
    public string OwnerId { get; }

    /// <summary>What the owner is called in the list: the clip, an effect's name, a mask.</summary>
    public string OwnerName { get; }

    /// <summary>The parameter.</summary>
    public ParamDescriptor Descriptor { get; }

    /// <summary>The parameter's name.</summary>
    public string Param => Descriptor.Name;

    /// <summary>Which part of a pair or a four: 0 for x, 1 for y and so on; -1 for a single number.</summary>
    public int Component { get; }

    /// <summary>The curve's colour.</summary>
    public string Color { get; }

    /// <summary>What the list calls it; a driven one says so.</summary>
    public string Label => (Component < 0 ? Descriptor.Label : $"{Descriptor.Label} {Suffix}") + (IsDriven ? " (driven)" : string.Empty);

    /// <summary>True when an expression works it out: drawn, with no keyframes to move.</summary>
    public bool IsDriven => _value is DrivenValue;

    /// <summary>What a driver reads (other parameters, markers), and where its owner starts on the sequence.</summary>
    internal (IDriverEnvironment? Environment, Flicks Origin) Drivers { get; init; }

    /// <summary>The keyframes, in time order.</summary>
    public IReadOnlyList<CurveKey> Keys { get; }

    /// <summary>The whole keyframed value; none for a driven one.</summary>
    internal KeyframedValue Value => _value as KeyframedValue ?? new KeyframedValue([]);

    private string Suffix => Descriptor.Type == ParamType.Float4 ? (Component switch { 0 => "x", 1 => "y", 2 => "width", _ => "height" }) : (Component == 0 ? "x" : "y");

    /// <summary>The channel's value at a time from the clip's start.</summary>
    public double ValueAt(Flicks local)
    {
        using DriverScope.Entered scope = DriverScope.Enter(Drivers.Environment, Drivers.Origin);
        return Part(ParamEval.Eval(_value, Descriptor, local));
    }

    /// <summary>A keyframe's whole value with this channel's part changed.</summary>
    internal ParamValue With(ParamValue whole, double part)
    {
        float value = (float)part;
        return whole switch
        {
            ParamValue.Float2 pair => new ParamValue.Float2(Component == 0 ? new Vector2(value, pair.Value.Y) : new Vector2(pair.Value.X, value)),
            ParamValue.Float4 four => new ParamValue.Float4(Component switch
            {
                0 => four.Value with { X = value },
                1 => four.Value with { Y = value },
                2 => four.Value with { Z = value },
                _ => four.Value with { W = value },
            }),
            ParamValue.Int => new ParamValue.Int((int)Math.Round(part)),
            _ => new ParamValue.Float(value),
        };
    }

    private double Part(ParamValue value) => value switch
    {
        ParamValue.Float number => number.Value,
        ParamValue.Int whole => whole.Value,
        ParamValue.Float2 pair => Component == 0 ? pair.Value.X : pair.Value.Y,
        ParamValue.Float4 four => Component switch { 0 => four.Value.X, 1 => four.Value.Y, 2 => four.Value.Z, _ => four.Value.W },
        _ => 0,
    };
}

/// <summary>
/// The Curve Editor: every animated number of the selected clip, its effects and its masks, drawn
/// as curves over the clip's length, with keyframes to select, drag, ease and copy.
/// </summary>
/// <remarks>
/// <para>
/// Everything it changes is a keyframe command the CLI could send, in local time
/// (<c>--local</c>): a drag of the selected keyframes is one <c>batch</c> of
/// <c>keyframe.set-value</c> and <c>keyframe.move</c> sent when the button comes up, so it is one
/// undo step; while the drag goes on the graph draws them where they will land. An ease preset is
/// <c>keyframe.set-interp</c>, a handle <c>keyframe.set-handles</c>, a paste <c>keyframe.add</c>.
/// </para>
/// <para>
/// Numbers of different sizes (an opacity from 0 to 1 and a position in pixels) share one graph
/// badly, so <see cref="IsNormalized"/> draws each curve from its own lowest to its own highest.
/// Times snap to the sequence's frames unless <see cref="SnapToFrames"/> is off.
/// </para>
/// </remarks>
public sealed partial class CurveEditorPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "curves";

    private static readonly string[] Palette = ["#E8704F", "#58B368", "#4F8FE8", "#E8C24F", "#B36BD9", "#4FD1C5", "#E86B9E", "#9AA0A6"];

    private readonly ILogger _log = Log.ForContext<CurveEditorPanelViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly Func<Flicks> _playhead;
    private readonly HashSet<KeyRef> _selected = [];
    private readonly List<Copied> _copied = [];
    private int _refreshQueued;

    /// <summary>Each curve drawn from its own range rather than all on one scale.</summary>
    [ObservableProperty]
    private bool _isNormalized;

    /// <summary>Dragged keyframes land on the sequence's frames.</summary>
    [ObservableProperty]
    private bool _snapToFrames = true;

    /// <summary>Why the last change did not take, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Goes up whenever what the graph draws changes, for the graph to redraw.</summary>
    [ObservableProperty]
    private int _revision;

    /// <summary>Creates the panel.</summary>
    /// <param name="session">Where the commands go.</param>
    /// <param name="selection">Whose curves show.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="playhead">Where a paste lands.</param>
    public CurveEditorPanelViewModel(ISession session, SelectionService selection, IUiDispatcher ui, Func<Flicks>? playhead = null)
        : base(PanelId, "Curves")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);
        _session = session;
        _selection = selection;
        _playhead = playhead ?? (() => Flicks.Zero);

        void Queue()
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                ui.Post(() =>
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    Refresh();
                });
            }
        }

        _session.ProjectChanged += (_, _) => Queue();
        _selection.Changed += (_, _) => Queue();
        Refresh();
    }

    /// <summary>The animated numbers of the selected clip.</summary>
    public ObservableCollection<CurveChannel> Channels { get; } = [];

    /// <summary>The clip whose curves show, or null.</summary>
    public string? ClipId { get; private set; }

    /// <summary>The clip's length: the graph's time axis.</summary>
    public Flicks Length { get; private set; }

    /// <summary>The sequence's frame rate, for snapping and the time ruler.</summary>
    public Rational FrameRate { get; private set; } = Rational.Fps30;

    /// <summary>The selected keyframes.</summary>
    public IReadOnlyCollection<KeyRef> Selected => _selected;

    /// <summary>How far the selected keyframes are being dragged in time, for drawing them there.</summary>
    public Flicks DragTime { get; private set; }

    /// <summary>How far they are being dragged in value, in graph units: normalized when <see cref="IsNormalized"/>.</summary>
    public double DragValue { get; private set; }

    /// <summary>True while the selection is being dragged.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>True when there is something to paste.</summary>
    public bool CanPaste => _copied.Count > 0;

    /// <summary>What the panel says when there is nothing to draw.</summary>
    public string Empty => ClipId is null
        ? "Select a clip to see its keyframed parameters as curves."
        : "Nothing on this clip is keyframed. Use a parameter's stopwatch in the Inspector, or keyframe.add.";

    /// <summary>True when there are curves to draw.</summary>
    public bool HasCurves => Channels.Count > 0;

    /// <summary>True when there are none, and the panel says why.</summary>
    public bool ShowEmpty => Channels.Count == 0;

    /// <summary>The graph's value range: over every shown curve, or 0 to 1 when normalized.</summary>
    public (double Min, double Max) Range()
    {
        if (IsNormalized)
        {
            return (-0.05, 1.05);
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (CurveChannel channel in Channels.Where(channel => channel.IsShown))
        {
            (double low, double high) = Extent(channel);
            min = Math.Min(min, low);
            max = Math.Max(max, high);
        }

        if (min > max)
        {
            return (0, 1);
        }

        double pad = Math.Max((max - min) * 0.08, 1e-3);
        return (min - pad, max + pad);
    }

    /// <summary>A channel's lowest and highest value over the clip, sampled and at its keyframes.</summary>
    public (double Min, double Max) Extent(CurveChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (CurveKey key in channel.Keys)
        {
            min = Math.Min(min, key.Value);
            max = Math.Max(max, key.Value);
        }

        for (int step = 0; step <= 64; step++)
        {
            double value = channel.ValueAt(new Flicks(Length.Value * step / 64));
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return max - min < 1e-9 ? (min - 0.5, max + 0.5) : (min, max);
    }

    /// <summary>A channel's value on the graph's scale: itself, or 0 to 1 across its own range.</summary>
    public double ToGraph(CurveChannel channel, double value)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsNormalized)
        {
            return value;
        }

        (double min, double max) = Extent(channel);
        return (value - min) / (max - min);
    }

    /// <summary>A distance on the graph's scale as a distance in a channel's own units.</summary>
    public double FromGraphDistance(CurveChannel channel, double distance)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsNormalized)
        {
            return distance;
        }

        (double min, double max) = Extent(channel);
        return distance * (max - min);
    }

    /// <summary>Reads the selected clip's curves.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        HashSet<(string, string, int)> hidden = [.. Channels.Where(channel => !channel.IsShown).Select(channel => (channel.OwnerId, channel.Param, channel.Component))];
        string? clipId = Playback.SelectedPicture.Of(project, _selection.Ids)?.Clip.Id;
        if (clipId != ClipId)
        {
            _selected.Clear();
        }

        ClipId = clipId;
        Channels.Clear();

        if (clipId is not null && project.FindClip(clipId) is { } location)
        {
            Clip clip = location.Clip;
            Length = clip.Duration;
            FrameRate = project.SettingsFor(location.Sequence).FrameRate;
            EffectRegistry registry = Engine.Effects.EffectCatalog.Registry;
            var drivers = new ProjectDriverEnvironment(project, location.Sequence);

            IEnumerable<(string Id, string Name)> owners =
            [
                (clip.Id, "Clip"),
                .. clip.Effects.Select(effect => (effect.Id, registry.Find(effect.TypeId)?.Name ?? effect.TypeId)),
                .. clip.Masks.Select((mask, index) => (mask.Id, $"Mask {index + 1}")),
            ];

            foreach ((string ownerId, string ownerName) in owners)
            {
                if (ParamTargets.Find(project, ownerId) is not { } owner)
                {
                    continue;
                }

                foreach (ParamDescriptor descriptor in ParamTargets.Params(owner, registry))
                {
                    AnimatedValue? stored = ParamTargets.Get(owner, descriptor.Name);
                    if (stored is not (KeyframedValue { IsAnimated: true } or DrivenValue))
                    {
                        continue;
                    }

                    int parts = descriptor.Type switch
                    {
                        ParamType.Float or ParamType.Int => 1,
                        ParamType.Float2 or ParamType.Point => 2,
                        ParamType.Float4 => 4,
                        _ => 0,
                    };

                    for (int part = 0; part < parts; part++)
                    {
                        int component = parts == 1 ? -1 : part;
                        var channel = new CurveChannel(ownerId, ownerName, descriptor, component, Palette[Channels.Count % Palette.Length], stored)
                        {
                            Drivers = (drivers, clip.Start),
                            IsShown = !hidden.Contains((ownerId, descriptor.Name, component)),
                        };
                        channel.PropertyChanged += (_, _) => Revision++;
                        Channels.Add(channel);
                    }
                }
            }
        }
        else
        {
            Length = Flicks.Zero;
        }

        _selected.RemoveWhere(key => key.Channel >= Channels.Count || key.Index >= Channels[key.Channel].Keys.Count);
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(HasCurves));
        OnPropertyChanged(nameof(ShowEmpty));
        Revision++;
    }

    /// <summary>Selects a keyframe, alone or added to the selection.</summary>
    public void Select(KeyRef key, bool add)
    {
        if (!add)
        {
            _selected.Clear();
        }

        _selected.Add(key);
        Revision++;
    }

    /// <summary>Selects every keyframe a test says yes to: a box drawn on the graph.</summary>
    public void SelectWhere(Func<KeyRef, bool> inside, bool add)
    {
        ArgumentNullException.ThrowIfNull(inside);
        if (!add)
        {
            _selected.Clear();
        }

        for (int channel = 0; channel < Channels.Count; channel++)
        {
            if (!Channels[channel].IsShown)
            {
                continue;
            }

            for (int index = 0; index < Channels[channel].Keys.Count; index++)
            {
                var key = new KeyRef(channel, index);
                if (inside(key))
                {
                    _selected.Add(key);
                }
            }
        }

        Revision++;
    }

    /// <summary>Clears the selection.</summary>
    public void ClearSelection()
    {
        _selected.Clear();
        Revision++;
    }

    /// <summary>The selection is being dragged this far: draws it there, sending nothing yet.</summary>
    public void DragBy(Flicks time, double value)
    {
        IsDragging = true;
        DragTime = SnapToFrames ? Flicks.FromFrames(time.ToFrames(FrameRate, RoundingMode.Nearest), FrameRate) : time;
        DragValue = value;
        Revision++;
    }

    /// <summary>Stops a drag without changing anything.</summary>
    public void CancelDrag()
    {
        IsDragging = false;
        DragTime = Flicks.Zero;
        DragValue = 0;
        Revision++;
    }

    /// <summary>Sends the drag as one undo step: new values, then new times.</summary>
    public Task CommitDragAsync()
    {
        Flicks dt = DragTime;
        double dv = DragValue;
        CancelDrag();
        if (ClipId is null || _selected.Count == 0 || (dt == Flicks.Zero && Math.Abs(dv) < 1e-12))
        {
            return Task.CompletedTask;
        }

        var commands = new List<ICommand>();
        var moves = new List<(Flicks At, ICommand Move)>();

        // A keyframe of a pair is one keyframe: its parts' changes are made together.
        foreach (IGrouping<(string Owner, string Param, int Index), KeyRef> keyframe in _selected.GroupBy(key => (Channels[key.Channel].OwnerId, Channels[key.Channel].Param, key.Index)))
        {
            CurveChannel first = Channels[keyframe.First().Channel];
            Keyframe original = first.Value.Keyframes[keyframe.Key.Index];
            if (Math.Abs(dv) >= 1e-12)
            {
                ParamValue value = original.Value;
                foreach (KeyRef part in keyframe)
                {
                    CurveChannel channel = Channels[part.Channel];
                    value = channel.With(value, channel.Keys[part.Index].Value + FromGraphDistance(channel, dv));
                }

                value = ParamValues.Clamp(first.Descriptor, value);
                commands.Add(new SetKeyframeValueCommand(keyframe.Key.Owner, keyframe.Key.Param, original.Time, ParamValues.Format(value), Local: true));
            }

            if (dt != Flicks.Zero)
            {
                Flicks to = Flicks.Max(Flicks.Zero, original.Time + dt);
                moves.Add((original.Time, new MoveKeyframeCommand(keyframe.Key.Owner, keyframe.Key.Param, original.Time, to, Local: true)));
            }
        }

        // Later ones first when moving later, so none lands on one still waiting to move.
        commands.AddRange((dt > Flicks.Zero ? moves.OrderByDescending(move => move.At) : moves.OrderBy(move => move.At)).Select(move => move.Move));
        return RunAsync(commands, "Move keyframes");
    }

    /// <summary>Gives the selected keyframes an ease: hold, linear, ease-in, ease-out, ease-in-out or bezier.</summary>
    [RelayCommand]
    public Task EaseAsync(Interp interp)
    {
        IEnumerable<ICommand> commands = SelectedKeyframes().Select(key => (ICommand)new SetKeyframeInterpCommand(key.Owner, key.Param, key.Keyframe.Time, interp, Local: true));
        return RunAsync([.. commands], "Ease keyframes");
    }

    /// <summary>Removes the selected keyframes.</summary>
    [RelayCommand]
    public Task DeleteAsync()
    {
        ICommand[] commands = [.. SelectedKeyframes().Select(key => (ICommand)new RemoveKeyframeCommand(key.Owner, key.Param, key.Keyframe.Time, Local: true))];
        _selected.Clear();
        return RunAsync(commands, "Remove keyframes");
    }

    /// <summary>Copies the selected keyframes.</summary>
    [RelayCommand]
    public void Copy()
    {
        _copied.Clear();
        foreach ((string owner, string param, Keyframe keyframe) in SelectedKeyframes())
        {
            _copied.Add(new Copied(owner, owner == ClipId, param, keyframe));
        }

        OnPropertyChanged(nameof(CanPaste));
    }

    /// <summary>Pastes the copied keyframes at the playhead, keeping their spacing: onto the same parameters, or this clip's own for a clip's.</summary>
    [RelayCommand]
    public Task PasteAsync()
    {
        if (ClipId is not { } clipId || _copied.Count == 0 || _session.Project.FindClip(clipId) is not { } found)
        {
            return Task.CompletedTask;
        }

        Flicks earliest = _copied.Min(copied => copied.Keyframe.Time);
        Flicks at = Flicks.Max(Flicks.Zero, _playhead() - found.Clip.Start);
        var commands = new List<ICommand>();
        foreach (Copied copied in _copied)
        {
            string? owner = copied.FromClip ? clipId : ParamTargets.Find(_session.Project, copied.Owner) is not null ? copied.Owner : null;
            if (owner is null)
            {
                continue;
            }

            commands.Add(new AddKeyframeCommand(owner, copied.Param, at + (copied.Keyframe.Time - earliest), ParamValues.Format(copied.Keyframe.Value), copied.Keyframe.Interp, Local: true));
        }

        return RunAsync(commands, "Paste keyframes");
    }

    /// <summary>
    /// Sets a keyframe's bezier handle from where it was dragged to on the graph, in the channel's
    /// own time and value; kept inside its segment in time.
    /// </summary>
    public Task SetHandleAsync(KeyRef key, bool outgoing, Flicks time, double value)
    {
        CurveChannel channel = Channels[key.Channel];
        int other = outgoing ? key.Index + 1 : key.Index - 1;
        if (other < 0 || other >= channel.Keys.Count)
        {
            return Task.CompletedTask;
        }

        CurveKey from = channel.Keys[outgoing ? key.Index : other];
        CurveKey to = channel.Keys[outgoing ? other : key.Index];
        double span = (to.Time - from.Time).Value;
        double rise = to.Value - from.Value;
        double t = Math.Clamp(span <= 0 ? 0 : (time - from.Time).Value / span, 0, 1);
        double v = Math.Abs(rise) < 1e-9 ? (outgoing ? 0 : 1) : (value - from.Value) / rise;
        string handle = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(t, 4)}, {Math.Round(v, 4)}");
        CurveKey target = channel.Keys[key.Index];
        return RunAsync(
            [new SetKeyframeHandlesCommand(channel.OwnerId, channel.Param, target.Time, In: outgoing ? null : handle, Out: outgoing ? handle : null, Local: true)],
            "Shape curve");
    }

    private IEnumerable<(string Owner, string Param, Keyframe Keyframe)> SelectedKeyframes() =>
        _selected
            .Select(key => (Channels[key.Channel].OwnerId, Channels[key.Channel].Param, Channels[key.Channel].Value.Keyframes[key.Index]))
            .Distinct();

    private async Task RunAsync(IReadOnlyList<ICommand> commands, string label)
    {
        if (commands.Count == 0)
        {
            return;
        }

        try
        {
            CommandResult result = await _session.ExecuteAsync(commands.Count == 1 ? commands[0] : new BatchCommand([.. commands], label)).ConfigureAwait(true);
            Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "The curve editor's {Label} failed", label);
            Status = exception.Message;
        }
    }

    partial void OnIsNormalizedChanged(bool value) => Revision++;

    /// <summary>A keyframe on the clipboard, with where it came from.</summary>
    private sealed record Copied(string Owner, bool FromClip, string Param, Keyframe Keyframe);
}
