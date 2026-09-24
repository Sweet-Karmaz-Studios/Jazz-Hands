using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Effects;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>
/// Transitions on the timeline: their bars, dragging an edge to change the duration, the menu,
/// dropping one on a cut from the effects panel, and deleting the selected ones.
/// </summary>
/// <remarks>
/// A bar spans where the transition plays and sits along the bottom of the clips it joins. A
/// centred transition's edges move together, so dragging either one keeps it on the cut; one
/// that ends or starts at the cut has one edge on the cut, which stays, and the other drags. The
/// drag sends one <c>transition.set</c> when the button comes up, which the engine fits to what
/// the clips have room for, and the picture and sound transitions at the same cut change together.
/// </remarks>
public sealed partial class TimelineViewModel
{
    /// <summary>How near a cut, in pixels, a transition dropped from the effects panel lands on it.</summary>
    public const double CutZone = 24.0;

    private TransitionView? _resized;
    private ClipEdge _resizedEdge;
    private Flicks? _resizedTo;

    /// <summary>The transition bar under a point on a row, or null.</summary>
    private TransitionView? TransitionAt(TrackView track, Controls.Timeline.TrackRow row, Point point)
    {
        foreach (TransitionView bar in track.Transitions)
        {
            Rect band = Geometry.TransitionBand(row, bar.Start, bar.End);
            if (point.Y >= band.Top && point.Y <= band.Bottom && point.X >= band.Left - 2 && point.X <= band.Right + 2)
            {
                return bar;
            }
        }

        return null;
    }

    /// <summary>True for an edge of a bar that can be dragged: either of a centred one, the free one otherwise.</summary>
    internal static bool Movable(TransitionView bar, ClipEdge edge) => bar.Transition.Alignment switch
    {
        TransitionAlignment.EndOfLeft => edge == ClipEdge.Start,
        TransitionAlignment.StartOfRight => edge == ClipEdge.End,
        _ => edge != ClipEdge.None,
    };

    /// <summary>A press on a transition's bar: selects it, and on a movable edge starts a resize.</summary>
    private void TransitionDown(TimelineHit hit, TransitionView bar, ModifierKeys modifiers)
    {
        SetSelectedEdit(null);
        if (modifiers != ModifierKeys.None || !Selected.Contains(bar.Id))
        {
            Select([bar.Id], ModeFor(modifiers));
        }

        if (Movable(bar, hit.Edge) && Tools.Tool == TimelineTool.Select)
        {
            _gesture = Gesture.TransitionEdge;
            _resized = bar;
            _resizedEdge = hit.Edge;
            _resizedTo = null;
            return;
        }

        _gesture = Gesture.None;
    }

    /// <summary>The duration a drag of a bar's edge to a point asks for, a frame at least.</summary>
    internal static Flicks ResizedDuration(TransitionView bar, ClipEdge edge, Flicks to, Rational frameRate)
    {
        Flicks duration = bar.Transition.Alignment switch
        {
            TransitionAlignment.EndOfLeft => bar.Cut - to,
            TransitionAlignment.StartOfRight => to - bar.Cut,
            _ => (edge == ClipEdge.Start ? bar.Cut - to : to - bar.Cut) * 2,
        };

        return Flicks.Max(duration, Flicks.FromFrames(1, frameRate));
    }

    private void PreviewTransitionEdge(Point point)
    {
        if (_resized is not { } bar)
        {
            return;
        }

        Flicks duration = ResizedDuration(bar, _resizedEdge, Geometry.TimeAt(point.X), Geometry.FrameRate);
        (Flicks before, Flicks after) = Core.Queries.TransitionTiming.Split(duration, bar.Transition.Alignment, Geometry.FrameRate);
        _resizedTo = duration;

        Ghost = new TimelineGhost([new GhostClip(bar.TrackId, bar.Cut - before, bar.Cut + after)], null);
        Status = $"{bar.Name}: {Core.Time.Timecode.Format(duration, Geometry.FrameRate)}";
        Invalidate(TimelineLayers.Ghost);
    }

    private void CommitTransitionEdge()
    {
        if (_resized is { } bar && _resizedTo is { } duration && duration != bar.Transition.Duration)
        {
            _ = RunAsync(new SetTransitionCommand(bar.Id, Duration: duration));
        }

        _resized = null;
        _resizedTo = null;
    }

    /// <summary>The right-click menu for a transition's bar.</summary>
    private List<TimelineMenuItem> TransitionMenu(TransitionView bar)
    {
        if (!Selected.Contains(bar.Id))
        {
            Select([bar.Id], SelectMode.Replace);
        }

        TransitionAlignment alignment = bar.Transition.Alignment;
        TimelineMenuItem Align(string header, TransitionAlignment to) =>
            new(header, null, () => RunAsync(new SetTransitionCommand(bar.Id, Alignment: to)), alignment != to);

        return
        [
            Align("Centre on cut", TransitionAlignment.Centered),
            Align("End at cut", TransitionAlignment.EndOfLeft),
            Align("Start at cut", TransitionAlignment.StartOfRight),
            TimelineMenuItem.Separator,
            new TimelineMenuItem($"Make {bar.Name} the default", null, () => RunAsync(new SetDefaultTransitionCommand(bar.Transition.TypeId))),
            new TimelineMenuItem("Remove transition", "Delete", () => RunAsync(new RemoveTransitionCommand(bar.Id))),
        ];
    }

