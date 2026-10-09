using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using JazzHands.App.Controls.Timeline;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>What the pointer is over, which decides the cursor and what a press does.</summary>
public enum TimelineCursor
{
    /// <summary>Nothing special.</summary>
    Arrow,

    /// <summary>A clip's start edge: dragging trims it.</summary>
    TrimStart,

    /// <summary>A clip's end edge: dragging trims it.</summary>
    TrimEnd,

    /// <summary>Over the ruler or the playhead: dragging scrubs.</summary>
    Scrub,

    /// <summary>Clips are being moved.</summary>
    Move,

    /// <summary>The razor, over a clip: a click cuts here.</summary>
    Razor,

    /// <summary>The hand: dragging scrolls.</summary>
    Hand,

    /// <summary>An audio clip's volume line: dragging sets the level.</summary>
    Volume,

    /// <summary>A sound clip's fade handle: dragging sideways sets the fade.</summary>
    Fade,

    /// <summary>Slip or slide, over a clip.</summary>
    Slip,
}

/// <summary>Where one clip would end up, drawn over the timeline while a gesture is in progress.</summary>
/// <param name="TrackId">The track it would be on.</param>
/// <param name="Start">Where it would start.</param>
/// <param name="End">Where it would end.</param>
public readonly record struct GhostClip(string TrackId, Flicks Start, Flicks End);

/// <summary>The outcome a gesture would have if the mouse came up now.</summary>
/// <param name="Clips">Where things would be.</param>
/// <param name="Refused">Why it would be refused, or null when it would go through.</param>
public sealed record TimelineGhost(ImmutableArray<GhostClip> Clips, string? Refused);

/// <summary>The part of the timeline under a point.</summary>
/// <param name="Region">Which band it is in.</param>
/// <param name="Clip">The clip under it, if any.</param>
/// <param name="Edge">Which edge of that clip is near enough to trim, if any.</param>
/// <param name="MarkerId">The marker under it, in the marker lane.</param>
/// <param name="Row">The track row under it.</param>
/// <param name="Transition">The transition bar under it, if any; <paramref name="Edge"/> is then that bar's.</param>
public readonly record struct TimelineHit(
    TimelineRegion Region,
    ClipView? Clip = null,
    ClipEdge Edge = ClipEdge.None,
    string? MarkerId = null,
    TrackRow? Row = null,
    TransitionView? Transition = null);

/// <summary>The bands of the timeline, top to bottom.</summary>
public enum TimelineRegion
{
    /// <summary>The timecode ruler.</summary>
    Ruler,

    /// <summary>The marker lane under the ruler.</summary>
    Markers,

    /// <summary>A track, on a clip or not.</summary>
    Track,

    /// <summary>Below the last track.</summary>
    Empty,
}

/// <summary>The mouse side of the timeline: selecting, moving, trimming, scrubbing, dropping.</summary>
/// <remarks>
/// Kept free of WPF input types beyond a point and the modifier keys, so tests can drive every
/// gesture without a window. Previews are computed with the same <see cref="EditOps"/> the
/// commands use, so a ghost drawn red is a gesture the engine would refuse, and on mouse up the
/// whole gesture goes to the session as one <see cref="BatchCommand"/>: one undo step however many
/// clips moved.
/// </remarks>
public sealed partial class TimelineViewModel
{
    /// <summary>How close to a clip's edge, in pixels, a press trims instead of moving.</summary>
    public const double EdgeZone = 6.0;

    /// <summary>How far the pointer has to travel before a press on a clip becomes a drag.</summary>
    public const double DragThreshold = 3.0;

    private Gesture _gesture;
    private Point _downAt;
    private ModifierKeys _modifiers;
    private ClipView? _grabbed;
    private ImmutableArray<string> _dragIds = [];
    private ImmutableArray<ClipView> _trimmed = [];
    private long _scrubFrame = -1;
    private ImmutableArray<ICommand> _pending = [];

    private enum Gesture
    {
        None,
        Pressed,
        Move,
        TrimStart,
        TrimEnd,
        Box,
        Scrub,
        Razor,
        Ripple,
        Roll,
        Slip,
        Slide,
        Stretch,
        Hand,
        TransitionEdge,
        Volume,
        Fade,
        Speed,
    }

    /// <summary>What a gesture in progress would do, or null.</summary>
    public TimelineGhost? Ghost
    {
        get;
        private set
        {
            bool had = field is not null;
            field = value;
            if (had != (value is not null))
            {
                OnPropertyChanged(nameof(ShowsEmptyHint));
            }
        }
    }

    /// <summary>True when the empty timeline's hint shows: no clips, and nothing being dragged on, whose ghost it would cover.</summary>
    public bool ShowsEmptyHint => IsEmpty && Ghost is null;

    /// <summary>The selection rectangle being dragged out, or null.</summary>
    public Rect? Box { get; private set; }

    /// <summary>What the pointer is over, for the cursor.</summary>
    public TimelineCursor Cursor { get; private set; }

