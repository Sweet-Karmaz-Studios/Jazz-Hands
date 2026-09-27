using System.Globalization;
using System.Numerics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Selection;

namespace JazzHands.App.ViewModels.Grading;

/// <summary>
/// The maths of a colour wheel: a puck on a disc to the red, green and blue offsets a wheel stores,
/// and back.
/// </summary>
/// <remarks>
/// The disc is the Cb/Cr plane: across is Cb, up is Cr, so red sits up and a little left, as on
/// the vectorscope, and a push towards a hue moves the picture towards that hue on the scope. The
/// offsets are luma free (a wheel tints; the master slider brightens), and a stored value that
/// carries brightness in its red, green and blue shows it on the master when read back.
/// </remarks>
public static class WheelMath
{
    /// <summary>The red, green and blue offsets for a puck, the disc's edge being <paramref name="range"/> of chroma.</summary>
    public static Vector3 Offsets(Point puck, double range)
    {
        double cb = puck.X * range * 0.5;
        double cr = puck.Y * range * 0.5;
        return new Vector3((float)(1.5748 * cr), (float)((-0.1873 * cb) - (0.4681 * cr)), (float)(1.8556 * cb));
    }

    /// <summary>The puck, and the brightness carried in the offsets, for stored offsets.</summary>
    public static (Point Puck, double Luma) Puck(Vector3 offsets, double range)
    {
        double luma = (0.2126 * offsets.X) + (0.7152 * offsets.Y) + (0.0722 * offsets.Z);
        double cb = (offsets.Z - luma) / 1.8556;
        double cr = (offsets.X - luma) / 1.5748;
        var puck = new Point(cb / (range * 0.5), cr / (range * 0.5));

        // Held on the disc: a value typed past its edge is shown at the edge.
        double length = Math.Sqrt((puck.X * puck.X) + (puck.Y * puck.Y));
        return (length > 1 ? new Point(puck.X / length, puck.Y / length) : puck, luma);
    }
}

/// <summary>One wheel of the Color panel: lift, gamma, gain or offset.</summary>
public sealed partial class WheelViewModel : ObservableObject
{
    private readonly ColorPanelViewModel _panel;
    private bool _loading;

    [ObservableProperty]
    private Point _puck;

    [ObservableProperty]
    private double _master;

    /// <summary>Creates a wheel.</summary>
    /// <param name="panel">The panel it sends through.</param>
    /// <param name="name">The color.wheels parameter: lift, gamma, gain or offset.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="range">How much chroma the disc's edge is.</param>
    /// <param name="masterRange">How far the master slider goes either way.</param>
    public WheelViewModel(ColorPanelViewModel panel, string name, string label, double range, double masterRange)
    {
        _panel = panel;
        Name = name;
        Label = label;
        Range = range;
        MasterRange = masterRange;
    }

    /// <summary>The parameter.</summary>
    public string Name { get; }

    /// <summary>What it is called.</summary>
    public string Label { get; }

    /// <summary>How much chroma the edge of the disc is.</summary>
    public double Range { get; }

    /// <summary>How far the master slider goes either way.</summary>
    public double MasterRange { get; }

    /// <summary>The value as the command reads it: red, green, blue and master.</summary>
    public string ValueText
    {
        get
        {
            Vector3 offsets = WheelMath.Offsets(Puck, Range);
            return string.Create(CultureInfo.InvariantCulture, $"{Round(offsets.X)}, {Round(offsets.Y)}, {Round(offsets.Z)}, {Round(Master)}");
        }
    }

    /// <summary>Shows a stored value without sending anything.</summary>
    public void Load(Vector4 value)
    {
        _loading = true;
        (Point puck, double luma) = WheelMath.Puck(new Vector3(value.X, value.Y, value.Z), Range);
        Puck = puck;
        Master = value.W + luma;
        _loading = false;
    }

    partial void OnPuckChanged(Point value) => Edited();

    partial void OnMasterChanged(double value) => Edited();