    /// <summary>The cut on a track nearest a point, within <see cref="CutZone"/> pixels, as its two clips.</summary>
    private (ClipView Left, ClipView Right)? CutNear(TrackView track, double x)
    {
        (ClipView, ClipView)? best = null;
        double nearest = CutZone;

        for (int index = 1; index < track.Clips.Length; index++)
        {
            ClipView left = track.Clips[index - 1];
            ClipView right = track.Clips[index];
            if (left.End != right.Start)
            {
                continue;
            }

            double distance = Math.Abs(Geometry.XOf(left.End) - x);
            if (distance <= nearest)
            {
                nearest = distance;
                best = (left, right);
            }
        }

        return best;
    }

    /// <summary>
    /// What a transition dragged from the effects panel would do at a point: change the type of
    /// the transition under it, or go on the nearest cut. Null with a reason when neither.
    /// </summary>
    private (ICommand? Command, string Message) TransitionDrop(EffectDescriptor descriptor, TimelineHit hit, Point point)
    {
        if (hit.Row is not { } row || Content.Track(row.TrackId) is not { } track)
        {
            return (null, $"Drop {descriptor.Name} on a cut between two clips.");
        }

        bool picture = descriptor.Kind == EffectKind.Transition;
        if (track.Track.Kind != (picture ? TrackKind.Video : TrackKind.Audio))
        {
            return (null, $"{descriptor.Name} goes between {(picture ? "pictures, on a video track" : "sounds, on an audio track")}.");
        }

        if (hit.Transition is { } bar)
        {
            return string.Equals(bar.Transition.TypeId, descriptor.TypeId, StringComparison.Ordinal)
                ? (null, $"That is already a {descriptor.Name}.")
                : (new SetTransitionCommand(bar.Id, descriptor.TypeId), $"Make it a {descriptor.Name}.");
        }

        if (CutNear(track, point.X) is not ({ } left, { } right))
        {
            return (null, $"Drop {descriptor.Name} on a cut, where one clip ends and the next starts.");
        }

        if (track.Transitions.FirstOrDefault(view => view.Transition.LeftClipId == left.Id) is { } existing)
        {
            return (new SetTransitionCommand(existing.Id, descriptor.TypeId), $"Make the transition between '{left.Clip.Name}' and '{right.Clip.Name}' a {descriptor.Name}.");
        }

        // Short of source, a transition dropped by hand holds a frame, as Premiere's does, and the
        // status line says so after it lands.
        return (
            new AddTransitionCommand(left.Id, right.Id, descriptor.TypeId, Handles: TransitionHandles.Hold, Audio: false),
            $"Add {descriptor.Name} between '{left.Clip.Name}' and '{right.Clip.Name}'.");
    }

    /// <summary>After a transition is dropped: says when it holds a frame because a clip is short of source.</summary>
    private void ReportHolds(ICommand command)
    {
        if (command is not AddTransitionCommand add)
        {
            return;
        }

        Project project = _session.Project;
        if (project.TrackOf(add.LeftClipId) is not { } track
            || Core.Queries.TransitionTiming.Between(track, add.LeftClipId, add.RightClipId) is not { } added
            || Core.Queries.TransitionTiming.Span(track, added, Geometry.FrameRate) is not { } span)
        {
            return;
        }

        (Flicks leftShort, Flicks rightShort) = Core.Queries.TransitionTiming.Shortfall(project, span);
        if (leftShort.Value > 0 || rightShort.Value > 0)
        {
            Status = $"{EffectCatalog.Registry.Find(added.TypeId)?.Name ?? added.TypeId} added; a clip is short of source for it, so it holds a frame. Shorten it, or trim the clips back.";
        }
    }

    /// <summary>Delete with transitions selected and no clips: takes the transitions off.</summary>
    private bool DeleteSelectedTransitions()
    {
        // In the order they were selected: the set's own order follows string hashing, which
        // changes from one run to the next.
        string[] transitions = [.. InSelectedOrder().Where(id => Content.Transition(id) is not null)];
        if (transitions.Length == 0 || SelectedClipIds().Length > 0)
        {
            return false;
        }

        ImmutableArray<ICommand> commands = [.. transitions.Select(id => (ICommand)new RemoveTransitionCommand(id))];
        _ = RunAsync(Batch(commands, "Remove transitions"));
        return true;
    }

    /// <summary>The menu entry that puts the default transition on a cut at a clip's edge, when there is one.</summary>
    private TimelineMenuItem? DefaultTransitionAt(ClipView clip, ClipEdge edge)
    {
        if (edge == ClipEdge.None || Content.Track(clip.TrackId) is not { } track)
        {
            return null;
        }

        Flicks cut = edge == ClipEdge.End ? clip.End : clip.Start;
        bool isCut = track.Clips.Any(other => edge == ClipEdge.End ? other.Start == cut : other.End == cut);
        bool taken = track.Transitions.Any(view => view.Cut == cut);
        if (!isCut || taken)
        {
            return null;
        }

        TransitionKinds kind = track.Track.Kind == TrackKind.Audio ? TransitionKinds.Audio : TransitionKinds.Video;
        return new TimelineMenuItem(
            "Add default transition",
            kind == TransitionKinds.Audio ? "Ctrl+Shift+D" : "Ctrl+D",
            () => RunAsync(new ApplyDefaultTransitionCommand(cut, track.Id, kind, SequenceId: SequenceId)));
    }
}
