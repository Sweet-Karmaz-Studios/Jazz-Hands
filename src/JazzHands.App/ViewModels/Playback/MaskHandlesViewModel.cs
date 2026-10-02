using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Stabilization;
using JazzHands.Core.Time;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Selection;
using JazzHands.Render.Compositing;
using Serilog;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>What the preview's mask tool does with the next press on the picture.</summary>
public enum MaskTool
{
    /// <summary>Pick up and drag the masks already there.</summary>
    Select,

    /// <summary>Drag out a rectangle.</summary>
    Rectangle,

    /// <summary>Drag out an ellipse.</summary>
    Ellipse,

    /// <summary>Click corner after corner; close on the first point or with a double-click.</summary>
    Polygon,

    /// <summary>Like a polygon, but a drag at each point pulls out its curve.</summary>
    Bezier,
}

/// <summary>What part of a mask the pointer is on.</summary>
public enum MaskGripKind
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>A point of a polygon or bezier.</summary>
    Point,

    /// <summary>The handle a curve arrives at a point along.</summary>
    In,

    /// <summary>The handle a curve leaves a point along.</summary>
    Out,

    /// <summary>A corner of a rectangle's or ellipse's bounds.</summary>
    Corner,

    /// <summary>Inside the shape: a drag moves all of it.</summary>
    Body,

    /// <summary>The feather handle above the shape: a drag up softens the edge.</summary>
    Feather,
}

/// <summary>A place on a mask the pointer found.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="MaskId">Whose.</param>
/// <param name="Figure">Which figure of the path.</param>
/// <param name="Index">Which point, or which corner clockwise from the top left.</param>
public readonly record struct MaskGrip(MaskGripKind Kind, string MaskId = "", int Figure = 0, int Index = 0)
{
    /// <summary>Nothing under the pointer.</summary>
    public static MaskGrip Nothing => default;
}

/// <summary>A mask as the overlay draws it, in sequence pixels from the frame centre.</summary>
/// <param name="MaskId">The mask.</param>
/// <param name="IsActive">The one being edited, drawn with its handles.</param>
/// <param name="Outlines">Each figure's outline, closed.</param>
/// <param name="Points">The points a person can drag: a path's points, or a box's four corners.</param>
/// <param name="Handles">Tangent handles, each from its point to its end.</param>
/// <param name="Feather">Where the feather handle is, for the active mask.</param>
/// <param name="FeatherBase">Where the feather handle's line starts, on the shape's top.</param>
public sealed record MaskView(
    string MaskId,
    bool IsActive,
    IReadOnlyList<IReadOnlyList<Vector2>> Outlines,
    IReadOnlyList<Vector2> Points,
    IReadOnlyList<(Vector2 From, Vector2 To)> Handles,
    Vector2? Feather,
    Vector2? FeatherBase);

/// <summary>
/// The selected clip's masks on the preview: their outlines, points, tangent handles and feather,
/// what dragging them does, and drawing new ones.
/// </summary>
/// <remarks>
/// <para>
/// A mask is in the clip's source pixels; the handles are where the clip's placement puts them on
/// the frame, so they sit on the picture whatever the clip's transform. Every change is a command
/// the CLI could send: a point, a handle or the whole shape moved is <c>param.set path</c> (or
/// <c>bounds</c> for a rectangle or ellipse) at the playhead, which keyframes it there when the
/// shape is animated, and the feather is <c>param.set feather</c>. A drag sends only its newest
/// value while one is on its way, and the commands merge, so a drag is one undo step. A new mask is
/// <c>mask.add</c>.
/// </para>
/// <para>
/// Handles move as a pair unless Alt is held, which breaks them for a corner. A shape keyframed at
/// two times morphs between them (see <see cref="Core.Animation.MaskShapes"/>), so drawing the next
/// shape by moving the points of the last is how a mask is animated.
/// </para>
/// </remarks>
public sealed partial class MaskHandlesViewModel : ObservableObject
{
    /// <summary>How far above the shape the feather handle sits at no feather, in sequence pixels at 1080 lines.</summary>
    public const float FeatherReach = 24.0f;

