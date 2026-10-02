using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Selection;
using Serilog;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>What part of the 3D handle the pointer is on.</summary>
public enum Gizmo3DGrip
{
    /// <summary>Not on the handle.</summary>
    None,

    /// <summary>The square at the pivot: a drag moves it across the picture, along the world's X and Y.</summary>
    Move,

    /// <summary>The red arrow: a drag moves it along X.</summary>
    MoveX,

    /// <summary>The green arrow: a drag moves it along Y.</summary>
    MoveY,

    /// <summary>The blue arrow: a drag moves it nearer or further.</summary>
    MoveZ,

    /// <summary>The knob above: a drag up or down tips it about X.</summary>
    TurnX,

    /// <summary>The knob to the right: a drag across turns it about Y.</summary>
    TurnY,

    /// <summary>The ring: a drag round it turns it about Z.</summary>
    TurnZ,
}

/// <summary>
/// The selected 3D layer's, text's, shape's or model's handle on the preview (Phase 49a): arrows
/// along the world's axes as the camera sees them, a square to move it across the picture, a ring
/// to turn it about Z and two knobs to tip and turn it about X and Y.
/// </summary>
/// <remarks>
/// Drawn from <c>clip.measure-3d</c> at the playhead, so it sits on the pivot through whatever
/// camera there is, at the same size on screen however big the monitor is (the view says how many
/// sequence pixels a screen pixel is): the arrows are the projected steps scaled together, so the
/// one seen end on is still the shortest. A drag along an arrow is its move on screen measured along
/// the projected step, turned into world pixels by the step's length; the square solves for both X
/// and Y the same way. Every
/// drag sends one parameter (<c>transform.position</c>, <c>transform.z</c> or a turn) with
/// <c>param.set</c> at the playhead, latest wins, merging into one undo step.
/// </remarks>
public sealed partial class Gizmo3DViewModel : ObservableObject
{
    /// <summary>The ring's radius, in screen pixels: outside the arrows, so neither covers the other.</summary>
    public const float Ring = 70.0f;

    /// <summary>How far past the ring the knobs sit, in screen pixels.</summary>
    public const float KnobReach = 20.0f;

    /// <summary>The longest arrow's length, in screen pixels.</summary>
    public const float Arrow = 48.0f;

    /// <summary>Degrees a knob turns for each screen pixel dragged.</summary>
    public const float DegreesPerPixel = 0.4f;

    private readonly ILogger _log = Log.ForContext<Gizmo3DViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IPreviewEngine _preview;
    private ICommand? _pending;
    private bool _sending;
    private Task _pump = Task.CompletedTask;
    private Drag? _drag;

    /// <summary>Where the selected clip is through the camera, or null when there is no handle to show.</summary>
    [ObservableProperty]
    private Layer3DPlaceInfo? _place;

    /// <summary>Why the last change did not take, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the handle and follows the selection, the project and the playhead.</summary>
    public Gizmo3DViewModel(ISession session, SelectionService selection, IPreviewEngine preview, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _preview = preview;
        _selection.Changed += (_, _) => ui.Post(Refresh);
        _session.ProjectChanged += (_, _) => ui.Post(Refresh);
        _preview.PlayheadMoved += (_, _) => ui.Post(Refresh);
        Refresh();
    }

    /// <summary>The clip the handle is on, or null.</summary>
    public string? ClipId { get; private set; }

    /// <summary>
    /// How many sequence pixels one screen pixel is on the monitor now, which the view sets as the
    /// picture is fitted or zoomed; the handle keeps its size on screen by it.
    /// </summary>
    public float PixelsPerScreen
    {
        get => _pixelsPerScreen;
        set
        {
            if (value > 0.0f && float.IsFinite(value) && value != _pixelsPerScreen)
            {
                _pixelsPerScreen = value;
                OnPropertyChanged(string.Empty);
            }
        }
    }

    private float _pixelsPerScreen = 1.0f;

    /// <summary>True when there is a handle to draw.</summary>
    public bool IsShown => Place?.Pivot is not null;

    /// <summary>The pivot on the frame, in sequence pixels from its centre.</summary>
    public Vector2 Pivot => Point(Place?.Pivot);

    /// <summary>The tip of the X arrow.</summary>
    public Vector2 XEnd => Pivot + (StepX * Shown);

    /// <summary>The tip of the Y arrow.</summary>
    public Vector2 YEnd => Pivot + (StepY * Shown);

    /// <summary>The tip of the Z arrow; seen straight on it leans up and to the left (see <see cref="StepZ"/>).</summary>
    public Vector2 ZEnd => Pivot + (StepZ * Shown);