    /// <summary>Back to the centre and no brightness change.</summary>
    [RelayCommand]
    private void Reset()
    {
        _loading = true;
        Puck = default;
        Master = 0;
        _loading = false;
        Edited();
    }

    private void Edited()
    {
        if (!_loading)
        {
            _panel.Send(Name, ValueText);
        }
    }

    // Plus zero, so a value that rounds to nothing is written 0 rather than -0.
    private static string Round(double value) => (Math.Round(value, 4) + 0.0).ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>One of the curves the curve editor can show.</summary>
/// <param name="Name">The color.curves parameter.</param>
/// <param name="Label">The tab's caption.</param>
/// <param name="Periodic">True for a hue curve, whose ends wrap.</param>
/// <param name="Neutral">The text for no change.</param>
public sealed record CurveChoice(string Name, string Label, bool Periodic, string Neutral);

/// <summary>A clip the graded one can be matched to.</summary>
/// <param name="ClipId">The reference clip.</param>
/// <param name="Label">Its name and where it starts.</param>
public sealed record MatchChoice(string ClipId, string Label);

/// <summary>
/// The Color panel: lift, gamma, gain and offset wheels with saturation, contrast and pivot, and a
/// curve editor, for the selected clip's first Colour wheels and first Curves effect.
/// </summary>
/// <remarks>
/// Everything it changes is a parameter of an effect on the clip, sent as <c>param.set</c> through
/// <see cref="ParamSender"/>, so the CLI, MCP and the inspector see the same values and a drag is
/// one undo step. With no such effect on the clip the panel offers to add one. Every control resets
/// on its own: double-click or Backspace on a wheel or the curve, the reset button beside each.
/// "Match to" grades the clip like another clip in its sequence (<c>color.match</c>), reading the
/// frame under the playhead of whichever of the two it is over and the middle of the other.
/// </remarks>
public sealed partial class ColorPanelViewModel : ToolViewModel
{
    /// <summary>The docking content id.</summary>
    public const string PanelId = "color";

    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly ParamSender _sender;
    private readonly IPreviewEngine? _playback;
    private string? _clipId;
    private bool _loading;

    [ObservableProperty]
    private bool _hasClip;

