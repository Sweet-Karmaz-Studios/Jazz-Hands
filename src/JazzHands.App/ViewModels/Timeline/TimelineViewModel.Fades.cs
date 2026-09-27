using System.Windows;
using JazzHands.App.Controls.Timeline;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>Which of a sound clip's fade handles a point is on.</summary>
/// <param name="Clip">The clip.</param>
/// <param name="FadeIn">True for the fade in's handle, false for the fade out's.</param>
public readonly record struct FadeHit(ClipView Clip, bool FadeIn);

/// <summary>
/// A sound clip's fades, dragged from the handles at the top of its body.
/// </summary>
/// <remarks>
/// With the Select tool, a press on a handle and a drag sideways sets that fade's length
/// (<c>audio.set-fade-in</c> or <c>audio.set-fade-out</c>, its shape kept), on frames, never
/// into the other fade. Each step is a mergeable command sent through a <see cref="CommandPump"/>,
/// so the sound follows the pointer and the whole drag is one undo step. The Inspector's Fades
/// section sets the same fades by number, with their shapes.
/// </remarks>
public sealed partial class TimelineViewModel
{
    private CommandPump? _fadePump;
    private ClipView? _fadeClip;
    private bool _fadeIn;
    private bool _fadeMoved;

    /// <summary>The fade handle under a point, or null.</summary>
    public FadeHit? FadeAt(Point point) => FadeAt(HitAt(point), point);

    /// <summary>The fade handle under a point on a hit already made, or null.</summary>
    internal FadeHit? FadeAt(TimelineHit hit, Point point)
    {
        if (Tools.Tool != TimelineTool.Select || hit is not { Clip: { } clip, Row: { } row, Edge: ClipEdge.None, Transition: null })
        {
            return null;
        }

        Rect body = VolumeLine.Body(Geometry, row, clip);
        if (!FadeHandles.Shown(clip, body))
        {
            return null;
        }

        if (FadeHandles.On(FadeHandles.Handle(Geometry, clip, body, fadeIn: true), point))
        {
            return new FadeHit(clip, true);
        }

        return FadeHandles.On(FadeHandles.Handle(Geometry, clip, body, fadeIn: false), point)
            ? new FadeHit(clip, false)
            : null;
    }

    /// <summary>A press that may be on a fade handle. True when it started a fade drag.</summary>
    private bool FadeDown(TimelineHit hit, Point point)
    {
        if (FadeAt(hit, point) is not { } on)
        {
            return false;
        }

        _fadeClip = on.Clip;
        _fadeIn = on.FadeIn;
        _fadeMoved = false;
        _gesture = Gesture.Fade;
        SetCursor(TimelineCursor.Fade);
        return true;
    }

    /// <summary>The fade follows the pointer once it has moved far enough to mean it.</summary>
    private void FadeMove(Point point)
    {
        if (_fadeClip is not { } clip)
        {
            return;
        }

        if (!_fadeMoved && Math.Abs(point.X - _downAt.X) < DragThreshold)
        {
            return;
        }

        _fadeMoved = true;
        SendFade(clip, FadeHandles.Length(Geometry, clip, _fadeIn, point.X - _downAt.X));
    }

    private void FadeUp(Point point)
    {
        FadeMove(point);
        _fadeClip = null;
    }

    private void SendFade(ClipView clip, Flicks length)
    {
        string id = clip.Id;
        Interp curve = (_fadeIn ? clip.Clip.FadeIn : clip.Clip.FadeOut)?.Curve ?? Interp.Linear;

        if (_fadePump is null)
        {
            _fadePump = new CommandPump(_session, _ui);
            _fadePump.Refused += (_, message) => Status = message;
        }

        _fadePump.Send(
            (_fadeIn ? "fade-in:" : "fade-out:") + id,
            _fadeIn ? () => new SetAudioFadeInCommand(id, length, curve) : () => new SetAudioFadeOutCommand(id, length, curve));
    }
}
