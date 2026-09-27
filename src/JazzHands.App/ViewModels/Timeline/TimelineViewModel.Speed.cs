using System.Globalization;
using System.Windows;
using System.Windows.Input;
using JazzHands.App.Controls.Timeline;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>What part of a clip's speed lane a point is on.</summary>
/// <param name="Clip">The clip.</param>
/// <param name="Body">Its body on the timeline, which the lane is drawn across.</param>
/// <param name="Point">The point under it, from 1, or null for the line between.</param>
public readonly record struct SpeedHit(ClipView Clip, Rect Body, int? Point);

/// <summary>
/// The speed lane (Phase 45): a remapped clip's speed curve drawn across it, shaped by hand.
/// </summary>
/// <remarks>
/// With the Select tool, a press on a point drags it: up and down for its speed, sideways for its
/// time, on frames and snapping as the playhead does (<c>clip.remap-move-point</c>). Ctrl and a
/// press on the line adds a point there at the speed the line has (<c>clip.remap-add-point</c>),
/// and the same drag moves it. A press on the line without Ctrl moves the clip as before. Every
/// step is a mergeable command through a <see cref="CommandPump"/>, so the picture follows the
/// pointer and the whole drag, add and all, is one undo step. The status line reads the speed,
/// the point's time and the clip's length as it goes.
/// </remarks>
public sealed partial class TimelineViewModel
{
    private CommandPump? _speedPump;
    private string? _speedClip;
    private int _speedPoint;
    private bool _speedMoved;

    /// <summary>The speed lane under a point, or null.</summary>
    public SpeedHit? SpeedAt(Point point) => SpeedAt(HitAt(point), point);

    /// <summary>The speed lane under a point on a hit already made, or null.</summary>
    internal SpeedHit? SpeedAt(TimelineHit hit, Point point)
    {
        if (Tools.Tool != TimelineTool.Select || hit is not { Clip: { } clip, Row: { } row, Edge: ClipEdge.None, Transition: null })
        {
            return null;
        }

        Rect body = VolumeLine.Body(Geometry, row, clip);
        if (!SpeedLine.Shown(clip, body))
        {
            return null;
        }

        IReadOnlyList<Flicks> points = SpeedLine.Points(clip);
        for (int index = 0; index < points.Count; index++)
        {
            if (Math.Abs(point.X - Geometry.XOf(points[index])) <= SpeedLine.HandleZone
                && Math.Abs(point.Y - SpeedLine.Y(body, SpeedLine.Level(clip, points[index]))) <= SpeedLine.HandleZone)
            {
                return new SpeedHit(clip, body, index + 1);
            }
        }

        Flicks time = Geometry.TimeAt(point.X, snapToFrame: false);
        return Math.Abs(point.Y - SpeedLine.Y(body, SpeedLine.Level(clip, time))) <= SpeedLine.LineZone
            ? new SpeedHit(clip, body, null)
            : null;
    }

    /// <summary>A press that may be on a speed lane. True when it started a speed drag.</summary>
    private bool SpeedDown(TimelineHit hit, Point point, ModifierKeys modifiers)
    {
        if (SpeedAt(hit, point) is not { } on)
        {
            return false;
        }

        ClipView clip = on.Clip;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            // On a frame inside the clip, at the speed the line has there; the new point is the
            // one the drag then moves, numbered as the command will number it.
            Flicks time = Flicks.Clamp(Geometry.TimeAt(point.X), clip.Start, clip.End);
            IReadOnlyList<Flicks> points = SpeedLine.Points(clip);
            int before = points.Count(existing => existing < time);
            bool first = points.Count == 0 && time > clip.Start;
            _speedClip = clip.Id;
            _speedPoint = before + 1 + (first ? 1 : 0);
            double speed = SpeedLine.Level(clip, time);
            Pump().Send("speed:" + clip.Id, () => new RemapAddPointCommand(clip.Id, time, speed));
        }
        else if (on.Point is { } index)
        {
            _speedClip = clip.Id;
            _speedPoint = index;
        }
        else
        {
            return false;
        }

        _speedMoved = false;
        _gesture = Gesture.Speed;
        SetCursor(TimelineCursor.Volume);
        return true;
    }

    /// <summary>The point follows the pointer once it has moved far enough to mean it.</summary>
    private void SpeedMove(Point point)
    {
        if (_speedClip is not { } id || Content.Clip(id) is not { } clip || Geometry.Row(clip.TrackId) is not { } row)
        {
            return;
        }

        if (!_speedMoved && Math.Abs(point.Y - _downAt.Y) < DragThreshold && Math.Abs(point.X - _downAt.X) < DragThreshold)
        {
            return;
        }

        _speedMoved = true;
        Rect body = VolumeLine.Body(Geometry, row, clip);
        double speed = SpeedLine.Speed(body, point.Y);
        Flicks at = Snapped(Geometry.TimeAt(point.X));
        int index = _speedPoint;
        Pump().Send("speed:" + id, () => new RemapMovePointCommand(id, index, at, speed));

        Rational rate = Content.Settings.FrameRate;
        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"Speed {speed * 100:0}% at {Core.Time.Timecode.Format(at, rate)}; the clip is {Core.Time.Timecode.Format(clip.Clip.Duration, rate)} long");
    }

    private void SpeedUp(Point point)
    {
        SpeedMove(point);
        _speedClip = null;
    }

    private CommandPump Pump()
    {
        if (_speedPump is null)
        {
            _speedPump = new CommandPump(_session, _ui);
            _speedPump.Refused += (_, message) => Status = message;
        }

        return _speedPump;
    }
}
