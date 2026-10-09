using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Drivers;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
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

    /// <summary>Sections a person folded, by heading: true while folded.</summary>
    private readonly Dictionary<string, bool> _folded = new(StringComparer.Ordinal);

    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<string, ParamDescriptor[]> _pluginRows = new(StringComparer.Ordinal);
    private readonly IPreviewEngine? _preview;
    private readonly PointPicker? _picker;
    private readonly Dictionary<ParamRowViewModel, string> _pending = [];
    private readonly HashSet<ParamRowViewModel> _sending = [];

    private string _shape = string.Empty;
    private string? _clipId;
    private string? _transitionId;
    private string? _nodeId;
    private string? _trackId;
    private IReadOnlyList<string> _targets = [];
    private bool _loading;

    [ObservableProperty]
    private bool _hasTarget;

    [ObservableProperty]
    private bool _canMaskClip;

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

    /// <summary>The text section, when the clip is a title; null otherwise.</summary>
    [ObservableProperty]
    private TitleSectionViewModel? _titleSection;

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

    /// <summary>The inspected clip's own masks, which limit what of it is seen.</summary>
    public ObservableCollection<MaskItemViewModel> ClipMasks { get; } = [];

    /// <summary>Every blend mode, for the drop-down.</summary>
    public IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();

    /// <summary>The clip being shown, or null.</summary>
    public string? ClipId => _clipId;

    /// <summary>The transition being shown, when one is selected and no clip is; null otherwise.</summary>
    public string? TransitionId => _transitionId;

    /// <summary>A transition's duration, as the inspector shows it: seconds, sent as <c>transition.set --dur</c>.</summary>
    internal static ParamDescriptor TransitionDuration { get; } = new(
        "duration", ParamType.Float, new ParamValue.Float(1), "Duration",
        "How long it runs, in seconds. It is fitted to what the clips have room for.",
        Min: 0.01, Max: 600, SliderMax: 5, Unit: "s", Animatable: false);

    /// <summary>A transition's place on its cut, sent as <c>transition.set --alignment</c>.</summary>
    internal static ParamDescriptor TransitionAlignmentParam { get; } = new(
        "alignment", ParamType.Enum, new ParamValue.Enum("centered"), "Alignment",
        "Centred on the cut, ending at it, or starting at it.",
        Animatable: false, Choices: new EquatableArray<string>(["centered", "end-of-left", "start-of-right"]));

    /// <summary>The fade shapes as the commands spell them, and as the rows offer them.</summary>
    private static readonly (string Name, Interp Curve)[] FadeCurves =
    [
        ("linear", Interp.Linear),
        ("ease-in-out", Interp.EaseInOut),
        ("ease-in", Interp.EaseIn),
        ("ease-out", Interp.EaseOut),
        ("bezier", Interp.Bezier),
    ];

    /// <summary>A sound clip's fade in, in seconds, sent as <c>audio.set-fade-in --dur</c>.</summary>
    internal static ParamDescriptor FadeInLength { get; } = FadeLength("fade-in", "Fade in", "How long the sound takes to rise from silence, in seconds; 0 for none.");

    /// <summary>The fade in's shape, sent as <c>audio.set-fade-in --curve</c>.</summary>
    internal static ParamDescriptor FadeInShape { get; } = FadeShape("fade-in-shape", "Fade in shape");

    /// <summary>A sound clip's fade out, in seconds, sent as <c>audio.set-fade-out --dur</c>.</summary>
    internal static ParamDescriptor FadeOutLength { get; } = FadeLength("fade-out", "Fade out", "How long the sound takes to fall to silence at the end, in seconds; 0 for none.");

    /// <summary>The fade out's shape, sent as <c>audio.set-fade-out --curve</c>.</summary>
    internal static ParamDescriptor FadeOutShape { get; } = FadeShape("fade-out-shape", "Fade out shape");

    /// <summary>Whether a sound clip keeps its pitch at a speed (Phase 36), sent as <c>clip.set-keep-pitch</c>.</summary>
    internal static ParamDescriptor KeepPitchParam { get; } = new(
        "keep-pitch", ParamType.Bool, new ParamValue.Bool(true), "Keep pitch",
        "At a speed other than normal the sound keeps its pitch; off, it goes up and down with the speed, like tape.",
        Animatable: false);

    /// <summary>Whether a picture clip blurs with its speed (Phase 45), sent as <c>clip.set-speed-blur</c>.</summary>
    internal static ParamDescriptor SpeedBlurParam { get; } = new(
        "speed-blur", ParamType.Bool, new ParamValue.Bool(false), "Speed blur",
        "Blur that follows the speed: where the clip runs fast the picture streaks as a camera's shutter would at that speed; at normal speed it stays sharp.",
        Animatable: false);

    /// <summary>Above what speed a sound clip is silent (Phase 45), sent as <c>clip.set-fast-mute</c>.</summary>
    internal static ParamDescriptor FastMuteParam { get; } = new(
        "fast-mute", ParamType.Enum, new ParamValue.Enum("never"), "Mute above",
        "For speed ramps: the sound is silent where the clip plays faster than this, and comes back after.",
        Animatable: false, Choices: new EquatableArray<string>([.. FastMuteChoices.Select(pair => pair.Name)]));

    private static (string Name, Rational? Speed)[] FastMuteChoices =>
    [
        ("never", null),
        ("1.5x", new Rational(3, 2)),
        ("2x", new Rational(2, 1)),
        ("3x", new Rational(3, 1)),
        ("4x", new Rational(4, 1)),
    ];

    /// <summary>Whether a picture clip is a 3D layer (Phase 47), sent as <c>clip.set-3d</c>.</summary>
    internal static ParamDescriptor ThreeDParam { get; } = new(
        "3d", ParamType.Bool, new ParamValue.Bool(false), "3D layer",
        "Gives the clip depth, a turn about X and Y and a material, seen through the sequence's camera and lit by its lights. Off makes it flat again.",
        Animatable: false);

    /// <summary>Whether a 3D layer takes the scene's lights (Phase 47), sent as <c>clip.set-3d --lights</c>.</summary>
    internal static ParamDescriptor LightsParam { get; } = new(
        "lights", ParamType.Bool, new ParamValue.Bool(true), "Takes lights",
        "Lit by the scene's lights; off shows the picture as it is.",
        Animatable: false);

    /// <summary>Whether a 3D layer throws shadows (Phase 47), sent as <c>clip.set-3d --casts-shadows</c>.</summary>
    internal static ParamDescriptor CastsShadowsParam { get; } = new(
        "casts-shadows", ParamType.Bool, new ParamValue.Bool(false), "Casts shadows",
        "Throws a shadow from lights that cast them, onto the layers behind.",
        Animatable: false);

    /// <summary>Whether a 3D layer is darkened by shadows (Phase 47), sent as <c>clip.set-3d --accepts-shadows</c>.</summary>
    internal static ParamDescriptor AcceptsShadowsParam { get; } = new(
        "accepts-shadows", ParamType.Bool, new ParamValue.Bool(true), "Takes shadows",
        "Darkened by the shadows other layers throw on it.",
        Animatable: false);

    /// <summary>How a picture clip shows the moments between its source frames (Phase 42), sent as <c>clip.set-retime</c>.</summary>
    internal static ParamDescriptor RetimeParam { get; } = new(
        "retime", ParamType.Enum, new ParamValue.Enum("nearest"), "Between frames",
        "When slowed: nearest shows the frame before, which steps; blend crossfades the two either side; optical flow moves them along the motion, the smoothest.",
        Animatable: false, Choices: new EquatableArray<string>(["nearest", "blend", "optical-flow"]));

    private Flicks Playhead => _preview?.Position ?? Flicks.Zero;

    private static ParamDescriptor FadeLength(string name, string label, string description) => new(
        name, ParamType.Float, new ParamValue.Float(0), label, description,
        Min: 0, Max: 600, SliderMax: 5, Unit: "s", Animatable: false);

    private static ParamDescriptor FadeShape(string name, string label) => new(
        name, ParamType.Enum, new ParamValue.Enum("linear"), label,
        "Linear; ease-in-out for a smooth start and end; ease-in to start slowly; ease-out to start quickly; bezier for a steeper S.",
        Animatable: false, Choices: new EquatableArray<string>([.. FadeCurves.Select(pair => pair.Name)]));

    private static bool IsFadeRow(ParamRowViewModel row) =>
        ReferenceEquals(row.Descriptor, ThreeDParam) || ReferenceEquals(row.Descriptor, LightsParam) || ReferenceEquals(row.Descriptor, CastsShadowsParam) || ReferenceEquals(row.Descriptor, AcceptsShadowsParam)
        || ReferenceEquals(row.Descriptor, KeepPitchParam) || ReferenceEquals(row.Descriptor, RetimeParam) || ReferenceEquals(row.Descriptor, SpeedBlurParam) || ReferenceEquals(row.Descriptor, FastMuteParam) || ReferenceEquals(row.Descriptor, FadeInLength) || ReferenceEquals(row.Descriptor, FadeInShape)
        || ReferenceEquals(row.Descriptor, FadeOutLength) || ReferenceEquals(row.Descriptor, FadeOutShape);

    /// <summary>Adds an effect to the inspected clip, for a drop from the effects panel.</summary>
    public Task AddEffectAsync(string typeId, int? index = null) =>
        (_clipId ?? _trackId) is { } ownerId ? RunAsync(new AddEffectCommand(ownerId, typeId, index)) : Task.CompletedTask;

    /// <summary>Applies a preset to the inspected clip.</summary>
    public Task ApplyPresetAsync(string presetId) =>
        (_clipId ?? _trackId) is { } ownerId ? RunAsync(new ApplyEffectPresetCommand(ownerId, presetId)) : Task.CompletedTask;

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
            Status = $"Put the playhead over {Over(row)} to start animating.";
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
            Status = $"Put the playhead over {Over(row)} to add a keyframe.";
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

        if (string.Equals(row.OwnerId, _transitionId, StringComparison.Ordinal) || string.Equals(row.OwnerId, _trackId, StringComparison.Ordinal) || IsFadeRow(row)
            || ParamTargets.Find(_session.Project, row.OwnerId) is { Kind: ParamOwnerKind.Mask })
        {
            Send(row, ParamValues.Format(row.Descriptor.Default));
            return;
        }

        if (!string.Equals(row.OwnerId, _clipId, StringComparison.Ordinal))
        {
            _ = RunAsync(new ResetEffectCommand(row.OwnerId, row.Name));
            return;
        }

        var commands = new List<ICommand>();
        foreach (string target in TargetsFor(row))
        {
            if (row.IsDriven)
            {
                commands.Add(new ClearDriverCommand(target, row.Name));
            }

            if (row.IsAnimated)
            {
                commands.Add(new ClearKeyframesCommand(target, row.Name));
            }

            commands.Add(new SetParamCommand(target, row.Name, ParamValues.Format(row.Descriptor.Default)));
        }

        _ = RunAsync(commands.Count == 1 ? commands[0] : new BatchCommand([.. commands], $"Reset {row.Label}"));
    }

    /// <inheritdoc />
    public void SetDriver(ParamRowViewModel row, string expression)
    {
        ArgumentNullException.ThrowIfNull(row);
        ICommand[] commands = [.. TargetsFor(row).Select(target => (ICommand)new SetDriverCommand(target, row.Name, expression))];
        _ = RunAsync(commands.Length == 1 ? commands[0] : new BatchCommand(commands, $"Drive {row.Label}"));
    }

    /// <inheritdoc />
    public void ClearDriver(ParamRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ICommand[] commands = [.. TargetsFor(row).Select(target => (ICommand)new ClearDriverCommand(target, row.Name))];
        _ = RunAsync(commands.Length == 1 ? commands[0] : new BatchCommand(commands, $"Stop driving {row.Label}"));
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
        if (_nodeId is { } nodeId)
        {
            // A comp node of another type has other parameters; anything else reloads in place.
            if (ParamTargets.Find(project, nodeId) is { Kind: ParamOwnerKind.Effect, Effect: { } node, Graph.Comp: not null } && $"node|{node.Id}|{node.TypeId}" == _shape)
            {
                RefreshValues();
            }
            else
            {
                Rebuild();
            }

            return;
        }

        if (_transitionId is { } transitionId)
        {
            // A new type has other parameters; anything else reloads in place.
            if (ParamTargets.Find(project, transitionId) is { Transition: { } transition } && $"transition|{transition.Id}|{transition.TypeId}" == _shape)
            {
                RefreshValues();
            }
            else
            {
                Rebuild();
            }

            return;
        }

        if (_trackId is { } trackId)
        {
            // Effects added, removed or reordered rebuild the panel; anything else reloads in place.
            if (ParamTargets.Find(project, trackId) is { Kind: ParamOwnerKind.Track } track && TrackShape(track.Track) == _shape)
            {
                RefreshValues();
            }
            else
            {
                Rebuild();
            }

            return;
        }

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
        TitleSection = null;
        Effects.Clear();
        ClipMasks.Clear();
        CanMaskClip = false;
        _pending.Clear();

        _transitionId = null;
        _nodeId = null;
        _trackId = null;
        if (_targets.Count == 0 && _selection.Ids.Select(id => ParamTargets.Find(project, id)).FirstOrDefault(owner => owner?.Kind == ParamOwnerKind.Transition) is { } transition)
        {
            BuildTransition(transition);
            return;
        }

        // A node of a comp graph (Phase 49), selected in the Nodes panel.
        if (_targets.Count == 0 && _selection.Ids.Select(id => ParamTargets.Find(project, id)).FirstOrDefault(owner => owner is { Kind: ParamOwnerKind.Effect, Graph.Comp: not null }) is { } node)
        {
            BuildNode(node);
            return;
        }

        // A track, selected by its header: its own effects.
        if (_targets.Count == 0 && _selection.Ids.Select(id => ParamTargets.Find(project, id)).FirstOrDefault(owner => owner?.Kind == ParamOwnerKind.Track) is { } track)
        {
            BuildTrack(track);
            return;
        }

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
        bool title = string.Equals(clip.GeneratorId, TitleParams.GeneratorId, StringComparison.Ordinal);
        TitleSection = title ? new TitleSectionViewModel(clip.Id, RunAsync, () => _session.Query(new ListFontsQuery())) : null;
        foreach (EffectDescriptor section in ParamTargets.Sections(owner, EffectCatalog.Registry))
        {
            if (title && string.Equals(section.TypeId, TitleParams.GeneratorId, StringComparison.Ordinal))
            {
                AddTitleSections(clip.Id, section);
                continue;
            }

            var view = Section(section.Name);
            foreach (ParamDescriptor parameter in section.Params)
            {
                view.Rows.Add(new ParamRowViewModel(this, clip.Id, parameter, section.Name));
            }

            // A picture on a video track can be made 3D (Phase 47): the switch sits under its
            // place, and a 3D layer's material switches under its depth and turns.
            // Text, a shape or a model is always 3D, so it has no switch, only its shadow switches.
            if (ReferenceEquals(section, ParamTargets.Transform) && found.Track.Kind == TrackKind.Video && !SceneObjects.IsMesh(clip.GeneratorId))
            {
                view.Rows.Add(new ParamRowViewModel(this, clip.Id, ThreeDParam, section.Name));
            }
            else if (ReferenceEquals(section, ParamTargets.Space) || ReferenceEquals(section, ParamTargets.MeshSpace))
            {
                foreach (ParamDescriptor material in (ParamDescriptor[])[LightsParam, CastsShadowsParam, AcceptsShadowsParam])
                {
                    view.Rows.Add(new ParamRowViewModel(this, clip.Id, material, section.Name));
                }
            }

            Sections.Add(view);
        }

        if (found.Track.Kind == TrackKind.Audio)
        {
            // Keeping the pitch at a speed (Phase 36) is the clip's own too: clip.set-keep-pitch.
            var speed = Section("Speed");
            speed.Rows.Add(new ParamRowViewModel(this, clip.Id, KeepPitchParam, speed.Title));
            speed.Rows.Add(new ParamRowViewModel(this, clip.Id, FastMuteParam, speed.Title));
            Sections.Add(speed);

            // The fades are the clip's own, not parameters: their rows send audio.set-fade-in and -out.
            var fades = Section("Fades");
            foreach (ParamDescriptor parameter in (ParamDescriptor[])[FadeInLength, FadeInShape, FadeOutLength, FadeOutShape])
            {
                fades.Rows.Add(new ParamRowViewModel(this, clip.Id, parameter, fades.Title));
            }

            Sections.Add(fades);
        }
        else if (found.Track.Kind == TrackKind.Video && clip.IsMedia && !clip.IsHold)
        {
            // How a slowed picture shows the moments between its frames (Phase 42): clip.set-retime.
            var speed = Section("Speed");
            speed.Rows.Add(new ParamRowViewModel(this, clip.Id, RetimeParam, speed.Title));
            speed.Rows.Add(new ParamRowViewModel(this, clip.Id, SpeedBlurParam, speed.Title));
            Sections.Add(speed);
        }

        // A picture on a video or adjustment track can be masked, and so can its picture effects.
        bool maskable = found.Track.Kind is TrackKind.Video or TrackKind.Adjustment && !SceneObjects.Is(clip);
        if (maskable)
        {
            CanMaskClip = true;
            AddMaskItems(ClipMasks, clip.Masks);
        }

        AddEffectItems(EffectChains.Visible(clip, clip.Effects), maskable);

        HasTarget = true;
        IsPicture = picture;
        Status = string.Empty;
        RefreshValues();
    }

    /// <summary>A plugin effect's heading: the plugin's name as the scan found it, or its id.</summary>
    private string PluginName(Effect effect)
    {
        string id = effect.Parameter("plugin") is StaticValue { Value: ParamValue.Text text } ? text.Value : "Plugin";
        try
        {
            return _session.Query(new ListPluginsQuery()).Plugins.FirstOrDefault(plugin => plugin.Id == id)?.Name ?? id;
        }
        catch (CommandException)
        {
            return id;
        }
    }

    /// <summary>
    /// A plugin effect's own parameters as rows (Phase 46): <c>plugin.params</c> asks the plugin, in
    /// a process of its own, which takes a moment, so the first time the rows come once it answers;
    /// after that they are kept for the effect.
    /// </summary>
    private ParamDescriptor[] PluginRows(Effect effect)
    {
        string key = effect.Id + "|" + (effect.Parameter("plugin") is StaticValue { Value: ParamValue.Text text } ? text.Value : string.Empty);
        if (_pluginRows.TryGetValue(key, out ParamDescriptor[]? known))
        {
            return known;
        }

        _pluginRows[key] = [];
        string effectId = effect.Id;
        _ = Task.Run(() =>
        {
            ParamDescriptor[] rows;
            try
            {
                rows = [.. _session.Query(new PluginParamsQuery(effectId)).Select(parameter => new ParamDescriptor(
                    parameter.Name,
                    ParamType.Float,
                    new ParamValue.Float((float)parameter.Default),
                    parameter.Label,
                    $"The plugin's {parameter.Label}, {parameter.Min:0.##} to {parameter.Max:0.##}.",
                    Min: parameter.Min,
                    Max: parameter.Max))];
            }
            catch (CommandException error)
            {
                rows = [];
                _ui.Post(() => Status = error.Message);
            }

            _ui.Post(() =>
            {
                _pluginRows[key] = rows;
                Rebuild();
            });
        });
        return [];
    }

    /// <summary>
    /// A section, open or folded as it was the last time one of its name was shown: fold Crop on
    /// one clip and it stays folded on the next, for as long as the editor runs.
    /// </summary>
    private InspectorSectionViewModel Section(string title, bool open = true)
    {
        var section = new InspectorSectionViewModel(title) { IsExpanded = _folded.TryGetValue(title, out bool folded) ? !folded : open };
        section.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InspectorSectionViewModel.IsExpanded))
            {
                _folded[title] = !section.IsExpanded;
            }
        };
        return section;
    }

    /// <summary>
    /// A title's parameters that are rows rather than the text section's own controls, grouped the
    /// way a person looks for them; the animation channels start folded, as the animation pickers
    /// drive them.
    /// </summary>
    private void AddTitleSections(string clipId, EffectDescriptor title)
    {
        foreach ((string heading, string[] names, bool open) in TitleGroups)
        {
            var view = Section(heading, open);
            foreach (string name in names)
            {
                if (title.Param(name) is { } parameter)
                {
                    view.Rows.Add(new ParamRowViewModel(this, clipId, parameter, heading));
                }
            }

            Sections.Add(view);
        }
    }

    /// <summary>The title's rows, section by section; the text section has the rest.</summary>
    internal static IReadOnlyList<(string Heading, string[] Names, bool Open)> TitleGroups { get; } =
    [
        ("Text", [TitleParams.Size, TitleParams.Colour, TitleParams.Position, TitleParams.Width, TitleParams.LineSpacing, TitleParams.Tracking], true),
        ("Outline", [TitleParams.Stroke, TitleParams.StrokeWidth], true),
        ("Box", [TitleParams.Box, TitleParams.BoxPadding, TitleParams.BoxRadius], true),
        ("Shadow", [TitleParams.Shadow, TitleParams.ShadowOffset, TitleParams.ShadowBlur], true),
        ("Animation channels", [TitleParams.Fade, TitleParams.Offset, TitleParams.Zoom, TitleParams.Blur, TitleParams.Reveal, TitleParams.RevealBy, TitleParams.RevealSoft], false),
    ];

    /// <summary>
    /// The panel for a selected transition: its duration and alignment, then its own parameters,
    /// every one sent as the command the CLI would send.
    /// </summary>
    private void BuildTransition(ParamOwner owner)
    {
        Transition transition = owner.Transition!;
        _clipId = null;
        _transitionId = transition.Id;
        _shape = $"transition|{transition.Id}|{transition.TypeId}";

        var timing = Section("Timing");
        timing.Rows.Add(new ParamRowViewModel(this, transition.Id, TransitionDuration, timing.Title));
        timing.Rows.Add(new ParamRowViewModel(this, transition.Id, TransitionAlignmentParam, timing.Title));
        Sections.Add(timing);

        foreach (EffectDescriptor section in ParamTargets.Sections(owner, EffectCatalog.Registry))
        {
            var view = Section(section.Name);
            foreach (ParamDescriptor parameter in section.Params)
            {
                view.Rows.Add(new ParamRowViewModel(this, transition.Id, parameter, section.Name));
            }

            Sections.Add(view);
        }

        HasTarget = true;
        IsPicture = false;
        Status = string.Empty;
        RefreshValues();
    }

    /// <summary>A comp graph node's parameters (Phase 49): its type's own, and a 3D object's place in its render.</summary>
    private void BuildNode(ParamOwner owner)
    {
        Effect node = owner.Effect!;
        _clipId = null;
        _nodeId = node.Id;
        _shape = $"node|{node.Id}|{node.TypeId}";

        foreach (EffectDescriptor section in ParamTargets.Sections(owner, EffectCatalog.Registry))
        {
            var view = Section(section.Name);
            foreach (ParamDescriptor parameter in section.Params)
            {
                view.Rows.Add(new ParamRowViewModel(this, node.Id, parameter, section.Name));
            }

            Sections.Add(view);
        }

        HasTarget = true;
        IsPicture = false;
        Status = string.Empty;
        RefreshValues();
    }

    /// <summary>
    /// A selected track: a sound track's own volume and pan, and the effects on the whole track,
    /// picture or sound, which every clip on it goes through. Their times are the sequence's.
    /// </summary>
    private void BuildTrack(ParamOwner owner)
    {
        Track track = owner.Track;
        _clipId = null;
        _trackId = track.Id;
        _shape = TrackShape(track);

        foreach (EffectDescriptor section in ParamTargets.Sections(owner, EffectCatalog.Registry))
        {
            var view = Section(section.Name);
            foreach (ParamDescriptor parameter in section.Params)
            {
                view.Rows.Add(new ParamRowViewModel(this, track.Id, parameter, section.Name));
            }

            Sections.Add(view);
        }

        AddEffectItems(track.Effects);
        HasTarget = true;
        IsPicture = false;
        Status = string.Empty;
        RefreshValues();
    }

    /// <summary>An item for each effect, with a row for each of its parameters.</summary>
    /// <param name="effects">The effects, in order.</param>
    /// <param name="maskable">True on a picture clip, where a picture effect can be limited by masks.</param>
    private void AddEffectItems(IEnumerable<Effect> effects, bool maskable = false)
    {
        foreach (Effect effect in effects)
        {
            EffectDescriptor? descriptor = EffectCatalog.Registry.Find(effect.TypeId);
            bool plugin = effect.TypeId == JazzHands.Audio.Effects.PluginEffect.TypeId;
            var item = new EffectItemViewModel(this, effect.Id, effect.TypeId, plugin ? PluginName(effect) : descriptor?.Name ?? effect.TypeId, descriptor is not null)
            {
                CanMask = maskable && descriptor is { Kind: EffectKind.Video },
            };
            IEnumerable<ParamDescriptor> rows = plugin ? PluginRows(effect) : descriptor?.Params ?? [];
            foreach (ParamDescriptor parameter in rows)
            {
                item.Rows.Add(new ParamRowViewModel(this, effect.Id, parameter, item.Name));
            }

            AddMaskItems(item.Masks, effect.Masks);
            Effects.Add(item);
        }
    }

    /// <summary>The parameters a mask's item shows: its shape is the preview's, the rest are rows.</summary>
    private static IEnumerable<ParamDescriptor> MaskRows =>
        ParamTargets.MaskParams.Params.Where(parameter => parameter.Name is not ("bounds" or "path"));

    private void AddMaskItems(ObservableCollection<MaskItemViewModel> into, EquatableArray<Mask> masks)
    {
        for (int index = 0; index < masks.Length; index++)
        {
            Mask mask = masks[index];
            var item = new MaskItemViewModel(this, mask.Id, $"Mask {index + 1}, {mask.Shape.ToString().ToLowerInvariant()}");
            foreach (ParamDescriptor parameter in MaskRows)
            {
                item.Rows.Add(new ParamRowViewModel(this, mask.Id, parameter, item.Name));
            }

            into.Add(item);
        }
    }

    /// <summary>Loads masks' values at a time in their clip, sending nothing.</summary>
    private void LoadMasks(Project project, IEnumerable<MaskItemViewModel> items, EquatableArray<Mask> masks, Clip clip, Flicks local, Flicks playhead, Flicks tolerance)
    {
        foreach ((MaskItemViewModel item, Mask mask) in items.Zip(masks))
        {
            item.Load(mask.Mode, mask.Invert);
            if (ParamTargets.Find(project, mask.Id) is not { } owner)
            {
                continue;
            }

            foreach (ParamRowViewModel row in item.Rows)
            {
                AnimatedValue? stored = ParamTargets.Get(owner, row.Name);
                row.Load(ParamEval.Eval(stored, row.Descriptor, local), stored, clip.Start, clip.Duration, playhead, tolerance);
            }
        }
    }

    /// <inheritdoc />
    public void AddMask(string ownerId, MaskShape shape)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        Project project = _session.Project;
        if (ParamTargets.Find(project, ownerId) is not { Clip: { } clip } owner)
        {
            return;
        }

        // Over the middle half of the picture, in its own pixels, to be moved on the preview.
        ProjectSettings settings = project.SettingsFor(owner.Sequence);
        System.Numerics.Vector2 size = Playback.MaskHandlesViewModel.SourceOf(project, clip, new System.Numerics.Vector2(settings.Width, settings.Height)).Size;
        _ = RunAsync(new AddMaskCommand(ownerId, shape, X: Math.Round(size.X / 4), Y: Math.Round(size.Y / 4), Width: Math.Round(size.X / 2), Height: Math.Round(size.Y / 2)));
    }

    /// <inheritdoc />
    public void RemoveMask(MaskItemViewModel mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        _ = RunAsync(new RemoveMaskCommand(mask.Id));
    }

    /// <inheritdoc />
    public void SetMask(MaskItemViewModel mask, MaskMode? mode, bool? invert)
    {
        ArgumentNullException.ThrowIfNull(mask);
        _ = RunAsync(new SetMaskCommand(mask.Id, Mode: mode, Invert: invert));
    }

    /// <summary>Adds a mask to the inspected clip itself: a rectangle or an ellipse.</summary>
    [RelayCommand]
    private void AddClipMask(string shape)
    {
        if (_clipId is { } clipId && Enum.TryParse(shape, ignoreCase: true, out MaskShape parsed))
        {
            AddMask(clipId, parsed);
        }
    }

    /// <summary>What decides whether a selected track's rows can be reloaded in place: its kind and its effects.</summary>
    private static string TrackShape(Track track) =>
        string.Join("|", ["track", track.Id, track.Kind.ToString(), .. track.Effects.Select(effect => $"{effect.Id}:{effect.TypeId}")]);

    /// <summary>A selected track's values, from the snapshot, at the playhead.</summary>
    private void RefreshTrack(Project project, string trackId)
    {
        if (ParamTargets.Find(project, trackId) is not { Kind: ParamOwnerKind.Track } owner)
        {
            Rebuild();
            return;
        }

        Track track = owner.Track;
        Flicks playhead = Playhead;
        Flicks length = owner.Sequence.Duration;
        Flicks tolerance = Tolerance();
        _loading = true;
        try
        {
            Heading = track.Name;
            Source = track.Kind switch
            {
                TrackKind.Audio => "A sound track: its volume, pan and effects, for every clip on it",
                TrackKind.Adjustment => "An adjustment track: its effects, over everything under it",
                _ => "A picture track: its effects, for every clip on it",
            };
            Range = Core.Words.Count(track.Clips.Length, "clip") + (track.Effects.IsEmpty ? ", no effects yet: drop one from the Effects panel on the track" : string.Empty);
            Speed = SelectionNote = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        foreach (ParamRowViewModel row in Sections.SelectMany(section => section.Rows))
        {
            AnimatedValue? stored = ParamTargets.Get(owner, row.Name);
            row.Load(ParamEval.Eval(stored, row.Descriptor, playhead), stored, Flicks.Zero, length, playhead, tolerance);
        }

        for (int index = 0; index < Effects.Count && index < track.Effects.Length; index++)
        {
            EffectItemViewModel item = Effects[index];
            Effect effect = track.Effects[index];
            item.Load(effect.Enabled, index, track.Effects.Length);
            foreach (ParamRowViewModel row in item.Rows)
            {
                AnimatedValue? stored = effect.Parameter(row.Name);
                row.Load(ParamEval.Eval(stored, row.Descriptor, playhead), stored, Flicks.Zero, length, playhead, tolerance);
            }
        }

        UpdateMarkers();
    }

    /// <summary>A selected comp node's values, from the snapshot, at the playhead in its clip's time.</summary>
    private void RefreshNode(Project project, string nodeId)
    {
        if (ParamTargets.Find(project, nodeId) is not { Kind: ParamOwnerKind.Effect, Clip: { } clip, Effect: { } node } owner)
        {
            Rebuild();
            return;
        }

        Flicks playhead = Playhead;
        Flicks local = playhead - clip.Start;
        Flicks tolerance = Tolerance();
        _loading = true;
        try
        {
            Heading = CompNodes.Find(node.TypeId)?.Name ?? EffectCatalog.Registry.Find(node.TypeId)?.Name ?? node.TypeId;
            Source = $"A node of the comp graph on '{clip.Name}'";
            Range = Speed = SelectionNote = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        foreach (ParamRowViewModel row in Sections.SelectMany(section => section.Rows))
        {
            AnimatedValue? stored = ParamTargets.Get(owner, row.Name);
            row.Load(ParamEval.Eval(stored, row.Descriptor, local), stored, clip.Start, clip.Duration, playhead, tolerance);
        }

        UpdateMarkers();
    }

    /// <summary>A selected transition's values, from the snapshot.</summary>
    private void RefreshTransition(Project project, string transitionId)
    {
        if (ParamTargets.Find(project, transitionId) is not { Kind: ParamOwnerKind.Transition } owner)
        {
            Rebuild();
            return;
        }

        Transition transition = owner.Transition!;
        Rational rate = project.SettingsFor(owner.Sequence).FrameRate;
        Clip? left = owner.Track.Clip(transition.LeftClipId);
        Clip? right = owner.Track.Clip(transition.RightClipId);
        Core.Queries.TransitionSpan? span = Core.Queries.TransitionTiming.Span(owner.Track, transition, rate);

        _loading = true;
        try
        {
            Heading = EffectCatalog.Registry.Find(transition.TypeId)?.Name ?? transition.TypeId;
            Source = $"Between '{left?.Name}' and '{right?.Name}'";
            Range = span is { } playing
                ? $"{Timecode.FormatClock(playing.Range.Start)} to {Timecode.FormatClock(playing.Range.End)}, {Timecode.FormatClock(playing.Range.Duration)} long"
                : "Its clips do not meet, so it does not play.";
            Speed = SelectionNote = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        Flicks tolerance = Tolerance();
        Flicks origin = owner.Origin;
        Flicks local = Clamp(Playhead - origin, transition.Duration);
        string alignment = transition.Alignment switch
        {
            TransitionAlignment.EndOfLeft => "end-of-left",
            TransitionAlignment.StartOfRight => "start-of-right",
            _ => "centered",
        };

        foreach (ParamRowViewModel row in Sections.SelectMany(section => section.Rows))
        {
            if (ReferenceEquals(row.Descriptor, TransitionDuration))
            {
                var seconds = new ParamValue.Float((float)transition.Duration.ToSeconds());
                row.Load(seconds, AnimatedValue.Constant(seconds), origin, transition.Duration, Playhead, tolerance);
            }
            else if (ReferenceEquals(row.Descriptor, TransitionAlignmentParam))
            {
                var choice = new ParamValue.Enum(alignment);
                row.Load(choice, AnimatedValue.Constant(choice), origin, transition.Duration, Playhead, tolerance);
            }
            else
            {
                AnimatedValue? stored = transition.Parameter(row.Name);
                row.Load(ParamEval.Eval(stored, row.Descriptor, local), stored, origin, transition.Duration, Playhead, tolerance);
            }
        }

        UpdateMarkers();
    }

    /// <summary>What one of a transition's rows sends: its duration and alignment through <c>transition.set</c>, the rest through <c>param.set</c>.</summary>
    private ICommand TransitionEdit(ParamRowViewModel row, string text)
    {
        if (ReferenceEquals(row.Descriptor, TransitionDuration))
        {
            ParamValue.Float seconds = (ParamValue.Float)ParamValues.Parse(TransitionDuration, text);
            return new SetTransitionCommand(row.OwnerId, Duration: Flicks.FromSeconds(seconds.Value));
        }

        if (ReferenceEquals(row.Descriptor, TransitionAlignmentParam))
        {
            TransitionAlignment alignment = text switch
            {
                "end-of-left" => TransitionAlignment.EndOfLeft,
                "start-of-right" => TransitionAlignment.StartOfRight,
                _ => TransitionAlignment.Centered,
            };
            return new SetTransitionCommand(row.OwnerId, Alignment: alignment);
        }

        return new SetParamCommand(row.OwnerId, row.Name, text, AtFor(row.OwnerId, row));
    }

    /// <summary>A fade row's value on a clip: the fade's length in seconds, or its shape.</summary>
    private static ParamValue FadeValue(Clip clip, ParamDescriptor row)
    {
        if (ReferenceEquals(row, ThreeDParam))
        {
            return new ParamValue.Bool(clip.Layer3D is not null);
        }

        if (ReferenceEquals(row, LightsParam))
        {
            return new ParamValue.Bool(clip.Layer3D?.AcceptsLights ?? true);
        }

        if (ReferenceEquals(row, CastsShadowsParam))
        {
            // Geometry throws shadows unless somebody says otherwise.
            return new ParamValue.Bool(clip.Layer3D?.CastsShadows ?? SceneObjects.IsMesh(clip.GeneratorId));
        }

        if (ReferenceEquals(row, AcceptsShadowsParam))
        {
            return new ParamValue.Bool(clip.Layer3D?.AcceptsShadows ?? true);
        }

        if (ReferenceEquals(row, KeepPitchParam))
        {
            return new ParamValue.Bool(clip.KeepsPitch);
        }

        if (ReferenceEquals(row, SpeedBlurParam))
        {
            return new ParamValue.Bool(clip.BlurFollowsSpeed == true);
        }

        if (ReferenceEquals(row, FastMuteParam))
        {
            // A limit set from jazz that is not one of the choices shows as the nearest one.
            double limit = clip.MuteFasterThan is { } speed ? speed.ToDouble() : 0;
            return new ParamValue.Enum(limit <= 0 ? "never" : FastMuteChoices.Where(pair => pair.Speed is not null).MinBy(pair => Math.Abs(pair.Speed!.Value.ToDouble() - limit)).Name);
        }

        if (ReferenceEquals(row, RetimeParam))
        {
            return new ParamValue.Enum(clip.Retime switch { RetimeMode.Blend => "blend", RetimeMode.OpticalFlow => "optical-flow", _ => "nearest" });
        }

        Fade? fade = ReferenceEquals(row, FadeInLength) || ReferenceEquals(row, FadeInShape) ? clip.FadeIn : clip.FadeOut;
        return ReferenceEquals(row, FadeInLength) || ReferenceEquals(row, FadeOutLength)
            ? new ParamValue.Float((float)(fade?.Duration ?? Flicks.Zero).ToSeconds())
            : new ParamValue.Enum(FadeCurves.FirstOrDefault(pair => pair.Curve == (fade?.Curve ?? Interp.Linear)).Name ?? "linear");
    }

    /// <summary>What a fade row sends for one clip: that fade with the row's half changed and the other half kept.</summary>
    private ICommand FadeEdit(string clipId, ParamRowViewModel row, string text)
    {
        if (ReferenceEquals(row.Descriptor, ThreeDParam))
        {
            return new SetClip3DCommand(clipId, Off: !((ParamValue.Bool)ParamValues.Parse(ThreeDParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, LightsParam))
        {
            return new SetClip3DCommand(clipId, Lights: ((ParamValue.Bool)ParamValues.Parse(LightsParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, CastsShadowsParam))
        {
            return new SetClip3DCommand(clipId, CastsShadows: ((ParamValue.Bool)ParamValues.Parse(CastsShadowsParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, AcceptsShadowsParam))
        {
            return new SetClip3DCommand(clipId, AcceptsShadows: ((ParamValue.Bool)ParamValues.Parse(AcceptsShadowsParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, KeepPitchParam))
        {
            return new SetClipKeepPitchCommand(clipId, ((ParamValue.Bool)ParamValues.Parse(KeepPitchParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, SpeedBlurParam))
        {
            return new SetClipSpeedBlurCommand(clipId, ((ParamValue.Bool)ParamValues.Parse(SpeedBlurParam, text)).Value);
        }

        if (ReferenceEquals(row.Descriptor, FastMuteParam))
        {
            Rational? limit = FastMuteChoices.FirstOrDefault(pair => string.Equals(pair.Name, text, StringComparison.Ordinal)).Speed;
            return limit is null ? new SetClipFastMuteCommand(clipId, Off: true) : new SetClipFastMuteCommand(clipId, limit);
        }

        if (ReferenceEquals(row.Descriptor, RetimeParam))
        {
            return new SetClipRetimeCommand(clipId, text switch { "blend" => RetimeMode.Blend, "optical-flow" => RetimeMode.OpticalFlow, _ => RetimeMode.Nearest });
        }

        bool fadeIn = ReferenceEquals(row.Descriptor, FadeInLength) || ReferenceEquals(row.Descriptor, FadeInShape);
        Fade? now = _session.Project.FindClip(clipId) is { } found ? (fadeIn ? found.Clip.FadeIn : found.Clip.FadeOut) : null;
        Flicks length = now?.Duration ?? Flicks.Zero;
        Interp curve = now?.Curve ?? Interp.Linear;

        if (row.Descriptor.Type == ParamType.Float)
        {
            float seconds = ((ParamValue.Float)ParamValues.Parse(row.Descriptor, text)).Value;
            length = Flicks.FromSeconds(Math.Round(seconds, 3));
        }
        else
        {
            curve = FadeCurves.FirstOrDefault(pair => string.Equals(pair.Name, text, StringComparison.Ordinal)) is { Name: not null } chosen ? chosen.Curve : Interp.Linear;
        }

        return fadeIn ? new SetAudioFadeInCommand(clipId, length, curve) : new SetAudioFadeOutCommand(clipId, length, curve);
    }

    /// <summary>Loads every value from the snapshot, at the playhead, without sending anything.</summary>
    private void RefreshValues()
    {
        Project project = _session.Project;
        if (_nodeId is { } nodeId)
        {
            RefreshNode(project, nodeId);
            return;
        }

        if (_transitionId is { } transitionId)
        {
            RefreshTransition(project, transitionId);
            return;
        }

        if (_trackId is { } trackId)
        {
            RefreshTrack(project, trackId);
            return;
        }

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

        // Drivers read other parameters and markers here; sound reads zero, as measuring it would
        // hold up the UI thread.
        using DriverScope.Entered drivers = DriverScope.Enter(new ProjectDriverEnvironment(project, found.Sequence), clip.Start);

        _loading = true;
        try
        {
            Heading = clip.Name.Length > 0 ? clip.Name : "Untitled clip";
            Source = SourceOf(project, clip);
            Range = $"{Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}, {Timecode.FormatClock(clip.Duration)} long";
            Speed = clip.EffectiveSpeed == Rational.One && !clip.Reverse
                ? string.Empty
                : $"{clip.EffectiveSpeed.ToDouble() * 100:0.#}% speed{(clip.Reverse ? ", reversed" : string.Empty)}{(found.Track.Kind == TrackKind.Audio && clip.EffectiveSpeed != Rational.One ? (clip.KeepsPitch ? ", pitch kept" : ", pitch follows the speed") : string.Empty)}";
            SelectionNote = _targets.Count > 1 ? $"{_targets.Count} clips selected: their own settings change together, and so do the effects they share." : string.Empty;
            Blend = clip.BlendMode;
        }
        finally
        {
            _loading = false;
        }

        ParamOwner owner = ParamTargets.Find(project, clip.Id)!;
        if (TitleSection is { } title && clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect)) is var own)
        {
            EffectDescriptor descriptor = EffectCatalog.Registry.Find(TitleParams.GeneratorId)!;
            title.Load(
                ParameterSet.Evaluate(descriptor, own, Clamp(local, clip.Duration)),
                own?.Parameter(TitleParams.Text) is KeyframedValue { IsAnimated: true },
                TitleAnimations.Read(own, clip.Duration),
                clip.Start + Clamp(local, clip.Duration));
        }

        foreach (ParamRowViewModel row in Sections.SelectMany(section => section.Rows))
        {
            if (IsFadeRow(row))
            {
                ParamValue fade = FadeValue(clip, row.Descriptor);
                row.Load(fade, AnimatedValue.Constant(fade), clip.Start, clip.Duration, playhead, tolerance);
                row.IsMixed = _targets.Count > 1 && _targets.Skip(1).Any(target => project.FindClip(target) is { } other && !Equals(FadeValue(other.Clip, row.Descriptor), fade));
                continue;
            }

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
                row.IsMixed = Matching(project, effect.Id).Any(other => Differs(project, other, row, playhead));
            }

            LoadMasks(project, item.Masks, effect.Masks, clip, local, playhead, tolerance);
        }

        LoadMasks(project, ClipMasks, clip.Masks, clip, local, playhead, tolerance);
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
                if (string.Equals(row.OwnerId, _transitionId, StringComparison.Ordinal))
                {
                    await RunAsync(TransitionEdit(row, text)).ConfigureAwait(true);
                    continue;
                }

                if (IsFadeRow(row))
                {
                    ICommand[] fades = [.. TargetsFor(row).Select(target => FadeEdit(target, row, text))];
                    await RunAsync(fades.Length == 1 ? fades[0] : new BatchCommand([.. fades], $"Set {row.Label}")).ConfigureAwait(true);
                    continue;
                }

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
        string.Equals(row.OwnerId, _clipId, StringComparison.Ordinal) ? _targets : [row.OwnerId, .. Matching(_session.Project, row.OwnerId)];

    /// <summary>
    /// The same effect on the other selected clips: on each that has one, the effect of the same
    /// type counted the same way (the second blur for the second blur). None for an effect that is
    /// not the first selected clip's, or with one clip selected.
    /// </summary>
    private IEnumerable<string> Matching(Project project, string effectId)
    {
        if (_targets.Count < 2 || project.FindClip(_targets[0]) is not { } first)
        {
            yield break;
        }

        int at = first.Clip.Effects.IndexOf(effect => string.Equals(effect.Id, effectId, StringComparison.Ordinal));
        if (at < 0)
        {
            yield break;
        }

        string type = first.Clip.Effects[at].TypeId;
        int nth = first.Clip.Effects.Take(at).Count(effect => effect.TypeId == type);
        foreach (string target in _targets.Skip(1))
        {
            if (project.FindClip(target)?.Clip.Effects.Where(effect => effect.TypeId == type).ElementAtOrDefault(nth) is { } same)
            {
                yield return same.Id;
            }
        }
    }

    /// <summary>The keyframe time for a set: the playhead when that owner's parameter is animated.</summary>
    private Flicks? AtFor(string ownerId, ParamRowViewModel row) =>
        ParamTargets.Find(_session.Project, ownerId) is { } owner && ParamTargets.Get(owner, row.Name) is KeyframedValue { IsAnimated: true }
            ? Playhead
            : null;

    private bool Within(ParamRowViewModel row) =>
        ParamTargets.Find(_session.Project, row.OwnerId) switch
        {
            { Clip: { } clip } => Playhead >= clip.Start && Playhead <= clip.End,
            { Kind: ParamOwnerKind.Transition } transition => Playhead >= transition.Origin && Playhead <= transition.Origin + transition.Length,

            // A track and its effects last the whole sequence.
            { Kind: ParamOwnerKind.Track or ParamOwnerKind.Effect } => true,
            _ => false,
        };

    private string Over(ParamRowViewModel row) =>
        string.Equals(row.OwnerId, _transitionId, StringComparison.Ordinal) ? "the transition" : "the clip";

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

    /// <summary>A time held inside a clip, so a playhead before or after it reads the nearest end.</summary>
    private static Flicks Clamp(Flicks local, Flicks length) =>
        local < Flicks.Zero ? Flicks.Zero : local >= length ? length - new Flicks(1) : local;

    /// <summary>What decides whether rows can be reloaded in place: the clip, its kind, and its effects.</summary>
    private static string Shape(ClipLocation found) =>
        string.Join(
            "|",
            [found.Clip.Id, found.Track.Kind.ToString(), found.Clip.GeneratorId ?? string.Empty, found.Clip.Layer3D is null ? "flat" : "3d", .. found.Clip.Masks.Select(mask => $"mask:{mask.Id}:{mask.Shape}"), .. EffectChains.Visible(found.Clip, found.Clip.Effects).Select(effect => $"{effect.Id}:{effect.TypeId}:{string.Join(",", effect.Masks.Select(mask => $"{mask.Id}:{mask.Shape}"))}")]);

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

        if (clip.Cue is not null)
        {
            return "Subtitle cue";
        }

        return clip.GeneratorId is { } generator
            ? EffectCatalog.Registry.Find(generator)?.Name ?? generator
            : "Adjustment layer";
    }
}