    private readonly ILogger _log = Log.ForContext<MaskHandlesViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IPreviewEngine _preview;
    private readonly IUiDispatcher _ui;
    private readonly List<Vector2> _drawing = [];
    private readonly List<MaskNode> _drawingNodes = [];
    private ICommand? _pending;
    private bool _sending;
    private Task _pump = Task.CompletedTask;
    private Drag? _drag;
    private Matrix3x2 _toSequence = Matrix3x2.Identity;
    private Matrix3x2 _toSource = Matrix3x2.Identity;
    private Vector2? _drawFrom;

    /// <summary>The stabilized clip's motion analysis the handles follow, by what it was read for.</summary>
    private (string Key, CameraMotion? Motion)? _motion;

    /// <summary>What is being read now, off the UI thread, or null.</summary>
    private string? _reading;

    /// <summary>True when the project changed since the analysis was read, so it may have been made again.</summary>
    private bool _motionStale;

    /// <summary>The masks drawn on the preview; empty when the selected clip has none or none is selected.</summary>
    [ObservableProperty]
    private IReadOnlyList<MaskView> _masks = [];

    /// <summary>What a press on the picture does.</summary>
    [ObservableProperty]
    private MaskTool _tool = MaskTool.Select;

    /// <summary>The shape being drawn so far, in sequence pixels from the centre: its points, or a box's two corners.</summary>
    [ObservableProperty]
    private IReadOnlyList<Vector2> _sketch = [];

    /// <summary>The mask being edited, whose handles show.</summary>
    [ObservableProperty]
    private string? _activeMaskId;

    /// <summary>Why the last change did not take, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the handles and follows the selection, the project and the playhead.</summary>
    public MaskHandlesViewModel(ISession session, SelectionService selection, IPreviewEngine preview, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _preview = preview;
        _ui = ui;

        _selection.Changed += (_, _) => ui.Post(Refresh);
        _session.ProjectChanged += (_, _) => ui.Post(() =>
        {
            _motionStale = true;
            Refresh();
        });
        _preview.PlayheadMoved += (_, _) => ui.Post(Refresh);
        Refresh();
    }

    /// <summary>The clip whose masks show, or null.</summary>
    public string? ClipId { get; private set; }

    /// <summary>True when there is a clip at the playhead to draw masks on, which is when the tools show.</summary>
    public bool CanDraw => ClipId is not null;

    /// <summary>The select tool is chosen; for its button.</summary>
    public bool IsSelectTool
    {
        get => Tool == MaskTool.Select;
        set => Choose(MaskTool.Select, value);
    }

    /// <summary>The rectangle tool is chosen.</summary>
    public bool IsRectangleTool
    {
        get => Tool == MaskTool.Rectangle;
        set => Choose(MaskTool.Rectangle, value);
    }

    /// <summary>The ellipse tool is chosen.</summary>
    public bool IsEllipseTool
    {
        get => Tool == MaskTool.Ellipse;
        set => Choose(MaskTool.Ellipse, value);
    }

    /// <summary>The polygon tool is chosen.</summary>
    public bool IsPolygonTool
    {
        get => Tool == MaskTool.Polygon;
        set => Choose(MaskTool.Polygon, value);
    }

    /// <summary>The bezier tool is chosen.</summary>
    public bool IsBezierTool
    {
        get => Tool == MaskTool.Bezier;
        set => Choose(MaskTool.Bezier, value);
    }

    /// <summary>True while a drag or a box being drawn is under way.</summary>
    public bool IsDragging => _drag is not null || _drawFrom is not null;

    /// <summary>True while a polygon or bezier is being clicked out.</summary>
    public bool IsDrawing => _drawingNodes.Count > 0;

    /// <summary>What is being sent, for a test to wait on.</summary>
    internal Task Sending => _pump;

    /// <summary>The sequence's height over 1080, which handle distances scale by.</summary>
    private float Reach => _session.Project.ActiveSequence is { } sequence ? _session.Project.SettingsFor(sequence).Height / 1080.0f : 1.0f;

