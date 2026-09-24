using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Selection;
using Serilog;

namespace JazzHands.App.ViewModels.Inspector;

/// <summary>
/// The inspector: everything about the selected clip that can be set, and its effects.
/// </summary>
/// <remarks>
/// It reads the project snapshot and the selection and shows the first selected clip: what it
/// is, then its own parameters section by section (a generator's, Transform, Opacity and Crop
/// for a picture, Audio for sound), then its effects in order. Every edit is a command through
/// <see cref="ISession"/>, exactly the one <c>jazz param set</c> would send, so the inspector can
/// do nothing the CLI cannot. With several clips selected, a clip's own parameters and blend
/// mode are set on all of them in one step; effects and keyframes are the first clip's.
///
/// A change from anywhere (an undo, the CLI, an MCP client) reaches it through
/// <see cref="ISession.ProjectChanged"/>. When the clip still has the same sections and effects
/// the rows are reloaded in place, so a drag in progress keeps its control; otherwise the panel
/// is built again.
/// </remarks>
public sealed partial class InspectorPanelViewModel : ToolViewModel, IParamEditor, IEffectEditor
{
    /// <summary>The docking content id.</summary>
    public const string PanelId = "inspector";

    private readonly ILogger _log = Log.ForContext<InspectorPanelViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IPreviewEngine? _preview;
    private readonly PointPicker? _picker;
    private readonly Dictionary<ParamRowViewModel, string> _pending = [];
    private readonly HashSet<ParamRowViewModel> _sending = [];

    private string _shape = string.Empty;
    private string? _clipId;
    private IReadOnlyList<string> _targets = [];
    private bool _loading;

    [ObservableProperty]
    private bool _hasTarget;

    [ObservableProperty]
    private string _heading = "Nothing selected";

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _range = string.Empty;

    [ObservableProperty]
    private string _speed = string.Empty;

    [ObservableProperty]
    private string _selectionNote = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isPicture;

    [ObservableProperty]
    private BlendMode _blend;

    /// <summary>Creates the panel.</summary>
    public InspectorPanelViewModel(
        ISession session,
        SelectionService selection,
        IUiDispatcher ui,
        IPreviewEngine? preview = null,
        PointPicker? picker = null)
        : base(PanelId, "Inspector")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _preview = preview;
        _picker = picker;

        _selection.Changed += (_, _) => _ui.Post(Rebuild);
        _session.ProjectChanged += (_, _) => _ui.Post(Changed);
        _preview?.PlayheadMoved += (_, _) => _ui.Post(RefreshValues);