    [ObservableProperty]
    private string _heading = "Select a clip to grade";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWheels))]
    private string? _wheelsId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurves))]
    private string? _curvesId;

    [ObservableProperty]
    private double _saturation = 1;

    [ObservableProperty]
    private double _contrast = 1;

    [ObservableProperty]
    private double _pivot = 0.435;

    [ObservableProperty]
    private CurveChoice _curve;

    [ObservableProperty]
    private string _curveText = "0,0 1,1";

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True while a wheel or the curve is being dragged: the panel does not reload under the pointer.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>Creates the panel.</summary>
    public ColorPanelViewModel(ISession session, SelectionService selection, IUiDispatcher ui, IPreviewEngine? playback = null)
        : base(PanelId, "Colour")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _playback = playback;
        _sender = new ParamSender(session, ui, () => playback?.Position ?? Flicks.Zero);
        _sender.Refused += (_, message) => Status = message;

        Wheels =
        [
            new WheelViewModel(this, "lift", "Lift", 0.4, 0.5),
            new WheelViewModel(this, "gamma", "Gamma", 0.8, 1.0),
            new WheelViewModel(this, "gain", "Gain", 0.8, 1.0),
            new WheelViewModel(this, "offset", "Offset", 0.4, 0.5),
        ];

        Curves =
        [
            new CurveChoice("master", "Master", false, "0,0 1,1"),
            new CurveChoice("red", "Red", false, "0,0 1,1"),
            new CurveChoice("green", "Green", false, "0,0 1,1"),
            new CurveChoice("blue", "Blue", false, "0,0 1,1"),
            new CurveChoice("hue-vs-sat", "Hue vs sat", true, "0,0.5 1,0.5"),
            new CurveChoice("hue-vs-hue", "Hue vs hue", true, "0,0.5 1,0.5"),
            new CurveChoice("sat-vs-sat", "Sat vs sat", false, "0,0.5 1,0.5"),
        ];
        _curve = Curves[0];

        _selection.Changed += (_, _) => _ui.Post(Reload);
        _session.ProjectChanged += (_, _) => _ui.Post(Reload);
        playback?.PlayheadMoved += (_, _) => _ui.Post(Reload);
        Reload();
    }

    /// <summary>Lift, gamma, gain and offset.</summary>
    public IReadOnlyList<WheelViewModel> Wheels { get; }

    /// <summary>The curves the editor can show.</summary>
    public IReadOnlyList<CurveChoice> Curves { get; }

    /// <summary>True when the clip has a Colour wheels effect for the wheels to drive.</summary>
    public bool HasWheels => WheelsId is not null;

    /// <summary>True when the clip has a Curves effect for the editor to drive.</summary>
    public bool HasCurves => CurvesId is not null;

    /// <summary>Sends one of the wheels effect's parameters.</summary>
    internal void Send(string name, string text)
    {
        if (!_loading && WheelsId is { } effect)
        {
            _sender.Send(effect, name, text);
        }
    }

    partial void OnSaturationChanged(double value) => Send("saturation", Number(value));

    partial void OnContrastChanged(double value) => Send("contrast", Number(value));

    partial void OnPivotChanged(double value) => Send("pivot", Number(value));

    partial void OnCurveChanged(CurveChoice value) => Reload();

    partial void OnIsEditingChanged(bool value)
    {
        if (!value)
        {
            Reload();
        }
    }

    partial void OnCurveTextChanged(string value)
    {
        if (!_loading && CurvesId is { } effect)
        {
            _sender.Send(effect, Curve.Name, value);
        }
    }

    [RelayCommand]
    private Task AddWheels() => AddAsync("color.wheels");

    [RelayCommand]
    private Task AddCurves() => AddAsync("color.curves");

    [RelayCommand]
    private void ResetSaturation() => Saturation = 1;

    [RelayCommand]
    private void ResetContrast() => Contrast = 1;

    [RelayCommand]
    private void ResetPivot() => Pivot = 0.435;

    [RelayCommand]
    private void ResetCurve() => CurveText = Curve.Neutral;

    [RelayCommand]
    private void ShowCurve(CurveChoice choice) => Curve = choice;

    private async Task AddAsync(string typeId)
    {
        if (_clipId is null)
        {
            return;
        }

        if (GraphId is not null)
        {
            // In a node graph, a new correction is a node after the selected one.
            await AddNodeAsync(typeId["color.".Length..], parallel: false).ConfigureAwait(true);
            return;
        }

        CommandResult result = await _session.ExecuteAsync(new AddEffectCommand(_clipId, typeId)).ConfigureAwait(true);
        _ui.Post(() => Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.");
    }

    /// <summary>The other pictures in the clip's sequence it can be matched to, in timeline order.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MatchChoice> MatchChoices { get; } = [];

    /// <summary>True when there is something to match to and no match is running.</summary>
    [ObservableProperty]
    private bool _canMatch;

    private bool _matching;

    /// <summary>Grades the clip to look like another, on its Colour wheels (one undo step).</summary>
    [RelayCommand]
    private async Task MatchTo(MatchChoice? choice)
    {
        if (_clipId is not { } clipId || choice is null || _matching)
        {
            return;
        }

        Project project = _session.Project;
        Flicks? Under(string id) => project.FindClip(id) is { } found && _playback?.Position is { } at && at >= found.Clip.Start && at < found.Clip.End ? at : null;

        _matching = true;
        CanMatch = false;
        Status = $"Matching the colour to {choice.Label}...";
        CommandResult result = await _session.ExecuteAsync(new MatchColorCommand(clipId, choice.ClipId, Under(clipId), Under(choice.ClipId))).ConfigureAwait(true);
        _ui.Post(() =>
        {
            _matching = false;
            Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.";
            Reload();
        });
    }

    private void LoadMatchChoices(Project project, ClipLocation? found)
    {
        MatchChoice[] choices = found is null
            ? []
            : [.. found.Sequence.Tracks
                .Where(track => track.Kind == TrackKind.Video)
                .SelectMany(track => track.Clips)
                .Where(clip => clip.Id != found.Clip.Id && clip.MediaId is not null)
                .OrderBy(clip => clip.Start)
                .Select(clip => new MatchChoice(clip.Id, $"{Name(project, clip)} at {Timecode.Format(clip.Start, project.SettingsFor(found.Sequence).FrameRate)}"))];

        if (!choices.SequenceEqual(MatchChoices))
        {
            MatchChoices.Clear();
            foreach (MatchChoice choice in choices)
            {
                MatchChoices.Add(choice);
            }
        }

        CanMatch = !_matching && MatchChoices.Count > 0;

        static string Name(Project project, Clip clip) =>
            clip.Name.Length > 0 ? clip.Name : project.Media.FirstOrDefault(item => item.Id == clip.MediaId)?.Name ?? "a clip";
    }

    /// <summary>Reads the selected clip and its effects into the controls, sending nothing.</summary>
    private void Reload()
    {
        if (IsEditing)
        {
            return;
        }

        Project project = _session.Project;
        ClipLocation? found = _selection.Ids
            .Select(project.FindClip)
            .FirstOrDefault(location => location is { Track.Kind: TrackKind.Video or TrackKind.Adjustment });

        _loading = true;
        try
        {
            _clipId = found?.Clip.Id;
            HasClip = found is not null;
            Heading = found is null ? "Select a clip to grade" : $"Grading {(found.Clip.Name.Length > 0 ? found.Clip.Name : "the clip")}";

            // With a colour graph, the wheels and curves drive the node selected in the node view.
            Effect? holder = found?.Clip.Effects.FirstOrDefault(effect => effect.TypeId == GradeGraph.TypeId);
            GradeNode? node = LoadNodes(holder);
            Effect? wheels = holder is not null
                ? node?.Effect is { TypeId: "color.wheels" } nodeWheels ? nodeWheels : null
                : found?.Clip.Effects.FirstOrDefault(effect => effect.TypeId == "color.wheels");
            Effect? curves = holder is not null
                ? node?.Effect is { TypeId: "color.curves" } nodeCurves ? nodeCurves : null
                : found?.Clip.Effects.FirstOrDefault(effect => effect.TypeId == "color.curves");
            LoadMatchChoices(project, found);
            LoadColorManagement(project, found?.Clip);
            WheelsId = wheels?.Id;
            CurvesId = curves?.Id;

            if (found is not null && wheels is not null && EffectCatalog.Registry.Find("color.wheels") is { } descriptor)
            {
                ParameterSet values = ParameterSet.Evaluate(descriptor, wheels, Local(found.Clip));
                foreach (WheelViewModel wheel in Wheels)
                {
                    wheel.Load(values.Float4(wheel.Name));
                }

                Saturation = values.Float("saturation");
                Contrast = values.Float("contrast");
                Pivot = values.Float("pivot");
            }

            if (found is not null && curves is not null && EffectCatalog.Registry.Find("color.curves") is { } curveDescriptor)
            {
                CurveText = ParameterSet.Evaluate(curveDescriptor, curves, Local(found.Clip)).Text(Curve.Name);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The playhead from the clip's start, held inside the clip: where its animated values are read.</summary>
    private Flicks Local(Clip clip)
    {
        Flicks local = (_playback?.Position ?? clip.Start) - clip.Start;
        return local < Flicks.Zero ? Flicks.Zero : local > clip.Duration ? clip.Duration : local;
    }

    private static string Number(double value) => (Math.Round(value, 4) + 0.0).ToString("0.####", CultureInfo.InvariantCulture);
}