    /// <summary>Chooses the tool, from the preview's mask buttons.</summary>
    [RelayCommand]
    private void UseTool(MaskTool tool)
    {
        CancelDrawing();
        Tool = tool;
    }

    /// <summary>A tool button was pressed: pressing the chosen one again goes back to selecting.</summary>
    private void Choose(MaskTool tool, bool on)
    {
        UseTool(on ? tool : MaskTool.Select);

        // The button pressed toggled itself; say again what is chosen, even when nothing changed.
        OnToolChanged(Tool);
    }

    partial void OnToolChanged(MaskTool value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsRectangleTool));
        OnPropertyChanged(nameof(IsEllipseTool));
        OnPropertyChanged(nameof(IsPolygonTool));
        OnPropertyChanged(nameof(IsBezierTool));
    }

    /// <summary>Reads the selected clip's masks at the playhead.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        string? chosen = null;
        if (_selection.Ids.Length == 1 && project.FindClip(_selection.Ids[0]) is { } found
            && found.Track.Kind is TrackKind.Video or TrackKind.Adjustment
            && _preview.Position >= found.Clip.Start && _preview.Position < found.Clip.End

            // A 3D layer is placed through the camera, which these handles do not follow; a
            // camera or a light has no picture to mask (Phase 47).
            && found.Clip.Layer3D is null && !SceneObjects.Is(found.Clip))
        {
            chosen = found.Clip.Id;
        }

        if (chosen != ClipId)
        {
            CancelDrawing();
            ActiveMaskId = null;
        }

        ClipId = chosen;
        OnPropertyChanged(nameof(CanDraw));
        Build();
    }

    /// <summary>What a point on the picture is on, with a tolerance in sequence pixels. The active mask's handles come first.</summary>
    public MaskGrip HitTest(Vector2 at, float tolerance)
    {
        if (Tool != MaskTool.Select || ClipId is null)
        {
            return MaskGrip.Nothing;
        }

        IEnumerable<MaskView> order = Masks.OrderByDescending(view => view.IsActive);
        foreach (MaskView view in order)
        {
            if (view.IsActive && view.Feather is { } feather && Vector2.Distance(at, feather) <= tolerance)
            {
                return new MaskGrip(MaskGripKind.Feather, view.MaskId);
            }

            if (Mask(view.MaskId) is not { } mask)
            {
                continue;
            }

            Flicks local = Local;
            if (mask.Shape is MaskShape.Rectangle or MaskShape.Ellipse)
            {
                for (int corner = 0; corner < view.Points.Count; corner++)
                {
                    if (Vector2.Distance(at, view.Points[corner]) <= tolerance)
                    {
                        return new MaskGrip(MaskGripKind.Corner, view.MaskId, 0, corner);
                    }
                }
            }
            else
            {
                List<List<MaskNode>> figures = MaskNodes.Read(PathOf(mask, local));
                for (int figure = 0; figure < figures.Count; figure++)
                {
                    for (int index = 0; index < figures[figure].Count; index++)
                    {
                        MaskNode node = figures[figure][index];
                        if (view.IsActive && node.Out is { } outgoing && Vector2.Distance(at, ToSequence(outgoing)) <= tolerance)
                        {
                            return new MaskGrip(MaskGripKind.Out, view.MaskId, figure, index);
                        }

                        if (view.IsActive && node.In is { } incoming && Vector2.Distance(at, ToSequence(incoming)) <= tolerance)
                        {
                            return new MaskGrip(MaskGripKind.In, view.MaskId, figure, index);
                        }

                        if (Vector2.Distance(at, ToSequence(node.Point)) <= tolerance)
                        {
                            return new MaskGrip(MaskGripKind.Point, view.MaskId, figure, index);
                        }
                    }
                }
            }

            if (view.Outlines.Any(outline => Inside(outline, at)))
            {
                return new MaskGrip(MaskGripKind.Body, view.MaskId);
            }
        }

        return MaskGrip.Nothing;
    }

    /// <summary>Starts a drag on a mask, making it the active one.</summary>
    public bool Begin(MaskGrip grip, Vector2 at)
    {
        if (grip.Kind == MaskGripKind.None || Mask(grip.MaskId) is not { } mask)
        {
            return false;
        }

        Flicks local = Local;
        ActiveMaskId = grip.MaskId;
        _drag = new Drag(
            grip,
            at,
            ToSource(at),
            MaskNodes.Read(PathOf(mask, local)),
            BoundsOf(mask, local),
            FeatherOf(mask, local));
        Status = string.Empty;
        Build();
        return true;
    }

    /// <summary>The pointer moved during a drag: sends the mask's new shape. <paramref name="broken"/> moves one tangent handle alone.</summary>
    public void Move(Vector2 at, bool broken = false)
    {
        if (_drawFrom is { } from)
        {
            Sketch = [from, at];
            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        Vector2 source = ToSource(at);
        Vector2 by = source - drag.FromSource;
        string maskId = drag.Grip.MaskId;
        Flicks when = _preview.Position;

        switch (drag.Grip.Kind)
        {
            case MaskGripKind.Point:
            case MaskGripKind.In:
            case MaskGripKind.Out:
            {
                List<List<MaskNode>> figures = [.. drag.Figures.Select(nodes => nodes.ToList())];
                MaskNode node = figures[drag.Grip.Figure][drag.Grip.Index];
                figures[drag.Grip.Figure][drag.Grip.Index] = drag.Grip.Kind == MaskGripKind.Point
                    ? MaskNodes.Moved(node, by)
                    : MaskNodes.WithHandle(node, drag.Grip.Kind == MaskGripKind.Out, source, broken);
                Send(new SetParamCommand(maskId, "path", MaskNodes.Write(figures), At: when));
                break;
            }

            case MaskGripKind.Body when Mask(maskId) is { Shape: MaskShape.Rectangle or MaskShape.Ellipse }:
                Send(new SetParamCommand(maskId, "bounds", MaskNodes.BoundsText(drag.Bounds + new Vector4(by.X, by.Y, 0, 0)), At: when));
                break;

            case MaskGripKind.Body:
                Send(new SetParamCommand(maskId, "path", MaskNodes.Write(drag.Figures.Select(nodes => (IReadOnlyList<MaskNode>)[.. nodes.Select(node => MaskNodes.Moved(node, by))])), At: when));
                break;

            case MaskGripKind.Corner:
            {
                // The opposite corner stays where it is.
                Vector4 box = drag.Bounds;
                Vector2[] corners = [new(box.X, box.Y), new(box.X + box.Z, box.Y), new(box.X + box.Z, box.Y + box.W), new(box.X, box.Y + box.W)];
                Vector2 fixedCorner = corners[(drag.Grip.Index + 2) % 4];
                Vector2 min = Vector2.Min(fixedCorner, source);
                Vector2 max = Vector2.Max(fixedCorner, source);
                Send(new SetParamCommand(maskId, "bounds", MaskNodes.BoundsText(new Vector4(min.X, min.Y, max.X - min.X, max.Y - min.Y)), At: when));
                break;
            }

            case MaskGripKind.Feather:
            {
                // Up is softer: the handle's height above its resting place, in sequence pixels.
                float feather = Math.Max(0.0f, drag.Feather + (drag.From.Y - at.Y));
                Send(new SetParamCommand(maskId, "feather", FormattableString.Invariant($"{Math.Round(feather, 1)}"), At: when));
                break;
            }
        }
    }

    /// <summary>Ends a drag, or finishes the box being drawn.</summary>
    public void End(Vector2 at)
    {
        if (_drawFrom is { } from)
        {
            _drawFrom = null;
            Sketch = [];
            Vector2 a = ToSource(from);
            Vector2 b = ToSource(at);
            Vector2 min = Vector2.Min(a, b);
            Vector2 size = Vector2.Max(a, b) - min;
            if (ClipId is { } clipId && size.X >= 2 && size.Y >= 2)
            {
                MaskShape shape = Tool == MaskTool.Ellipse ? MaskShape.Ellipse : MaskShape.Rectangle;
                string maskId = Core.Model.Id.New();
                ActiveMaskId = maskId;
                Send(new AddMaskCommand(clipId, shape, Round(min.X), Round(min.Y), Round(size.X), Round(size.Y), MaskId: maskId));
            }

            return;
        }

        _drag = null;
    }

    /// <summary>
    /// A press with a drawing tool: starts a box, or adds a point to the polygon or bezier being
    /// drawn, closing it on its first point or on a double-click.
    /// </summary>
    /// <returns>True when the press was taken.</returns>
    public bool Press(Vector2 at, int clicks, float tolerance)
    {
        if (Tool == MaskTool.Select || ClipId is null)
        {
            return false;
        }

        if (Tool is MaskTool.Rectangle or MaskTool.Ellipse)
        {
            _drawFrom = at;
            Sketch = [at, at];
            return true;
        }

        // A double-click's first click has already put the last point where the second lands.
        bool repeated = clicks >= 2 && _drawingNodes.Count > 0 && Vector2.Distance(at, ToSequence(_drawingNodes[^1].Point)) <= tolerance;
        bool closing = _drawingNodes.Count >= 3 && (repeated || Vector2.Distance(at, ToSequence(_drawingNodes[0].Point)) <= tolerance);
        if (closing)
        {
            Close();
            return true;
        }

        if (repeated)
        {
            return true;
        }

        _drawingNodes.Add(new MaskNode(ToSource(at)));
        _drawing.Add(at);
        Sketch = [.. _drawing];
        return true;
    }

    /// <summary>While the button is held after a bezier press, pulls the new point's curve out towards the pointer.</summary>
    public void Pull(Vector2 at)
    {
        if (Tool != MaskTool.Bezier || _drawingNodes.Count == 0)
        {
            return;
        }

        MaskNode last = _drawingNodes[^1];
        Vector2 handle = ToSource(at);
        if (Vector2.Distance(handle, last.Point) < 1.0f)
        {
            return;
        }

        _drawingNodes[^1] = last with { Out = handle, In = last.Point - (handle - last.Point) };
    }

    /// <summary>Closes the polygon or bezier being drawn and adds it as a mask.</summary>
    public void Close()
    {
        if (ClipId is not { } clipId || _drawingNodes.Count < 3)
        {
            CancelDrawing();
            return;
        }

        string path = MaskNodes.Write([[.. _drawingNodes]]);
        MaskShape shape = Tool == MaskTool.Bezier ? MaskShape.Bezier : MaskShape.Polygon;
        string maskId = Core.Model.Id.New();
        CancelDrawing();
        ActiveMaskId = maskId;
        Send(new AddMaskCommand(clipId, shape, Path: path, MaskId: maskId));
    }

    /// <summary>Drops the shape being drawn.</summary>
    public void CancelDrawing()
    {
        _drawingNodes.Clear();
        _drawing.Clear();
        _drawFrom = null;
        Sketch = [];
    }

    /// <summary>True when a point is inside a closed outline, by the even-odd rule.</summary>
    internal static bool Inside(IReadOnlyList<Vector2> outline, Vector2 at)
    {
        bool inside = false;
        for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
        {
            Vector2 a = outline[index];
            Vector2 b = outline[previous];
            if ((a.Y > at.Y) != (b.Y > at.Y) && at.X < ((b.X - a.X) * (at.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private Flicks Local => ClipId is { } id && _session.Project.FindClip(id) is { } found ? _preview.Position - found.Clip.Start : Flicks.Zero;

    private static string PathOf(Mask mask, Flicks local) =>
        ParamEval.Eval(mask.PathData, ParamTargets.MaskParams.Param("path")!, local) is ParamValue.Path path ? path.Value : string.Empty;

    private static Vector4 BoundsOf(Mask mask, Flicks local) =>
        ParamEval.Eval(mask.Bounds, ParamTargets.MaskParams.Param("bounds")!, local) is ParamValue.Float4 bounds ? bounds.Value : Vector4.Zero;

    private static float FeatherOf(Mask mask, Flicks local) =>
        ParamEval.Eval(mask.Feather, ParamTargets.MaskParams.Param("feather")!, local) is ParamValue.Float feather ? feather.Value : 0.0f;

    private static double Round(float value) => Math.Round(value, 1);

    private Mask? Mask(string maskId) =>
        ClipId is { } id && _session.Project.FindClip(id) is { } found
            ? found.Clip.Masks.FirstOrDefault(mask => mask.Id == maskId)
            : null;

    private Vector2 ToSequence(Vector2 source) => Vector2.Transform(source, _toSequence);

    private Vector2 ToSource(Vector2 sequence) => Vector2.Transform(sequence, _toSource);

    /// <summary>Works out where the clip's source pixels land on the frame, then lays out every mask.</summary>
    private void Build()
    {
        Project project = _session.Project;
        if (ClipId is not { } clipId || project.FindClip(clipId) is not { } found || project.ActiveSequence is not { } sequence)
        {
            Masks = [];
            return;
        }

        Clip clip = found.Clip;
        Flicks local = _preview.Position - clip.Start;
        ProjectSettings settings = project.SettingsFor(sequence);
        var frame = new Vector2(settings.Width, settings.Height);
        (Vector2 size, ConformPolicy policy) = SourceOf(project, clip, frame);
        Transform transform = clip.Transform ?? Transform.Identity;

        // A stabilized clip's picture is moved back onto its smoothed path before it is placed,
        // and its masks with it, so the handles go where the renderer draws them.
        _toSequence = Steady(project, clip, size) * RenderGraphBuilder.Placement(
                size,
                frame,
                policy,
                Eval2(transform.Position, "transform.position", local, Vector2.Zero),
                Eval2(transform.Scale, "transform.scale", local, Vector2.One),
                ParamEval.Eval(transform.Rotation, ParamTargets.Transform.Param("transform.rotation")!, local) is ParamValue.Float turn ? turn.Value : 0.0f,
                Eval2(transform.Anchor, "transform.anchor", local, Vector2.Zero),
                1.0f)
            * Matrix3x2.CreateTranslation(-frame / 2.0f);
        if (!Matrix3x2.Invert(_toSequence, out _toSource))
        {
            Masks = [];
            return;
        }

        if (ActiveMaskId is { } active && clip.Masks.All(mask => mask.Id != active))
        {
            ActiveMaskId = null;
        }

        var views = new List<MaskView>();
        foreach (Mask mask in clip.Masks)
        {
            bool isActive = mask.Id == ActiveMaskId;
            List<List<MaskNode>> figures = mask.Shape switch
            {
                MaskShape.Rectangle => [MaskNodes.Rectangle(BoundsOf(mask, local))],
                MaskShape.Ellipse => [MaskNodes.Ellipse(BoundsOf(mask, local))],
                _ => MaskNodes.Read(PathOf(mask, local)),
            };

            List<IReadOnlyList<Vector2>> outlines = [.. figures.Select(nodes => (IReadOnlyList<Vector2>)[.. MaskNodes.Outline(nodes).Select(ToSequence)])];
            IReadOnlyList<Vector2> points = mask.Shape is MaskShape.Rectangle or MaskShape.Ellipse
                ? [.. MaskNodes.Rectangle(BoundsOf(mask, local)).Select(node => ToSequence(node.Point))]
                : [.. figures.SelectMany(nodes => nodes).Select(node => ToSequence(node.Point))];

            var handles = new List<(Vector2, Vector2)>();
            if (isActive && mask.Shape is MaskShape.Bezier or MaskShape.Polygon)
            {
                foreach (MaskNode node in figures.SelectMany(nodes => nodes))
                {
                    if (node.In is { } incoming)
                    {
                        handles.Add((ToSequence(node.Point), ToSequence(incoming)));
                    }

                    if (node.Out is { } outgoing)
                    {
                        handles.Add((ToSequence(node.Point), ToSequence(outgoing)));
                    }
                }
            }

            Vector2? featherHandle = null;
            Vector2? featherBase = null;
            if (isActive && outlines.SelectMany(outline => outline).ToList() is { Count: > 0 } all)
            {
                float top = all.Min(point => point.Y);
                float middle = (all.Min(point => point.X) + all.Max(point => point.X)) / 2.0f;
                featherBase = new Vector2(middle, top);
                featherHandle = new Vector2(middle, top - (FeatherReach * Reach) - FeatherOf(mask, local));
            }

            views.Add(new MaskView(mask.Id, isActive, outlines, points, handles, featherHandle, featherBase));
        }

        Masks = views;
    }

    /// <summary>
    /// The renderer's stabilize correction for the clip at the playhead; the identity until its
    /// motion analysis has been read, which is done off the UI thread and then lays the handles
    /// out again.
    /// </summary>
    private Matrix3x2 Steady(Project project, Clip clip, Vector2 size)
    {
        if (!RenderGraphBuilder.IsStabilized(clip) || clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { Hash.Length: > 0 } item)
        {
            return Matrix3x2.Identity;
        }

        string path = _session.ProjectPath;
        string key = FormattableString.Invariant($"{path}|{item.Hash}|{clip.SourceStreamIndex}");
        bool have = _motion is { } known && known.Key == key;
        if ((!have || _motionStale) && _reading != key)
        {
            _reading = key;
            _motionStale = false;
            string hash = item.Hash;
            int stream = clip.SourceStreamIndex;
            _ = Task.Run(() => MotionStore.For(path).Load(hash, stream)).ContinueWith(
                read => _ui.Post(() =>
                {
                    if (_reading == key)
                    {
                        _reading = null;
                        _motion = (key, read.IsCompletedSuccessfully ? read.Result : null);
                        Build();
                    }
                }),
                TaskScheduler.Default);
        }

        // While a newer reading is on its way, the last one for this clip stands, so the handles do
        // not jump back to where the unstabilized picture would be and then forward again.
        return have ? RenderGraphBuilder.Stabilization(project, clip, _preview.Position, size, _motion!.Value.Motion, EffectCatalog.Registry) : Matrix3x2.Identity;
    }

    private static Vector2 Eval2(AnimatedValue value, string name, Flicks local, Vector2 fallback) =>
        ParamEval.Eval(value, ParamTargets.Transform.Param(name)!, local) is ParamValue.Float2 pair ? pair.Value : fallback;

    /// <summary>A clip's picture size in its own pixels, and how it is fitted: what the renderer places.</summary>
    private static (Vector2 Size, ConformPolicy Policy) SourceOf(Project project, Clip clip, Vector2 frame)
    {
        if (clip.MediaId is { } mediaId && project.MediaItem(mediaId) is { } item
            && item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex) is { Width: > 0, Height: > 0 } stream)
        {
            return (new Vector2(stream.Width, stream.Height), item.Conform);
        }

        if (clip.SequenceId is { } sequenceId && project.Sequence(sequenceId) is { } nested)
        {
            ProjectSettings inner = project.SettingsFor(nested);
            return (new Vector2(inner.Width, inner.Height), ConformPolicy.Fit);
        }

        return (frame, ConformPolicy.Stretch);
    }

    /// <summary>Latest wins: while one command is on its way, only the newest waits behind it.</summary>
    private void Send(ICommand command)
    {
        _pending = command;
        if (!_sending)
        {
            _pump = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        _sending = true;
        try
        {
            while (_pending is { } next)
            {
                _pending = null;
                try
                {
                    CommandResult result = await _session.ExecuteAsync(next).ConfigureAwait(true);
                    Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _log.Error(exception, "The preview's {Command} failed", CommandRegistry.NameOf(next));
                    Status = exception.Message;
                }
            }
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>What a drag started from.</summary>
    private sealed record Drag(MaskGrip Grip, Vector2 From, Vector2 FromSource, List<List<MaskNode>> Figures, Vector4 Bounds, float Feather);
}
