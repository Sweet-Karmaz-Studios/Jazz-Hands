using System.Globalization;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Engine.Selection;
using Serilog;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>What part of a title's frame on the preview the pointer is on.</summary>
public enum TitleGrip
{
    /// <summary>Not on the title.</summary>
    None,

    /// <summary>Inside its box: a drag moves the text.</summary>
    Move,

    /// <summary>On a corner: a drag scales it about its centre.</summary>
    Scale,

    /// <summary>On the handle above it: a drag turns it about its centre.</summary>
    Rotate,
}

/// <summary>
/// The selected title's frame on the preview, and what dragging it does.
/// </summary>
/// <remarks>
/// <para>
/// The frame is where <c>title.measure</c> says the text is, after its own offset and zoom and the
/// clip's transform, so it sits on the picture whatever has been done to the clip. Every drag is a
/// command the CLI could send: moving is <c>param.set position</c> at the playhead (a keyframe
/// there when the position is animated); scaling and turning are <c>clip.set-transform</c> about
/// the text's centre, which moves the clip's anchor there and its position with it so nothing
/// jumps. While a drag runs only the newest value waits behind the one being sent, and each
/// command merges with the one before, so a drag is one undo step.
/// </para>
/// <para>
/// A double-click opens the text for editing in place: plain text when the title has no styled
/// spans, its markup when it does, sent as <c>title.set-text</c>.
/// </para>
/// </remarks>
public sealed partial class TitleHandlesViewModel : ObservableObject
{
    /// <summary>How far above the box the turning handle sits, in sequence pixels at 1080 lines.</summary>
    public const float RotateReach = 48.0f;

    private readonly ILogger _log = Log.ForContext<TitleHandlesViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IPreviewEngine _preview;
    private ICommand? _pending;
    private bool _sending;
    private Task _pump = Task.CompletedTask;
    private Drag? _drag;
    private bool _plainEdit;

    /// <summary>The box's corners, clockwise from the top left, in sequence pixels from the frame centre; null when no title is shown.</summary>
    [ObservableProperty]
    private IReadOnlyList<Vector2>? _corners;

    /// <summary>True while the text is open for editing on the preview.</summary>
    [ObservableProperty]
    private bool _isEditingText;

    /// <summary>The text being edited.</summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    /// <summary>Why the last change did not take, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the handles and follows the selection, the project and the playhead.</summary>
    public TitleHandlesViewModel(ISession session, SelectionService selection, IPreviewEngine preview, IUiDispatcher ui)
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

    /// <summary>The title shown, or null.</summary>
    public string? ClipId { get; private set; }

    /// <summary>The middle of the box on the picture, which scaling and turning are about.</summary>
    public Vector2 Centre => Corners is { Count: 4 } corners ? (corners[0] + corners[2]) / 2.0f : Vector2.Zero;

    /// <summary>Where the turning handle is: above the middle of the box's top edge, turned with it.</summary>
    public Vector2 RotateHandle
    {
        get
        {
            if (Corners is not { Count: 4 } corners)
            {
                return Vector2.Zero;
            }

            Vector2 top = (corners[0] + corners[1]) / 2.0f;
            Vector2 up = Vector2.Normalize(top - ((corners[3] + corners[2]) / 2.0f));
            return top + (float.IsFinite(up.X) ? up * RotateReach * Reach : Vector2.Zero);
        }
    }

    /// <summary>What is being sent, for a test to wait on.</summary>
    internal Task Sending => _pump;

    /// <summary>True while a drag is under way.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>The sequence's height over 1080, which handle distances scale by.</summary>
    private float Reach => _session.Project.ActiveSequence is { } sequence ? _session.Project.SettingsFor(sequence).Height / 1080.0f : 1.0f;

    /// <summary>Reads where the selected title is at the playhead.</summary>
    public void Refresh()
    {
        if (_drag is not null)
        {
            // The frame follows the commands as they land; a drag does not start over.
            Measure(ClipId);
            return;
        }

        Project project = _session.Project;
        string? chosen = _selection.Ids.Length == 1 && project.FindClip(_selection.Ids[0]) is { Clip.GeneratorId: TitleParams.GeneratorId } found
            && _preview.Position >= found.Clip.Start && _preview.Position < found.Clip.End
                ? found.Clip.Id
                : null;

        if (chosen != ClipId)
        {
            IsEditingText = false;
        }

        ClipId = chosen;
        Measure(chosen);
    }

    /// <summary>What a point on the picture is on, with a tolerance in sequence pixels.</summary>
    public TitleGrip HitTest(Vector2 at, float tolerance)
    {
        if (Corners is not { Count: 4 } corners)
        {
            return TitleGrip.None;
        }

        if (Vector2.Distance(at, RotateHandle) <= tolerance)
        {
            return TitleGrip.Rotate;
        }

        if (corners.Any(corner => Vector2.Distance(at, corner) <= tolerance))
        {
            return TitleGrip.Scale;
        }

        return Inside(corners, at) ? TitleGrip.Move : TitleGrip.None;
    }