    /// <summary>The ring's radius here, in sequence pixels.</summary>
    public float RingRadius => Ring * PixelsPerScreen;

    /// <summary>The knob that tips it about X: above the ring.</summary>
    public Vector2 TurnXKnob => Pivot - new Vector2(0.0f, (Ring + KnobReach) * PixelsPerScreen);

    /// <summary>The knob that turns it about Y: right of the ring.</summary>
    public Vector2 TurnYKnob => Pivot + new Vector2((Ring + KnobReach) * PixelsPerScreen, 0.0f);

    /// <summary>A step right as the camera sees it, in sequence pixels on the frame.</summary>
    internal Vector2 StepX => Place?.XAxis is { } x ? Point(x) - Pivot : Vector2.Zero;

    /// <summary>A step down as the camera sees it.</summary>
    internal Vector2 StepY => Place?.YAxis is { } y ? Point(y) - Pivot : Vector2.Zero;

    /// <summary>
    /// A step away as the camera sees it. Seen straight on, away is a point; the step then leans up
    /// and to the left at seven tenths of the longer of the other two, and dragging along it still
    /// means away.
    /// </summary>
    internal Vector2 StepZ
    {
        get
        {
            Vector2 seen = Place?.ZAxis is { } z ? Point(z) - Pivot : Vector2.Zero;
            float longest = MathF.Max(StepX.Length(), StepY.Length());
            return seen.Length() >= 0.12f * longest ? seen : Vector2.Normalize(new Vector2(-1.0f, -1.0f)) * 0.7f * longest;
        }
    }

    /// <summary>How much the steps are scaled by to be drawn: the longest at <see cref="Arrow"/> screen pixels.</summary>
    private float Shown
    {
        get
        {
            float longest = MathF.Max(StepX.Length(), MathF.Max(StepY.Length(), StepZ.Length()));
            return longest < 1e-3f ? 0.0f : Arrow * PixelsPerScreen / longest;
        }
    }

    /// <summary>True while a drag is under way.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>What is being sent, for a test to wait on.</summary>
    internal Task Sending => _pump;

    /// <summary>Reads where the selected 3D clip is at the playhead.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        string? chosen = _selection.Ids.Length == 1
            && project.FindClip(_selection.Ids[0]) is { Track.Kind: TrackKind.Video } found
            && (found.Clip.Layer3D is not null || SceneObjects.IsMesh(found.Clip.GeneratorId))
            && _preview.Position >= found.Clip.Start && _preview.Position < found.Clip.End
                ? found.Clip.Id
                : null;
        if (chosen != ClipId)
        {
            _drag = null;
        }