        Rebuild();
    }

    /// <summary>The clip's own parameters, section by section.</summary>
    public ObservableCollection<InspectorSectionViewModel> Sections { get; } = [];

    /// <summary>The clip's effects, first to last.</summary>
    public ObservableCollection<EffectItemViewModel> Effects { get; } = [];

    /// <summary>Every blend mode, for the drop-down.</summary>
    public IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();

    /// <summary>The clip being shown, or null.</summary>
    public string? ClipId => _clipId;

    private Flicks Playhead => _preview?.Position ?? Flicks.Zero;

    /// <summary>Adds an effect to the inspected clip, for a drop from the effects panel.</summary>
    public Task AddEffectAsync(string typeId, int? index = null) =>
        _clipId is { } clipId ? RunAsync(new AddEffectCommand(clipId, typeId, index)) : Task.CompletedTask;

    /// <summary>Applies a preset to the inspected clip.</summary>
    public Task ApplyPresetAsync(string presetId) =>
        _clipId is { } clipId ? RunAsync(new ApplyEffectPresetCommand(clipId, presetId)) : Task.CompletedTask;

    /// <inheritdoc />
    public void Send(ParamRowViewModel row, string text)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Latest wins: while a value is on its way, only the newest one waits behind it, so a
        // drag sends as fast as the engine takes commands and never queues a backlog.
        _pending[row] = text;
        if (_sending.Add(row))
        {
            _ = PumpAsync(row);
        }
    }

    /// <inheritdoc />
    public void ToggleAnimation(ParamRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsAnimated)
        {
            Flicks? at = Within(row) ? Playhead : null;
            _ = RunAsync(new ClearKeyframesCommand(row.OwnerId, row.Name, at));
            return;
        }

        if (!Within(row))
        {
            Status = "Put the playhead over the clip to start animating.";
            return;
        }

        _ = RunAsync(new AddKeyframeCommand(row.OwnerId, row.Name, Playhead));
    }

    /// <inheritdoc />
    public void ToggleKeyframe(ParamRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!Within(row))
        {
            Status = "Put the playhead over the clip to add a keyframe.";
            return;
        }

        _ = RunAsync(row.HasKeyframeHere
            ? new RemoveKeyframeCommand(row.OwnerId, row.Name, Playhead)
            : new AddKeyframeCommand(row.OwnerId, row.Name, Playhead));
    }

    /// <inheritdoc />
    public void GoToKeyframe(ParamRowViewModel row, bool forward)
    {
        ArgumentNullException.ThrowIfNull(row);

        Flicks tolerance = Tolerance();
        if ((forward ? row.KeyframeAfter(Playhead, tolerance) : row.KeyframeBefore(Playhead, tolerance)) is { } time)
        {
            _ = RunAsync(new SeekCommand(time));
        }
    }

    /// <inheritdoc />
    public void Reset(ParamRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!string.Equals(row.OwnerId, _clipId, StringComparison.Ordinal))
        {
            _ = RunAsync(new ResetEffectCommand(row.OwnerId, row.Name));
            return;
        }

        var commands = new List<ICommand>();
        foreach (string target in TargetsFor(row))
        {
            if (row.IsAnimated)
            {
                commands.Add(new ClearKeyframesCommand(target, row.Name));
            }

            commands.Add(new SetParamCommand(target, row.Name, ParamValues.Format(row.Descriptor.Default)));
        }

        _ = RunAsync(commands.Count == 1 ? commands[0] : new BatchCommand([.. commands], $"Reset {row.Label}"));
    }

    /// <inheritdoc />
    public void Pick(ParamRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsColor)
        {
            // An eyedropper: the colour under the click, read as the picture is before this
            // effect (a white balance picks from what it will correct, not from its result), off
            // the UI thread because it draws a frame.
            string? before = _session.Project.Sequences.SelectMany(sequence => sequence.Tracks)
                .SelectMany(track => track.Effects.Concat(track.Clips.SelectMany(clip => clip.Effects)))
                .Any(effect => effect.Id == row.OwnerId) ? row.OwnerId : null;
            Flicks at = Playhead;

            _picker?.Begin($"{row.Label}: click the colour in the picture", point => _ = Task.Run(() =>
            {
                try
                {
                    ColorSample sample = _session.Query(new SampleColorQuery(at, point.X, point.Y, before));
                    _ui.Post(() => Send(row, sample.Hex));
                }
                catch (CommandException refusal)
                {
                    _ui.Post(() => Status = refusal.Message);
                }
            }));
            return;
        }

        _picker?.Begin(row.Label, point => Send(row, ParamRowViewModel.PointText(point)));
        UpdateMarkers();
    }

    /// <inheritdoc />
    public void SetEnabled(EffectItemViewModel effect, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _ = RunAsync(new SetEffectEnabledCommand(effect.Id, enabled));
    }

    /// <inheritdoc />
    public void Remove(EffectItemViewModel effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _ = RunAsync(new RemoveEffectCommand(effect.Id));
    }

    /// <inheritdoc />
    public void Move(EffectItemViewModel effect, int index)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _ = RunAsync(new MoveEffectCommand(effect.Id, index));
    }

    /// <inheritdoc />
    public void ResetAll(EffectItemViewModel effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _ = RunAsync(new ResetEffectCommand(effect.Id));
    }

    partial void OnBlendChanged(BlendMode value)
    {
        if (_loading || _clipId is null)
        {
            return;
        }

        ICommand[] commands = [.. _targets.Select(target => (ICommand)new SetClipBlendCommand(target, value))];
        _ = RunAsync(commands.Length == 1 ? commands[0] : new BatchCommand([.. commands], "Blend mode"));
    }

    /// <summary>The project changed: reload in place when the clip still looks the same, rebuild when not.</summary>
    private void Changed()
    {
        Project project = _session.Project;
        if (_clipId is null || project.FindClip(_clipId) is not { } found || Shape(found) != _shape)
        {
            Rebuild();
            return;
        }

        RefreshValues();
    }

    /// <summary>Builds the panel for the selection as it now is.</summary>
    private void Rebuild()
    {
        Project project = _session.Project;
        _targets = [.. _selection.Ids.Where(id => project.FindClip(id) is not null)];

        Sections.Clear();
        Effects.Clear();
        _pending.Clear();

        if (_targets.Count == 0 || project.FindClip(_targets[0]) is not { } found)
        {
            _clipId = null;
            _shape = string.Empty;
            HasTarget = false;
            Heading = "Nothing selected";
            Source = Range = Speed = SelectionNote = string.Empty;
            UpdateMarkers();
            return;
        }

        Clip clip = found.Clip;
        _clipId = clip.Id;
        _shape = Shape(found);

        // Only clips of the same family as the first take a shared edit: a volume is no use to a picture.
        bool picture = found.Track.Kind is TrackKind.Video or TrackKind.Adjustment;
        _targets = [.. _targets.Where(id => project.FindClip(id) is { } other && (other.Track.Kind is TrackKind.Video or TrackKind.Adjustment) == picture)];

        ParamOwner owner = ParamTargets.Find(project, clip.Id)!;
        foreach (EffectDescriptor section in ParamTargets.Sections(owner, EffectCatalog.Registry))
        {
            var view = new InspectorSectionViewModel(section.Name);
            foreach (ParamDescriptor parameter in section.Params)
            {
                view.Rows.Add(new ParamRowViewModel(this, clip.Id, parameter, section.Name));
            }

            Sections.Add(view);
        }

        foreach (Effect effect in EffectChains.Visible(clip, clip.Effects))
        {
            EffectDescriptor? descriptor = EffectCatalog.Registry.Find(effect.TypeId);
            var item = new EffectItemViewModel(this, effect.Id, effect.TypeId, descriptor?.Name ?? effect.TypeId, descriptor is not null);
            foreach (ParamDescriptor parameter in descriptor?.Params ?? [])
            {
                item.Rows.Add(new ParamRowViewModel(this, effect.Id, parameter, item.Name));
            }

            Effects.Add(item);
        }

        HasTarget = true;
        IsPicture = picture;
        Status = string.Empty;
        RefreshValues();
    }

    /// <summary>Loads every value from the snapshot, at the playhead, without sending anything.</summary>
    private void RefreshValues()
    {
        Project project = _session.Project;
        if (_clipId is null)
        {
            return;
        }

        if (project.FindClip(_clipId) is not { } found)
        {
            Rebuild();
            return;
        }

        Clip clip = found.Clip;
        Flicks playhead = Playhead;
        Flicks local = playhead - clip.Start;
        Flicks tolerance = Tolerance();

        _loading = true;
        try
        {
            Heading = clip.Name.Length > 0 ? clip.Name : "Untitled clip";
            Source = SourceOf(project, clip);
            Range = $"{Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}, {Timecode.FormatClock(clip.Duration)} long";
            Speed = clip.EffectiveSpeed == Rational.One && !clip.Reverse
                ? string.Empty
                : $"{clip.EffectiveSpeed.ToDouble() * 100:0.#}% speed{(clip.Reverse ? ", reversed" : string.Empty)}";
            SelectionNote = _targets.Count > 1 ? $"{_targets.Count} clips selected: their own settings change together." : string.Empty;
            Blend = clip.BlendMode;
        }
        finally
        {
            _loading = false;
        }

        ParamOwner owner = ParamTargets.Find(project, clip.Id)!;
        foreach (ParamRowViewModel row in Sections.SelectMany(section => section.Rows))
        {
            AnimatedValue? stored = ParamTargets.Get(owner, row.Name);
            row.Load(ParamEval.Eval(stored, row.Descriptor, local), stored, clip.Start, clip.Duration, playhead, tolerance);
            row.IsMixed = _targets.Count > 1 && _targets.Skip(1).Any(target => Differs(project, target, row, playhead));
        }

        IReadOnlyList<Effect> visible = EffectChains.Visible(clip, clip.Effects);
        for (int index = 0; index < Effects.Count; index++)
        {
            EffectItemViewModel item = Effects[index];
            Effect effect = visible[index];
            item.Load(effect.Enabled, index, visible.Count);

            foreach (ParamRowViewModel row in item.Rows)
            {
                AnimatedValue? stored = effect.Parameter(row.Name);
                row.Load(ParamEval.Eval(stored, row.Descriptor, local), stored, clip.Start, clip.Duration, playhead, tolerance);
            }
        }

        UpdateMarkers();
    }

    /// <summary>The picked and pickable points, for the preview to draw.</summary>
    private void UpdateMarkers()
    {
        if (_picker is null)
        {
            return;
        }

        _picker.Markers =
        [
            .. Sections.SelectMany(section => section.Rows).Concat(Effects.SelectMany(effect => effect.Rows))
                .Where(row => row.IsPoint)
                .Select(row => new PreviewMarker($"{row.Section}: {row.Label}", new System.Numerics.Vector2((float)row.X, (float)row.Y), string.Equals(_picker.Picking, row.Label, StringComparison.Ordinal))),
        ];
    }

    private async Task PumpAsync(ParamRowViewModel row)
    {
        try
        {
            while (_pending.Remove(row, out string? text))
            {
                ICommand[] commands = [.. TargetsFor(row).Select(target => (ICommand)new SetParamCommand(target, row.Name, text, AtFor(target, row)))];
                await RunAsync(commands.Length == 1 ? commands[0] : new BatchCommand([.. commands], $"Set {row.Label}")).ConfigureAwait(true);
            }
        }
        finally
        {
            _sending.Remove(row);
        }
    }

    /// <summary>A clip's own parameter goes to every selected clip; an effect's to its effect.</summary>
    private IReadOnlyList<string> TargetsFor(ParamRowViewModel row) =>
        string.Equals(row.OwnerId, _clipId, StringComparison.Ordinal) ? _targets : [row.OwnerId];

    /// <summary>The keyframe time for a set: the playhead when that owner's parameter is animated.</summary>
    private Flicks? AtFor(string ownerId, ParamRowViewModel row) =>
        ParamTargets.Find(_session.Project, ownerId) is { } owner && ParamTargets.Get(owner, row.Name) is KeyframedValue { IsAnimated: true }
            ? Playhead
            : null;

    private bool Within(ParamRowViewModel row) =>
        ParamTargets.Find(_session.Project, row.OwnerId) is { Clip: { } clip } && Playhead >= clip.Start && Playhead <= clip.End;

    private bool Differs(Project project, string target, ParamRowViewModel row, Flicks playhead)
    {
        if (ParamTargets.Find(project, target) is not { Clip: { } clip } owner)
        {
            return false;
        }

        ParamValue value = ParamEval.Eval(ParamTargets.Get(owner, row.Name), row.Descriptor, playhead - clip.Start);
        return !string.Equals(ParamValues.Format(value), row.ValueText, StringComparison.Ordinal);
    }

    private Flicks Tolerance()
    {
        Project project = _session.Project;
        Rational rate = _clipId is not null && project.FindClip(_clipId) is { } found ? project.SettingsFor(found.Sequence).FrameRate : project.Settings.FrameRate;
        return new Flicks(Flicks.FromFrames(1, rate).Value / 2);
    }

    private async Task RunAsync(ICommand command)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            _ui.Post(() => Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "The inspector's {Command} failed", CommandRegistry.NameOf(command));
            _ui.Post(() => Status = exception.Message);
        }
    }

    /// <summary>What decides whether rows can be reloaded in place: the clip, its kind, and its effects.</summary>
    private static string Shape(ClipLocation found) =>
        string.Join(
            "|",
            [found.Clip.Id, found.Track.Kind.ToString(), found.Clip.GeneratorId ?? string.Empty, .. EffectChains.Visible(found.Clip, found.Clip.Effects).Select(effect => $"{effect.Id}:{effect.TypeId}")]);

    private static string SourceOf(Project project, Clip clip)
    {
        if (clip.MediaId is { } mediaId)
        {
            return project.MediaItem(mediaId) is { } media ? $"{media.Name}, stream {clip.SourceStreamIndex}" : "Missing media";
        }

        if (clip.SequenceId is { } sequenceId)
        {
            return $"Sequence {project.Sequence(sequenceId)?.Name ?? sequenceId}";
        }

        return clip.GeneratorId is { } generator
            ? EffectCatalog.Registry.Find(generator)?.Name ?? generator
            : "Adjustment layer";
    }
}
