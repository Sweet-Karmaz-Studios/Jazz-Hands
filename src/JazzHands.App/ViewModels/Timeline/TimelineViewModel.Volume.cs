using System.Windows;
using System.Windows.Input;
using JazzHands.App.Controls.Timeline;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>What part of an audio clip's volume line a point is on.</summary>
/// <param name="Clip">The clip.</param>
/// <param name="Body">Its body on the timeline, which the line is drawn across.</param>
/// <param name="Keyframe">The keyframe under the point, on the sequence, or null for the line between.</param>
public readonly record struct VolumeHit(ClipView Clip, Rect Body, Flicks? Keyframe);

/// <summary>
/// The rubber band: an audio clip's volume drawn as a line across it, and dragged.
/// </summary>
/// <remarks>
/// With the Select tool, a press on the line of a clip whose volume is one level drags that
/// level (<c>audio.set-gain</c>). A press on a keyframe drags that keyframe's level
/// (<c>audio.set-gain --at</c> its time). Ctrl and a press adds a keyframe where the press is,
/// at the level the line already has there, so the sound does not change until it is dragged.
/// On a line that already has keyframes, a press between them moves the clip as it always did.
/// Every step of a drag is a mergeable command sent through a <see cref="CommandPump"/>, so the
/// sound follows the pointer and the whole drag is one undo step.
/// </remarks>
public sealed partial class TimelineViewModel
{
    private CommandPump? _volumePump;
    private ClipView? _volumeClip;
    private Flicks? _volumeAt;
    private bool _volumeMoved;

    /// <summary>The volume line under a point, or null.</summary>
    public VolumeHit? VolumeAt(Point point) => VolumeAt(HitAt(point), point);

    /// <summary>The volume line under a point on a hit already made, or null.</summary>
    internal VolumeHit? VolumeAt(TimelineHit hit, Point point)
    {
        if (Tools.Tool != TimelineTool.Select || hit is not { Clip: { } clip, Row: { } row, Edge: ClipEdge.None, Transition: null })
        {
            return null;
        }

        Rect body = VolumeLine.Body(Geometry, row, clip);
        if (!VolumeLine.Shown(clip, body))
        {
            return null;
        }

        foreach (Flicks key in VolumeLine.Keyframes(clip))
        {
            if (Math.Abs(point.X - Geometry.XOf(key)) <= VolumeLine.HandleZone
                && Math.Abs(point.Y - VolumeLine.Y(body, VolumeLine.Level(clip, key))) <= VolumeLine.HandleZone)
            {
                return new VolumeHit(clip, body, key);
            }
        }

        Flicks time = Geometry.TimeAt(point.X, snapToFrame: false);
        return Math.Abs(point.Y - VolumeLine.Y(body, VolumeLine.Level(clip, time))) <= VolumeLine.LineZone
            ? new VolumeHit(clip, body, null)
            : null;
    }

    /// <summary>A press that may be on a volume line. True when it started a volume drag.</summary>
    private bool VolumeDown(TimelineHit hit, Point point, ModifierKeys modifiers)
    {
        if (VolumeAt(hit, point) is not { } on)
        {
            return false;
        }

        ClipView clip = on.Clip;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            // On a frame, inside the clip, at the level the line has there.
            Flicks time = Geometry.TimeAt(point.X);
            time = Flicks.Max(clip.Start, Flicks.Min(clip.End, time));
            _volumeClip = clip;
            _volumeAt = time;
            SendVolume(VolumeLine.Level(clip, time));
        }
        else if (on.Keyframe is { } key)
        {
            _volumeClip = clip;
            _volumeAt = key;
        }
        else if (clip.Clip.Volume is KeyframedValue { IsAnimated: true })
        {
            return false;
        }
        else
        {
            _volumeClip = clip;
            _volumeAt = null;
        }

        _volumeMoved = false;
        _gesture = Gesture.Volume;
        SetCursor(TimelineCursor.Volume);
        return true;
    }

    /// <summary>The level follows the pointer once it has moved far enough to mean it.</summary>
    private void VolumeMove(Point point)
    {
        if (_volumeClip is not { } clip || Geometry.Row(clip.TrackId) is not { } row)
        {
            return;
        }

        if (!_volumeMoved && Math.Abs(point.Y - _downAt.Y) < DragThreshold)
        {
            return;
        }

        _volumeMoved = true;
        SendVolume(VolumeLine.Db(VolumeLine.Body(Geometry, row, clip), point.Y));
    }

    private void VolumeUp(Point point)
    {
        VolumeMove(point);
        _volumeClip = null;
        _volumeAt = null;
    }

    private void SendVolume(double db)
    {
        string id = _volumeClip!.Id;
        Flicks? at = _volumeAt;

        if (_volumePump is null)
        {
            _volumePump = new CommandPump(_session, _ui);
            _volumePump.Refused += (_, message) => Status = message;
        }

        _volumePump.Send("volume:" + id, () => new SetAudioGainCommand(id, db, at));
    }
}