        ClipId = chosen;
        try
        {
            Place = chosen is null ? null : _session.Query(new Measure3DQuery(chosen, _preview.Position));
        }
        catch (CommandException)
        {
            Place = null;
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>What a point on the picture is on, with a tolerance in sequence pixels.</summary>
    public Gizmo3DGrip HitTest(Vector2 at, float tolerance)
    {
        if (!IsShown)
        {
            return Gizmo3DGrip.None;
        }

        float reach = tolerance * 1.5f;
        if (Vector2.Distance(at, TurnXKnob) <= reach)
        {
            return Gizmo3DGrip.TurnX;
        }

        if (Vector2.Distance(at, TurnYKnob) <= reach)
        {
            return Gizmo3DGrip.TurnY;
        }

        if (Vector2.Distance(at, Pivot) <= reach)
        {
            return Gizmo3DGrip.Move;
        }

        foreach ((Gizmo3DGrip grip, Vector2 tip) in new[] { (Gizmo3DGrip.MoveX, XEnd), (Gizmo3DGrip.MoveY, YEnd), (Gizmo3DGrip.MoveZ, ZEnd) })
        {
            if (Vector2.Distance(at, tip) <= reach || Along(at, Pivot, tip) <= tolerance)
            {
                return grip;
            }
        }

        return MathF.Abs(Vector2.Distance(at, Pivot) - RingRadius) <= tolerance ? Gizmo3DGrip.TurnZ : Gizmo3DGrip.None;
    }

    /// <summary>Starts a drag from a point on the picture.</summary>
    public bool Begin(Gizmo3DGrip grip, Vector2 at)
    {
        if (grip == Gizmo3DGrip.None || ClipId is not { } clipId || Place is not { Pivot: not null } place)
        {
            return false;
        }

        _drag = new Drag(grip, clipId, at, place, Pivot, StepX, StepY, StepZ);
        Status = string.Empty;
        return true;
    }

    /// <summary>The pointer moved during a drag: sends where the clip now is; <paramref name="snap"/> turns in steps of 15 degrees.</summary>
    public void Move(Vector2 at, bool snap = false)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        Vector2 delta = at - drag.From;
        Layer3DPlaceInfo start = drag.Place;
        double step = start.AxisLength;
        float degrees = DegreesPerPixel / PixelsPerScreen;
        switch (drag.Grip)
        {
            case Gizmo3DGrip.Move:
                (float across, float down) = Solve(drag.X, drag.Y, delta);
                SendPosition(drag, start.X + (across * step), start.Y + (down * step));
                break;

            case Gizmo3DGrip.MoveX:
                SendPosition(drag, start.X + (Measure(delta, drag.X) * step), start.Y);
                break;

            case Gizmo3DGrip.MoveY:
                SendPosition(drag, start.X, start.Y + (Measure(delta, drag.Y) * step));
                break;

            case Gizmo3DGrip.MoveZ:
                Send(drag, "transform.z", Number(start.Z + (Measure(delta, drag.Z) * step)));
                break;

            case Gizmo3DGrip.TurnX:
                Send(drag, "transform.rotation-x", Number(Snap(start.RotationX - (delta.Y * degrees), snap)));
                break;

            case Gizmo3DGrip.TurnY:
                Send(drag, "transform.rotation-y", Number(Snap(start.RotationY + (delta.X * degrees), snap)));
                break;

            case Gizmo3DGrip.TurnZ:
                double turned = Degrees(at - drag.Pivot) - Degrees(drag.From - drag.Pivot);
                Send(drag, "transform.rotation", Number(Snap(start.Rotation + Unwrapped(turned), snap)));
                break;
        }
    }

    /// <summary>Ends a drag.</summary>
    public void End() => _drag = null;

    /// <summary>How far a move goes along an arrow, in arrow lengths.</summary>
    internal static float Measure(Vector2 delta, Vector2 arrow)
    {
        float length = arrow.LengthSquared();
        return length < 1e-6f ? 0.0f : Vector2.Dot(delta, arrow) / length;
    }

    /// <summary>A move on screen as so many X arrows and Y arrows; each alone when the two lie along one line.</summary>
    internal static (float X, float Y) Solve(Vector2 x, Vector2 y, Vector2 delta)
    {
        float determinant = (x.X * y.Y) - (x.Y * y.X);
        if (MathF.Abs(determinant) < 1e-3f * MathF.Max(1.0f, x.LengthSquared() + y.LengthSquared()))
        {
            return (Measure(delta, x), Measure(delta, y));
        }

        return (((delta.X * y.Y) - (delta.Y * y.X)) / determinant, ((x.X * delta.Y) - (x.Y * delta.X)) / determinant);
    }

    /// <summary>How far a point is from a segment.</summary>
    private static float Along(Vector2 at, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-6f ? 0.0f : Math.Clamp(Vector2.Dot(at - a, ab) / length, 0.0f, 1.0f);
        return Vector2.Distance(at, a + (ab * t));
    }

    private static Vector2 Point(FramePoint? point) => point is null ? Vector2.Zero : new Vector2((float)point.X, (float)point.Y);

    private static double Degrees(Vector2 direction) => Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;

    private static double Unwrapped(double turned) => turned > 180.0 ? turned - 360.0 : turned < -180.0 ? turned + 360.0 : turned;

    private static double Snap(double value, bool snap) => snap ? Math.Round(value / 15.0) * 15.0 : value;

    private static string Number(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    private void SendPosition(Drag drag, double x, double y) =>
        Send(drag, "transform.position", string.Create(CultureInfo.InvariantCulture, $"{Math.Round(x, 2)}, {Math.Round(y, 2)}"));

    /// <summary>Latest wins: while one command is on its way, only the newest waits behind it.</summary>
    private void Send(Drag drag, string param, string value)
    {
        _pending = new SetParamCommand(drag.ClipId, param, value, At: _preview.Position);
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
                    _log.Error(exception, "The 3D handle's {Command} failed", CommandRegistry.NameOf(next));
                    Status = exception.Message;
                }
            }
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>What a drag started from: the place then, and the arrows on screen.</summary>
    private sealed record Drag(Gizmo3DGrip Grip, string ClipId, Vector2 From, Layer3DPlaceInfo Place, Vector2 Pivot, Vector2 X, Vector2 Y, Vector2 Z);
}