    /// <summary>Starts a drag from a point on the picture.</summary>
    public bool Begin(TitleGrip grip, Vector2 at)
    {
        if (grip == TitleGrip.None || ClipId is not { } clipId || Corners is not { Count: 4 } corners
            || _session.Project.FindClip(clipId) is not { } found)
        {
            return false;
        }

        Clip clip = found.Clip;
        Flicks local = _preview.Position - clip.Start;
        Effect? own = clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect));
        Transform transform = clip.Transform ?? Transform.Identity;
        EffectDescriptor? title = Engine.Effects.EffectCatalog.Registry.Find(TitleParams.GeneratorId);
        Vector2 position = title?.Param(TitleParams.Position) is { } descriptor && ParamEval.Eval(own?.Parameter(TitleParams.Position), descriptor, local) is ParamValue.Float2 at0 ? at0.Value : Vector2.Zero;

        _drag = new Drag(
            grip,
            clipId,
            at,
            position,
            Vector2Of(transform.Position, local, Vector2.Zero),
            Vector2Of(transform.Scale, local, Vector2.One),
            FloatOf(transform.Rotation, local),
            Vector2Of(transform.Anchor, local, Vector2.Zero),
            corners,
            Centre);
        Status = string.Empty;
        return true;
    }

    /// <summary>The pointer moved during a drag: sends where the title now is.</summary>
    public void Move(Vector2 at, bool snap = false)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        switch (drag.Grip)
        {
            case TitleGrip.Move:
                Vector2 moved = drag.Position + ToTitleSpace(drag, at - drag.From);
                Send(new SetParamCommand(drag.ClipId, TitleParams.Position, Pair(moved), At: _preview.Position));
                break;

            case TitleGrip.Scale:
                float from = Vector2.Distance(drag.From, drag.Centre);
                float factor = from > 1e-3f ? Vector2.Distance(at, drag.Centre) / from : 1.0f;
                Send(TransformAbout(drag, drag.Scale * Math.Max(0.01f, factor), null));
                break;

            case TitleGrip.Rotate:
                float turned = Degrees(at - drag.Centre) - Degrees(drag.From - drag.Centre);
                float rotation = drag.Rotation + turned;
                Send(TransformAbout(drag, null, snap ? MathF.Round(rotation / 15.0f) * 15.0f : rotation));
                break;
        }
    }

    /// <summary>Ends a drag.</summary>
    public void End() => _drag = null;

    /// <summary>Opens the text for editing on the preview.</summary>
    public void BeginTextEdit()
    {
        if (ClipId is not { } clipId || _session.Project.FindClip(clipId) is not { } found)
        {
            return;
        }

        Clip clip = found.Clip;
        Effect? own = clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect));
        if (own?.Parameter(TitleParams.Text) is KeyframedValue { IsAnimated: true })
        {
            Status = "The text is keyframed: change it in the inspector at a keyframe.";
            return;
        }

        string markup = own?.Parameter(TitleParams.Text) is StaticValue { Value: ParamValue.Text { Value: var text } } ? text : "Title";
        TitleText parsed = TitleMarkup.Parse(markup);
        _plainEdit = parsed.Spans.All(span => span.Style.IsPlain);
        EditText = _plainEdit ? parsed.Plain : markup;
        IsEditingText = true;
    }

    /// <summary>Sends the edited text and closes the editor.</summary>
    public Task CommitTextAsync()
    {
        if (!IsEditingText || ClipId is not { } clipId)
        {
            return Task.CompletedTask;
        }

        IsEditingText = false;
        return RunAsync(new SetTitleTextCommand(clipId, EditText, Plain: _plainEdit));
    }

    /// <summary>Closes the editor without sending anything.</summary>
    public void CancelTextEdit() => IsEditingText = false;

    /// <summary>True when a point is inside a convex quadrilateral given clockwise on screen.</summary>
    internal static bool Inside(IReadOnlyList<Vector2> corners, Vector2 at)
    {
        int sign = 0;
        for (int index = 0; index < corners.Count; index++)
        {
            Vector2 a = corners[index];
            Vector2 b = corners[(index + 1) % corners.Count];
            float cross = ((b.X - a.X) * (at.Y - a.Y)) - ((b.Y - a.Y) * (at.X - a.X));
            int side = Math.Sign(cross);
            if (side != 0)
            {
                if (sign != 0 && side != sign)
                {
                    return false;
                }

                sign = side;
            }
        }

        return true;
    }

    /// <summary>
    /// A clip transform with a new scale or rotation about the text's centre (only the one that
    /// changes, so a scale and a turn are two steps to undo): the anchor moves
    /// to the centre, and the position by what keeps the picture still while it does.
    /// </summary>
    internal static SetClipTransformCommand TransformAbout(Drag drag, Vector2? scale, float? rotation)
    {
        // The centre in the picture's own coordinates: undo the clip's transform on the frame.
        Matrix3x2 before = Linear(drag.Scale, drag.Rotation);
        Matrix3x2.Invert(before, out Matrix3x2 inverse);
        Vector2 centre = Vector2.Transform(drag.Centre - drag.Anchor - drag.ClipPosition, inverse) + drag.Anchor;

        // Moving the anchor to it without changing the scale or turn: T(p) = L(p - a) + a + pos
        // stays the same when pos becomes pos + (L - I)(a' - a).
        Vector2 shift = Vector2.Transform(centre - drag.Anchor, before) - (centre - drag.Anchor);
        Vector2 position = drag.ClipPosition + shift;

        return new SetClipTransformCommand(
            drag.ClipId,
            X: Round(position.X),
            Y: Round(position.Y),
            ScaleX: scale is { } across ? Math.Round(across.X, 4) : null,
            ScaleY: scale is { } down ? Math.Round(down.Y, 4) : null,
            Rotation: rotation is { } turn ? Math.Round(turn, 2) : null,
            AnchorX: Round(centre.X),
            AnchorY: Round(centre.Y));
    }

    private static Matrix3x2 Linear(Vector2 scale, float rotation) =>
        Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(rotation * MathF.PI / 180.0f);

    /// <summary>A move on the picture as a move of the title's own position, through whatever scales and turns it.</summary>
    private static Vector2 ToTitleSpace(Drag drag, Vector2 delta)
    {
        // The frame's edges say how the title's axes lie on the picture: top edge across, left edge down.
        IReadOnlyList<Vector2> corners = drag.Corners;
        Vector2 across = corners[1] - corners[0];
        Vector2 down = corners[3] - corners[0];
        float width = across.Length();
        float height = down.Length();
        if (width < 1e-3f || height < 1e-3f)
        {
            return delta;
        }

        // The box's size before the transform is unknown here, but its edges are unit axes times a
        // common scale per axis; the ratio of the picture's box to the title's is what the scale is.
        var axes = new Matrix3x2(across.X / width, across.Y / width, down.X / height, down.Y / height, 0, 0);
        if (!Matrix3x2.Invert(axes, out Matrix3x2 inverse))
        {
            return delta;
        }

        Vector2 local = Vector2.Transform(delta, inverse);
        float scaleX = drag.Scale.X == 0 ? 1 : Math.Abs(drag.Scale.X);
        float scaleY = drag.Scale.Y == 0 ? 1 : Math.Abs(drag.Scale.Y);
        return new Vector2(local.X / scaleX, local.Y / scaleY);
    }

    private static float Degrees(Vector2 direction) => MathF.Atan2(direction.Y, direction.X) * 180.0f / MathF.PI;

    private static double Round(float value) => Math.Round(value, 2);

    private static string Pair(Vector2 value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value.X, 2)}, {Math.Round(value.Y, 2)}");

    private static Vector2 Vector2Of(AnimatedValue value, Flicks local, Vector2 fallback) =>
        AnimationEvaluator.Evaluate(value, local) is ParamValue.Float2 pair ? pair.Value : fallback;

    private static float FloatOf(AnimatedValue value, Flicks local) =>
        AnimationEvaluator.Evaluate(value, local) is ParamValue.Float number ? number.Value : 0.0f;

    private void Measure(string? clipId)
    {
        if (clipId is null)
        {
            Corners = null;
            return;
        }

        try
        {
            TitleMeasureInfo measured = _session.Query(new MeasureTitleQuery(clipId, _preview.Position));
            Corners = [.. measured.Corners.Select(corner => new Vector2((float)corner.X, (float)corner.Y))];
        }
        catch (CommandException)
        {
            // An empty title, or one the playhead has just left: nothing to hold.
            Corners = null;
        }

        OnPropertyChanged(nameof(Centre));
        OnPropertyChanged(nameof(RotateHandle));
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
                await RunAsync(next).ConfigureAwait(true);
            }
        }
        finally
        {
            _sending = false;
        }
    }

    private async Task RunAsync(ICommand command)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "The preview's {Command} failed", CommandRegistry.NameOf(command));
            Status = exception.Message;
        }
    }

    /// <summary>What a drag started from.</summary>
    internal sealed record Drag(
        TitleGrip Grip,
        string ClipId,
        Vector2 From,
        Vector2 Position,
        Vector2 ClipPosition,
        Vector2 Scale,
        float Rotation,
        Vector2 Anchor,
        IReadOnlyList<Vector2> Corners,
        Vector2 Centre);
}