    /// <summary>What is under a point.</summary>
    public TimelineHit HitAt(Point point)
    {
        if (point.Y < TimelineGeometry.RulerHeight)
        {
            return new TimelineHit(TimelineRegion.Ruler);
        }

        if (point.Y < TimelineGeometry.TracksTop)
        {
            return new TimelineHit(TimelineRegion.Markers, MarkerId: MarkerAt(point.X));
        }

        if (Geometry.RowAt(point.Y) is not { } row)
        {
            return new TimelineHit(TimelineRegion.Empty);
        }

        if (Content.Track(row.TrackId) is not { } track)
        {
            return new TimelineHit(TimelineRegion.Track, Row: row);
        }

        Flicks time = Geometry.TimeAt(point.X, snapToFrame: false);

        // A transition's bar sits over the bottom of the clips it joins and takes the pointer there.
        if (TransitionAt(track, row, point) is { } bar)
        {
            double barLeft = Geometry.XOf(bar.Start);
            double barRight = Geometry.XOf(bar.End);
            double barZone = Math.Min(EdgeZone, (barRight - barLeft) / 3.0);
            ClipEdge barEdge = point.X - barLeft <= barZone ? ClipEdge.Start
                : barRight - point.X <= barZone ? ClipEdge.End
                : ClipEdge.None;
            return new TimelineHit(TimelineRegion.Track, ClipAt(track, time), barEdge, Row: row, Transition: bar);
        }

        // Overlapping cues sit side by side: the one in the lane under the pointer.
        ClipView? clip = track.Lanes.Count > 1
            ? CueAt(track, time, track.Lanes.LaneAt(point.Y, Geometry.TopOf(row) + VolumeLine.ClipInset, Math.Max(1, row.Height - (VolumeLine.ClipInset * 2))))
            : ClipAt(track, time);

        if (clip is null)
        {
            return new TimelineHit(TimelineRegion.Track, Row: row);
        }

        double left = Geometry.XOf(clip.Start);
        double right = Geometry.XOf(clip.End);
        double zone = Math.Min(EdgeZone, (right - left) / 3.0);

        ClipEdge edge = point.X - left <= zone ? ClipEdge.Start
            : right - point.X <= zone ? ClipEdge.End
            : ClipEdge.None;

        return new TimelineHit(TimelineRegion.Track, clip, edge, Row: row);
    }

    /// <summary>A mouse button went down.</summary>
    public void PointerDown(Point point, ModifierKeys modifiers)
    {
        _downAt = point;
        _modifiers = modifiers;
        _grabbed = null;
        _pending = [];
        _dragged = false;
        _bypassSnap = false;
        _snap = SnapService.None;
        SetGuide(null);

        if (Tools.Tool == TimelineTool.Hand)
        {
            _gesture = Gesture.Hand;
            _handScroll = Geometry.Scroll;
            _handOffset = Geometry.VerticalOffset;
            return;
        }

        TimelineHit hit = HitAt(point);

        if (hit.Region == TimelineRegion.Ruler || (hit.Region != TimelineRegion.Markers && NearPlayhead(point.X)))
        {
            _gesture = Gesture.Scrub;
            _scrubFrame = -1;
            Scrub(point.X);
            return;
        }

        if (hit.Region == TimelineRegion.Markers)
        {
            _gesture = Gesture.None;
            if (hit.MarkerId is { } marker)
            {
                Select([marker], ModeFor(modifiers));
            }

            return;
        }

        if (hit.Transition is { } bar && Tools.Tool is TimelineTool.Select)
        {
            TransitionDown(hit, bar, modifiers);
            return;
        }

        if (FadeDown(hit, point))
        {
            return;
        }

        if (SpeedDown(hit, point, modifiers))
        {
            return;
        }

        if (VolumeDown(hit, point, modifiers))
        {
            return;
        }

        if (hit.Clip is { } clip)
        {
            _grabbed = clip;
            SetSelectedEdit(null);

            if (ToolDown(hit, clip, modifiers))
            {
                return;
            }

            if (hit.Edge != ClipEdge.None)
            {
                _gesture = hit.Edge == ClipEdge.Start ? Gesture.TrimStart : Gesture.TrimEnd;
                _trimmed = [.. SameEdge(clip, hit.Edge)];
                _snap = Snapper(_trimmed.Select(view => view.Id));
                return;
            }

            string[] companions = [.. Content.Companions(clip.Id).Select(view => view.Id)];

            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                Select(companions, SelectMode.Toggle);
                _gesture = Gesture.None;
                return;
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                Select(companions, SelectMode.Add);
                _dragIds = [.. InSelectedOrder().Union(companions, StringComparer.Ordinal)];
            }
            else if (Selected.Contains(clip.Id))
            {
                // A press on what is already selected drags all of it; only a click without a
                // drag narrows the selection to this clip, on the way up.
                _dragIds = [.. InSelectedOrder().Where(id => Content.Clip(id) is not null)];
            }
            else
            {
                Select(companions, SelectMode.Replace);
                _dragIds = [.. companions];
            }

            _gesture = Gesture.Pressed;
            _snap = Snapper(_dragIds);
            return;
        }

        SetSelectedEdit(null);
        _gesture = Gesture.Box;
        Box = new Rect(point, point);
        Invalidate(TimelineLayers.Selection);
    }

    /// <summary>The pointer moved, with or without a button down.</summary>
    /// <param name="point">Where it is.</param>
    /// <param name="modifiers">The keys held: Ctrl holds snapping off.</param>
    public void PointerMove(Point point, ModifierKeys modifiers = ModifierKeys.None)
    {
        _bypassSnap = modifiers.HasFlag(ModifierKeys.Control);

        switch (_gesture)
        {
            case Gesture.None:
                TimelineHit hover = HitAt(point);
                SetCursor(CursorFor(hover, point));

                // The razor shows where it would cut.
                if (Tools.Tool == TimelineTool.Razor && hover.Clip is not null)
                {
                    _snap = Snapper(null);
                    SetGuide(Snapped(Geometry.TimeAt(point.X)));
                }
                else
                {
                    SetGuide(null);
                }

                return;

            case Gesture.Razor:
            case Gesture.Ripple:
            case Gesture.Roll:
            case Gesture.Slip:
            case Gesture.Slide:
            case Gesture.Stretch:
            case Gesture.Hand:
                ToolMove(point);
                return;

            case Gesture.Scrub:
                Scrub(point.X);
                return;

            case Gesture.Pressed:
                if (Math.Abs(point.X - _downAt.X) < DragThreshold && Math.Abs(point.Y - _downAt.Y) < DragThreshold)
                {
                    return;
                }

                _gesture = Gesture.Move;
                SetCursor(TimelineCursor.Move);
                PreviewMove(point);
                return;

            case Gesture.Move:
                PreviewMove(point);
                return;

            case Gesture.TransitionEdge:
                PreviewTransitionEdge(point);
                return;

            case Gesture.Speed:
                SpeedMove(point);
                return;

            case Gesture.Volume:
                VolumeMove(point);
                return;

            case Gesture.Fade:
                FadeMove(point);
                return;

            case Gesture.TrimStart:
            case Gesture.TrimEnd:
                PreviewTrim(point);
                return;

            case Gesture.Box:
                Box = new Rect(_downAt, point);
                Invalidate(TimelineLayers.Selection);
                return;
        }
    }

    /// <summary>The button came up: the gesture becomes a command, or a selection.</summary>
    public void PointerUp(Point point)
    {
        Gesture gesture = _gesture;
        _gesture = Gesture.None;

        switch (gesture)
        {
            case Gesture.Pressed when _grabbed is { } clip && _modifiers == ModifierKeys.None:
                // A click on a selected clip without a drag: just this one and its companions.
                Select([.. Content.Companions(clip.Id).Select(view => view.Id)], SelectMode.Replace);
                break;

            case Gesture.Move:
            case Gesture.TrimStart:
            case Gesture.TrimEnd:
                if (gesture == Gesture.Move)
                {
                    PreviewMove(point);
                }
                else
                {
                    PreviewTrim(point);
                }

                Commit();
                break;

            case Gesture.Box:
                FinishBox(point);
                break;

            case Gesture.TransitionEdge:
                PreviewTransitionEdge(point);
                CommitTransitionEdge();
                break;

            case Gesture.Speed:
                SpeedUp(point);
                break;

            case Gesture.Volume:
                VolumeUp(point);
                break;

            case Gesture.Fade:
                FadeUp(point);
                break;

            case Gesture.Razor:
            case Gesture.Ripple:
            case Gesture.Roll:
            case Gesture.Slip:
            case Gesture.Slide:
            case Gesture.Stretch:
            case Gesture.Hand:
                ToolUp(gesture, point);
                break;
        }

        ClearGesture();
        SetCursor(CursorFor(HitAt(point), point));
    }

    /// <summary>The gesture was abandoned: Escape, or the mouse was captured away.</summary>
    public void PointerCancel()
    {
        _gesture = Gesture.None;
        ClearGesture();
    }

    /// <summary>
    /// The wheel: scroll across, with Shift scroll down, with Ctrl zoom about the pointer.
    /// </summary>
    /// <param name="delta">Wheel units: 120 a notch, positive away from the user.</param>
    /// <param name="point">Where the pointer is.</param>
    /// <param name="modifiers">The keys held.</param>
    public void Wheel(double delta, Point point, ModifierKeys modifiers)
    {
        double notches = delta / 120.0;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            SetGeometry(Geometry.ZoomAbout(point.X, Math.Pow(1.25, notches)));
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            VerticalOffset -= notches * 40.0;
        }
        else
        {
            SetGeometry(Geometry.ScrollBy(-notches * ViewportWidth / 8.0));
        }
    }

    /// <summary>Media is being dragged over the timeline: show where it would land.</summary>
    /// <returns>True when it can be dropped here.</returns>
    public bool DragOver(IReadOnlyList<string> mediaIds, Point point, bool control = false)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);

        ImmutableArray<AddClipCommand> adds = Placements(mediaIds, point, out string? refused);
        Ghost = new TimelineGhost(
            [.. adds.Select(add => new GhostClip(add.TrackId, add.At, add.At + (add.Duration ?? Flicks.Zero)))],
            refused);
        Invalidate(TimelineLayers.Ghost);
        if (refused is null && !adds.IsEmpty)
        {
            Status = Inserts(control)
                ? $"Drop to insert, pushing what follows along; {(Magnetic ? "release Ctrl" : "without Ctrl")} to overwrite."
                : $"Drop to overwrite what is there; {(Magnetic ? "without Ctrl" : "hold Ctrl")} to insert, pushing what follows along.";
        }

        return refused is null && !adds.IsEmpty;
    }

    /// <summary>
    /// Whether a drop inserts (pushing what follows along) rather than overwriting: Ctrl turns it
    /// round, and a magnetic timeline inserts by default, as its moves do.
    /// </summary>
    private bool Inserts(bool control) => control != Magnetic;

    /// <summary>The drag left without dropping.</summary>
    public void DragLeave() => ClearGesture();

    /// <summary>
    /// An effect or a preset is being dragged over the timeline: says on the status line what it
    /// would go on.
    /// </summary>
    /// <returns>True when it can be dropped here.</returns>
    public bool EffectDragOver(string? typeId, string? presetId, Point point)
    {
        if (TransitionType(typeId) is { } transition)
        {
            (ICommand? onCut, string where) = TransitionDrop(transition, HitAt(point), point);
            Status = where;
            return onCut is not null;
        }

        (string? ownerId, string message) = EffectTarget(typeId, presetId, point);
        Status = message;
        return ownerId is not null;
    }

    /// <summary>
    /// An effect or a preset was dropped: it goes on the clip under the pointer, or on the track
    /// when there is no clip there. A generator becomes a clip on the track, at the pointer.
    /// </summary>
    public async Task DropEffectAsync(string? typeId, string? presetId, Point point)
    {
        if (TransitionType(typeId) is { } transition)
        {
            (ICommand? onCut, string where) = TransitionDrop(transition, HitAt(point), point);
            if (onCut is null)
            {
                Status = where;
                return;
            }

            if (await RunAsync(onCut).ConfigureAwait(true))
            {
                ReportHolds(onCut);
            }

            return;
        }

        (string? ownerId, string message) = EffectTarget(typeId, presetId, point);
        if (ownerId is null)
        {
            Status = message;
            return;
        }

        bool generator = typeId is not null && Engine.Effects.EffectCatalog.Registry.Find(typeId) is { Kind: Core.Effects.EffectKind.Generator or Core.Effects.EffectKind.AudioGenerator };
        ICommand command = generator && typeId == Core.Titles.TitleParams.GeneratorId
            ? new AddTitleCommand(Geometry.TimeAt(point.X), TrackId: ownerId)
            : generator
            ? new AddClipCommand(ownerId, Geometry.TimeAt(point.X), GeneratorId: typeId, Name: Engine.Effects.EffectCatalog.Registry.Find(typeId!)!.Name)
            : typeId is not null && typeId.StartsWith(Effects.EffectsPanelViewModel.PluginPrefix, StringComparison.Ordinal)
                ? new AddPluginCommand(ownerId, typeId[Effects.EffectsPanelViewModel.PluginPrefix.Length..])
            : typeId is not null
                ? new AddEffectCommand(ownerId, typeId)
                : new ApplyEffectPresetCommand(ownerId, presetId!);

        await RunAsync(command).ConfigureAwait(true);
    }

    /// <summary>A title preset is over the timeline: says where it would go, and whether it can.</summary>
    public bool TitleDragOver(string preset, Point point)
    {
        (string? trackId, string message) = TitleTarget(preset, point);
        Status = message;
        return trackId is not null;
    }

    /// <summary>
    /// A title preset was dropped: a new title from it on the video track under the pointer, at
    /// the pointer, exactly as <c>jazz title add --preset --at --track</c> makes one.
    /// </summary>
    public async Task DropTitleAsync(string preset, Point point)
    {
        (string? trackId, string message) = TitleTarget(preset, point);
        if (trackId is null)
        {
            Status = message;
            return;
        }

        await RunAsync(new AddTitleCommand(Geometry.TimeAt(point.X), Preset: preset, TrackId: trackId)).ConfigureAwait(true);
    }

    /// <summary>The video track a title dropped at a point goes on, or why it cannot.</summary>
    private (string? TrackId, string Message) TitleTarget(string preset, Point point)
    {
        TimelineHit hit = HitAt(point);
        if (hit.Region != TimelineRegion.Track || hit.Row is not { Kind: TrackKind.Video } row)
        {
            return (null, "Drop a title on a video track.");
        }

        string track = Content.Track(row.TrackId)?.Track.Name ?? string.Empty;
        return hit.Clip is { } there
            ? (null, $"'{there.Clip.Name}' is there: drop the title where {track} is empty.")
            : (row.TrackId, $"Add a {preset} title to {track} here.");
    }

    /// <summary>The transition type being dragged, or null for anything else.</summary>
    private static Core.Effects.EffectDescriptor? TransitionType(string? typeId) =>
        typeId is not null && Engine.Effects.EffectCatalog.Registry.Find(typeId) is { Kind: Core.Effects.EffectKind.Transition or Core.Effects.EffectKind.AudioTransition } descriptor ? descriptor : null;

    /// <summary>What an effect dropped at a point would go on, or why it cannot.</summary>
    private (string? OwnerId, string Message) EffectTarget(string? typeId, string? presetId, Point point)
    {
        TimelineHit hit = HitAt(point);
        if (hit.Region != TimelineRegion.Track || hit.Row is not { } row)
        {
            return (null, "Drop an effect on a clip, or on a track for the whole track.");
        }

        string ownerId = hit.Clip?.Id ?? row.TrackId;
        string what = hit.Clip is { } clip ? $"clip '{clip.Clip.Name}'" : $"track {Content.Track(row.TrackId)?.Track.Name ?? string.Empty}";

        if (presetId is not null)
        {
            return (ownerId, $"Apply the preset to {what}.");
        }

        // A plugin found on this computer (Phase 46): sound only.
        if (typeId is not null && typeId.StartsWith(Effects.EffectsPanelViewModel.PluginPrefix, StringComparison.Ordinal))
        {
            return row.Kind == TrackKind.Audio
                ? (ownerId, $"Add the plugin to {what}.")
                : (null, $"Plugins work on sound, and {what} carries a picture.");
        }

        if (typeId is null || Engine.Effects.EffectCatalog.Registry.Find(typeId) is not { } descriptor)
        {
            return (null, "That is not an effect this editor has.");
        }

        // A generator is a clip of its own: it goes on the video track under the pointer, at the
        // pointer, where that track is empty (the command refuses an overlap, so the hint does too).
        if (descriptor.Kind is Core.Effects.EffectKind.Generator or Core.Effects.EffectKind.AudioGenerator)
        {
            string track = Content.Track(row.TrackId)?.Track.Name ?? string.Empty;
            bool sound = descriptor.Kind == Core.Effects.EffectKind.AudioGenerator;
            return row.Kind != (sound ? TrackKind.Audio : TrackKind.Video)
                ? (null, sound ? $"{descriptor.Name} makes sound of its own; drop it on a sound track." : $"{descriptor.Name} makes a picture of its own; drop it on a video track.")
                : hit.Clip is { } there
                ? (null, $"'{there.Clip.Name}' is there: drop {descriptor.Name} where {track} is empty.")
                : (row.TrackId, $"Add a {descriptor.Name} clip to {track} here.");
        }

        bool picture = row.Kind is TrackKind.Video or TrackKind.Adjustment;
        bool suits = descriptor.Kind == Core.Effects.EffectKind.Audio ? row.Kind == TrackKind.Audio : descriptor.Kind == Core.Effects.EffectKind.Video && picture;
        return suits
            ? (ownerId, $"Add {descriptor.Name} to {what}.")
            : (null, $"{descriptor.Name} works on {(descriptor.Kind == Core.Effects.EffectKind.Audio ? "sound" : "pictures")}, and {what} carries {(picture ? "a picture" : "sound")}.");
    }

    /// <summary>The stretch a drag from the source monitor carries, while it is over the timeline (Phase 38).</summary>
    internal (string MediaId, Flicks In, Flicks Out)? DraggedRange { get; set; }

    /// <summary>
    /// Media was dropped: each item goes on the track under the pointer, end to end from the
    /// pointer's time, a movie bringing its sound onto audio tracks linked to the picture. It
    /// overwrites what it lands on (over empty space that is simply adding it); with
    /// <paramref name="control"/> held it inserts, pushing what follows along. A magnetic timeline
    /// has these the other way round.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> mediaIds, Point point, bool control = false)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);

        ImmutableArray<AddClipCommand> adds = Placements(mediaIds, point, out string? refused);
        ClearGesture();

        if (refused is not null)
        {
            Status = refused;
            return;
        }

        if (!adds.IsEmpty)
        {
            bool insert = Inserts(control);
            ICommand[] edits = [.. adds.Select(add => insert
                ? (ICommand)new InsertClipCommand(add.TrackId, add.At, add.MediaId, add.GeneratorId, add.SequenceId, add.SourceIn, add.Duration, add.Name, add.SourceStreamIndex, add.ClipId, add.WithAudio)
                : new OverwriteClipCommand(add.TrackId, add.At, add.MediaId, add.GeneratorId, add.SequenceId, add.SourceIn, add.Duration, add.Name, add.SourceStreamIndex, add.ClipId, add.WithAudio))];
            string verb = insert ? "Insert" : "Overwrite with";
            await RunAsync(new BatchCommand([.. edits], adds.Length == 1 ? $"{verb} clip" : $"{verb} {adds.Length} clips")).ConfigureAwait(true);
        }
    }

    /// <summary>Forgets any gesture's ghost and box.</summary>
    internal void ClearGesture()
    {
        bool had = Ghost is not null || Box is not null || Guide is not null;
        Ghost = null;
        Box = null;
        Guide = null;
        _pending = [];

        if (had)
        {
            Invalidate(TimelineLayers.Ghost | TimelineLayers.Selection);
        }
    }

    private static SelectMode ModeFor(ModifierKeys modifiers) =>
        modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Toggle
        : modifiers.HasFlag(ModifierKeys.Shift) ? SelectMode.Add
        : SelectMode.Replace;

    /// <summary>The cue showing at a time in one lane of a subtitle track whose cues overlap.</summary>
    private static ClipView? CueAt(TrackView track, Flicks time, int lane) =>
        track.Clips.FirstOrDefault(clip => clip.Start <= time && time < clip.End && track.Lanes.LaneOf(clip.Clip.Id) == lane);

    private static ClipView? ClipAt(TrackView track, Flicks time)
    {
        ImmutableArray<ClipView> clips = track.Clips;
        int low = 0;
        int high = clips.Length - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            ClipView clip = clips[middle];

            if (time < clip.Start)
            {
                high = middle - 1;
            }
            else if (time >= clip.End)
            {
                low = middle + 1;
            }
            else
            {
                return clip;
            }
        }

        return null;
    }

    private TimelineCursor CursorFor(TimelineHit hit, Point point) => (Tools.Tool, hit) switch
    {
        (TimelineTool.Hand, _) => TimelineCursor.Hand,
        (_, { Region: TimelineRegion.Ruler }) => TimelineCursor.Scrub,
        (TimelineTool.Select, { Transition: { } bar }) => Movable(bar, hit.Edge) ? hit.Edge == ClipEdge.Start ? TimelineCursor.TrimStart : TimelineCursor.TrimEnd : TimelineCursor.Arrow,
        (TimelineTool.Select, { Clip: not null }) when FadeAt(hit, point) is not null => TimelineCursor.Fade,
        (TimelineTool.Select, { Clip: not null }) when SpeedAt(hit, point) is not null => TimelineCursor.Volume,
        (TimelineTool.Select, { Clip: not null }) when VolumeAt(hit, point) is not null => TimelineCursor.Volume,
        (TimelineTool.Razor, { Clip: not null }) => TimelineCursor.Razor,
        (TimelineTool.Slip or TimelineTool.Slide, { Clip: not null }) => TimelineCursor.Slip,
        (TimelineTool.RateStretch, { Edge: ClipEdge.Start }) => TimelineCursor.Arrow,
        (_, { Edge: ClipEdge.Start }) => TimelineCursor.TrimStart,
        (_, { Edge: ClipEdge.End }) => TimelineCursor.TrimEnd,
        (_, { Region: TimelineRegion.Track or TimelineRegion.Empty }) when NearPlayhead(point.X) => TimelineCursor.Scrub,
        _ => TimelineCursor.Arrow,
    };

    private void SetCursor(TimelineCursor cursor)
    {
        if (cursor != Cursor)
        {
            Cursor = cursor;
            OnPropertyChanged(nameof(Cursor));
        }
    }

    private bool NearPlayhead(double x) => Math.Abs(x - Geometry.XOf(Playhead)) <= 4.0;

    private string? MarkerAt(double x)
    {
        foreach (Marker marker in Content.Sequence.Markers)
        {
            if (Math.Abs(Geometry.XOf(marker.Time) - x) <= 5.0)
            {
                return marker.Id;
            }
        }

        return null;
    }

    private void Select(IReadOnlyCollection<string> ids, SelectMode mode)
    {
        if (ids.Count == 0 && mode != SelectMode.Replace)
        {
            return;
        }

        ICommand command = ids.Count == 0
            ? new ClearSelectionCommand()
            : new SetSelectionCommand(new EquatableArray<string>([.. ids]), mode);

        _ = RunAsync(command);
    }

    private void Scrub(double x)
    {
        Flicks time = Geometry.TimeAt(x);
        long frame = time.ToFrames(Geometry.FrameRate, RoundingMode.Floor);

        if (frame == _scrubFrame)
        {
            return;
        }

        _scrubFrame = frame;
        _playhead = time;
        OnPropertyChanged(nameof(Timecode));
        Invalidate(TimelineLayers.Playhead);
        _ = RunAsync(new SeekCommand(time));
    }

    private IEnumerable<ClipView> SameEdge(ClipView clip, ClipEdge edge)
    {
        // A camera clip and its sound share their edges: trimming one trims the others that
        // start (or end) at the same frame, and leaves alone a companion that was cut apart.
        foreach (ClipView companion in Content.Companions(clip.Id))
        {
            bool shared = edge == ClipEdge.Start ? companion.Start == clip.Start : companion.End == clip.End;
            if (shared && (companion.Clip.LinkGroupId is not null || companion.Id == clip.Id))
            {
                yield return companion;
            }
        }
    }

    private void PreviewMove(Point point)
    {
        if (_grabbed is not { } grabbed || _dragIds.IsEmpty)
        {
            return;
        }

        ClipView[] moving = [.. _dragIds.Select(Content.Clip).OfType<ClipView>()];
        Flicks delta = Geometry.TimeAt(point.X) - Geometry.TimeAt(_downAt.X);
        Flicks earliest = moving.Min(clip => clip.Start);

        // Whichever edge of the group comes nearest something snaps it there.
        if (!_bypassSnap && _snap.FindFor(moving.SelectMany(clip => new[] { clip.Start + delta, clip.End + delta }), Geometry) is { } snap)
        {
            delta += snap.Correction;
            SetGuide(snap.Target.Time);
        }
        else
        {
            SetGuide(null);
        }

        if ((earliest + delta).IsNegative)
        {
            delta = Flicks.Zero - earliest;
        }

        int shift = RowShift(grabbed, point, moving);

        // On a magnetic sequence a clip dragged along the primary track goes in at a cut.
        if (shift == 0 && Content.Sequence.IsMagnetic && Content.Sequence.PrimaryTrack is { Locked: false } primary
            && string.Equals(grabbed.TrackId, primary.Id, StringComparison.Ordinal))
        {
            PreviewStoryline(moving, primary, delta);
            return;
        }

        var moves = new List<(ClipView Clip, string TrackId, Flicks To)>(moving.Length);
        foreach (ClipView clip in moving)
        {
            moves.Add((clip, ShiftedTrack(clip, shift), clip.Start + delta));
        }

        // Front first in the direction of travel, as clip.nudge does, so a clip never lands on a
        // neighbour that is about to move out of the way.
        moves.Sort((a, b) => delta.Value >= 0 ? b.Clip.Start.CompareTo(a.Clip.Start) : a.Clip.Start.CompareTo(b.Clip.Start));

        Sequence sequence = Content.Sequence;
        string? refused = null;
        var commands = new List<ICommand>(moves.Count);

        foreach ((ClipView clip, string trackId, Flicks to) in moves)
        {
            string? toTrack = string.Equals(trackId, clip.TrackId, StringComparison.Ordinal) ? null : trackId;
            if (to == clip.Start && toTrack is null)
            {
                continue;
            }

            EditResult<Sequence> result = EditOps.Move(sequence, clip.Id, toTrack, to);
            if (result.IsOk)
            {
                sequence = result.Value;
            }
            else
            {
                refused ??= result.Error!.Message;
            }

            commands.Add(new MoveClipCommand(clip.Id, to, toTrack));
        }

        _pending = [.. commands];
        Ghost = new TimelineGhost(
            [.. moves.Select(move => new GhostClip(move.TrackId, move.To, move.To + move.Clip.Clip.Duration))],
            refused);
        Invalidate(TimelineLayers.Ghost);
    }

    private void PreviewStoryline(ClipView[] moving, Track primary, Flicks delta)
    {
        Flicks to = moving.Where(clip => clip.TrackId == primary.Id).Min(clip => clip.Start) + delta;
        string[] ids = [.. moving.Select(clip => clip.Id)];

        _pending = [new StorylineMoveClipsCommand([.. ids], to)];
        ShowResult(EditOps.MoveOnStoryline(Content.Sequence, ids, to), ids);
    }

    /// <summary>
    /// How many tracks the grabbed clip has moved, counted the way tracks are numbered: picture
    /// tracks up from V1, sound tracks down from A1. Every moving clip moves that many tracks
    /// within its own kind, so dragging a camera clip from V1 to V2 takes its sound from A1 to A2.
    /// When any of them would run out of tracks, none change track.
    /// </summary>
    private int RowShift(ClipView grabbed, Point point, ClipView[] moving)
    {
        if (Geometry.RowAt(point.Y) is not { } target || !SameFamily(target.Kind, grabbed.Kind))
        {
            return 0;
        }

        int shift = TrackNumber(target.TrackId) - TrackNumber(grabbed.TrackId);
        if (shift == 0)
        {
            return 0;
        }

        foreach (ClipView clip in moving)
        {
            int number = TrackNumber(clip.TrackId) + shift;
            if (number < 0 || number >= Family(clip.Kind).Count)
            {
                return 0;
            }
        }

        return shift;
    }

    private static bool SameFamily(TrackKind a, TrackKind b) => a == b || (IsPicture(a) && IsPicture(b));

    private static bool IsPicture(TrackKind kind) => kind is TrackKind.Video or TrackKind.Adjustment;

    /// <summary>The tracks of a kind, numbered from zero: V1, V2 upwards, or A1, A2 downwards.</summary>
    private List<Track> Family(TrackKind kind) =>
        [.. Content.Sequence.Tracks.Where(track => SameFamily(track.Kind, kind)).OrderBy(track => track.Order)];

    private int TrackNumber(string trackId)
    {
        if (Content.Track(trackId) is not { } track)
        {
            return -1;
        }

        return Family(track.Track.Kind).FindIndex(candidate => string.Equals(candidate.Id, trackId, StringComparison.Ordinal));
    }

    private string ShiftedTrack(ClipView clip, int shift) =>
        shift == 0 ? clip.TrackId : Family(clip.Kind)[TrackNumber(clip.TrackId) + shift].Id;

    private void PreviewTrim(Point point)
    {
        if (_trimmed.IsEmpty)
        {
            return;
        }

        Flicks to = Snapped(Geometry.TimeAt(point.X));
        bool start = _gesture == Gesture.TrimStart;
        Sequence sequence = Content.Sequence;
        string? refused = null;
        var ghosts = new List<GhostClip>(_trimmed.Length);
        var commands = new List<ICommand>(_trimmed.Length);

        foreach (ClipView clip in _trimmed)
        {
            if (sequence.Track(clip.TrackId) is not { } track)
            {
                continue;
            }

            EditResult<Track> result = start
                ? EditOps.TrimIn(track, clip.Id, to)
                : EditOps.TrimOut(track, clip.Id, to, sourceDuration: SourceDuration(clip));

            if (result.IsOk)
            {
                sequence = sequence.ReplaceTrack(result.Value);
            }
            else
            {
                refused ??= result.Error!.Message;
            }

            ghosts.Add(start ? new GhostClip(clip.TrackId, to, clip.End) : new GhostClip(clip.TrackId, clip.Start, to));
            commands.Add(start ? new TrimClipCommand(clip.Id, In: to) : new TrimClipCommand(clip.Id, Out: to));
        }

        _pending = [.. commands];
        Ghost = new TimelineGhost([.. ghosts], refused);
        Invalidate(TimelineLayers.Ghost);
    }

    private Flicks? SourceDuration(ClipView clip) =>
        clip.Clip.MediaId is { } mediaId ? _session.Project.MediaItem(mediaId)?.Duration : null;

    private void Commit()
    {
        if (Ghost?.Refused is { } refused)
        {
            Status = refused;
            return;
        }

        if (_pending.IsEmpty)
        {
            return;
        }

        string label = _pending[0] switch
        {
            TrimClipCommand => "Trim",
            RippleTrimClipsCommand => "Ripple trim",
            RollClipsCommand => "Roll",
            SlipClipCommand => "Slip",
            SlideClipCommand => "Slide",
            RateStretchClipCommand => "Rate stretch",
            SplitClipCommand => "Cut",
            StorylineMoveClipsCommand => "Move along the storyline",
            _ => _pending.Length == 1 ? "Move clip" : $"Move {_pending.Length} clips",
        };

        _ = RunAsync(new BatchCommand([.. _pending], label));
    }

    private void FinishBox(Point point)
    {
        var box = new Rect(_downAt, point);

        if (box.Width < DragThreshold && box.Height < DragThreshold)
        {
            // A click on an empty lane: clear, unless a modifier said to keep what there is.
            if (_modifiers == ModifierKeys.None)
            {
                Select([], SelectMode.Replace);
            }

            return;
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (TrackView track in Content.Tracks)
        {
            if (Geometry.Row(track.Id) is not { } row)
            {
                continue;
            }

            double top = Geometry.TopOf(row);
            if (top > box.Bottom || top + row.Height < box.Top)
            {
                continue;
            }

            foreach (ClipView clip in track.Clips)
            {
                double left = Geometry.XOf(clip.Start);
                double right = Geometry.XOf(clip.End);

                if (right < box.Left || left > box.Right || !seen.Add(clip.Id))
                {
                    continue;
                }

                foreach (ClipView companion in Content.Companions(clip.Id))
                {
                    if (seen.Add(companion.Id) || companion.Id == clip.Id)
                    {
                        ids.Add(companion.Id);
                    }
                }
            }
        }

        SelectMode mode = _modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Toggle
            : _modifiers.HasFlag(ModifierKeys.Shift) ? SelectMode.Add
            : SelectMode.Replace;

        Select(ids, mode);
    }

    /// <summary>Works out where dropped media would go, and why it cannot when it cannot.</summary>
    private ImmutableArray<AddClipCommand> Placements(IReadOnlyList<string> mediaIds, Point point, out string? refused)
    {
        refused = null;
        Project project = _session.Project;
        Flicks at = Geometry.TimeAt(point.X);
        TrackRow? under = Geometry.RowAt(point.Y);
        var adds = ImmutableArray.CreateBuilder<AddClipCommand>(mediaIds.Count);

        foreach (string mediaId in mediaIds)
        {
            if (project.MediaItem(mediaId) is not { } item)
            {
                continue;
            }

            bool picture = HasPicture(item);
            TrackKind wanted = picture ? TrackKind.Video : TrackKind.Audio;

            string? trackId = under is { } row && (row.Kind == wanted || (picture && IsPicture(row.Kind)))
                ? row.TrackId
                : FirstTrack(wanted);

            if (trackId is null)
            {
                refused = picture
                    ? "There is no video track to put the picture on. Add one first."
                    : $"'{item.Name}' is sound only, and there is no audio track to put it on. Add one first.";
                return [];
            }

            if (Content.Track(trackId)?.Track.Locked == true)
            {
                refused = $"{Content.Track(trackId)!.Track.Name} is locked.";
                return [];
            }

            // A stretch marked in the source monitor, or the item's own range (a subclip's).
            (Flicks from, Flicks length) = DraggedRange is { } range && range.MediaId == mediaId
                ? (range.In, range.Out - range.In)
                : (item.DefaultIn, item.DefaultOut - item.DefaultIn);
            adds.Add(new AddClipCommand(trackId, at, MediaId: mediaId, SourceIn: from, Duration: length));
            at += length;
        }

        return SnappedDrop(adds.ToImmutable());
    }

    /// <summary>
    /// Snaps what is being dropped as a clip move snaps: whichever edge of the run comes nearest
    /// something takes it there, and the guide is drawn.
    /// </summary>
    private ImmutableArray<AddClipCommand> SnappedDrop(ImmutableArray<AddClipCommand> adds)
    {
        if (adds.IsEmpty
            || Snapper(null).FindFor(adds.SelectMany(add => new[] { add.At, add.At + (add.Duration ?? Flicks.Zero) }), Geometry) is not { } snap)
        {
            SetGuide(null);
            return adds;
        }

        Flicks correction = snap.Correction;
        Flicks earliest = adds.Min(add => add.At);
        if ((earliest + correction).IsNegative)
        {
            correction = Flicks.Zero - earliest;
        }

        SetGuide(snap.Target.Time);
        return [.. adds.Select(add => add with { At = add.At + correction })];
    }

    private static bool HasPicture(MediaItem item) =>
        item.Kind != MediaKind.Movie
        || item.Info is null
        || item.Info.Streams.Any(stream => stream.Kind == MediaStreamKind.Video);

    /// <summary>
    /// Where a drop outside any fitting track goes: the lowest picture track (V1), or the first
    /// sound track (A1).
    /// </summary>
    private string? FirstTrack(TrackKind kind)
    {
        TrackView? track = kind == TrackKind.Video
            ? Content.Tracks.Where(view => view.Track.Kind == TrackKind.Video).OrderBy(view => view.Track.Order).FirstOrDefault()
            : Content.Tracks.Where(view => view.Track.Kind == TrackKind.Audio).OrderBy(view => view.Track.Order).FirstOrDefault();

        return track?.Id;
    }
}
