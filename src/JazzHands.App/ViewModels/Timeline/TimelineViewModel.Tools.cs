using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>One cut a roll moves: the clip before it and the clip after it, on one track.</summary>
/// <param name="TrackId">The track.</param>
/// <param name="LeftId">The outgoing clip.</param>
/// <param name="RightId">The incoming clip.</param>
public readonly record struct RollPair(string TrackId, string LeftId, string RightId);

/// <summary>
/// An edit point picked with the ripple or roll tool, which the keyboard then trims: comma and
/// full stop a frame at a time, or a number typed after plus or minus.
/// </summary>
/// <param name="Tool">Ripple or roll.</param>
/// <param name="Edge">For a ripple, which edge of the clips.</param>
/// <param name="ClipIds">For a ripple, the clips sharing the edge.</param>
/// <param name="Rolls">For a roll, the cuts.</param>
/// <param name="Time">Where the edit is.</param>
public sealed record TimelineEdit(
    TimelineTool Tool,
    ClipEdge Edge,
    ImmutableArray<string> ClipIds,
    ImmutableArray<RollPair> Rolls,
    Flicks Time);

/// <summary>One entry in the timeline's right-click menu.</summary>
/// <param name="Header">What it says.</param>
/// <param name="Shortcut">The key that does the same, shown on the right.</param>
/// <param name="Run">What it does; null for a separator.</param>
/// <param name="Enabled">False to show it greyed out.</param>
public sealed record TimelineMenuItem(string Header, string? Shortcut, Func<Task>? Run, bool Enabled = true)
{
    /// <summary>A line between groups.</summary>
    public static TimelineMenuItem Separator { get; } = new(string.Empty, null, null);

    /// <summary>True for <see cref="Separator"/>.</summary>
    public bool IsSeparator => Run is null;
}

/// <summary>A step back up the nesting, shown over a compound clip's timeline.</summary>
/// <param name="SequenceId">The sequence.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsCurrent">True for the one being shown.</param>
public sealed record SequenceCrumb(string SequenceId, string Name, bool IsCurrent);

/// <summary>
/// The tools beyond select, snapping, trim edit mode, the clipboard, the right-click menu and
/// opening compound clips.
/// </summary>
public sealed partial class TimelineViewModel
{
    private SnapService _snap = SnapService.None;
    private bool _bypassSnap;
    private bool _dragged;
    private bool _razorAll;
    private ClipEdge _edge;
    private Flicks _handScroll;
    private double _handOffset;
    private Flicks _cutTime;
    private ImmutableArray<RollPair> _rolls = [];
    private string? _entry;

    /// <summary>The tool in hand and whether snapping is on, shared by every timeline.</summary>
    public TimelineTools Tools { get; }

    /// <summary>Where a snap line or the razor's cut line is drawn, or null.</summary>
    public Flicks? Guide { get; private set; }

    /// <summary>The edit point the keyboard trims, or null.</summary>
    public TimelineEdit? SelectedEdit { get; private set; }

    /// <summary>The way back up from a compound clip's timeline; empty when this was not opened from one.</summary>
    public ImmutableArray<SequenceCrumb> Trail { get; private set; } = [];

    /// <summary>True when there is a way back up to show.</summary>
    public bool HasTrail => Trail.Length > 1;

    /// <summary>True when this sequence keeps its primary picture track gapless.</summary>
    public bool Magnetic => Content.Sequence.IsMagnetic;

    /// <summary>Where copies go and come from; the Windows clipboard in the app.</summary>
    public IClipboardService? Clipboard { get; set; }

    /// <summary>Opens another sequence's tab, from a compound clip: set by the tabs.</summary>
    public Action<string, string?>? OpenSequence { get; set; }

    /// <summary>Where thumbnails and waveforms come from: the engine's caches in the app, nothing in a test.</summary>
    public Controls.Timeline.ITimelineImagery Imagery { get; set; } = Controls.Timeline.NoImagery.Instance;

    /// <summary>
    /// A key the timeline wants before the keymap: typing a number to nudge or trim, and the
    /// keys that trim an edit point picked with the ripple or roll tool.
    /// </summary>
    /// <returns>True when the key was used.</returns>
    public bool KeyDown(Key key, ModifierKeys modifiers)
    {
        if (_entry is not null)
        {
            return EntryKey(key);
        }

        bool plus = key == Key.Add || (key == Key.OemPlus && modifiers == ModifierKeys.Shift);
        bool minus = key == Key.Subtract || (key == Key.OemMinus && modifiers == ModifierKeys.None);

        if ((plus || minus) && (SelectedEdit is not null || SelectedClipIds().Length > 0))
        {
            _entry = plus ? "+" : "-";
            ShowEntry();
            return true;
        }

        if (SelectedEdit is null)
        {
            return false;
        }

        switch (key)
        {
            case Key.OemComma:
                _ = TrimEditAsync(modifiers == ModifierKeys.Shift ? -10 : -1);
                return true;

            case Key.OemPeriod:
                _ = TrimEditAsync(modifiers == ModifierKeys.Shift ? 10 : 1);
                return true;

            case Key.Escape:
                SetSelectedEdit(null);
                return true;

            default:
                return false;
        }
    }

    /// <summary>A keymap action that belongs to the timeline rather than the engine.</summary>
    /// <returns>True when it was one of the timeline's.</returns>
    public bool Invoke(string action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (TimelineTools.FromAction(action) is { } tool)
        {
            Tools.Tool = tool;
            SetSelectedEdit(null);
            Status = TimelineTools.Describe(tool);
            return true;
        }

        switch (action)
        {
            case "ui.snap":
                Tools.Snapping = !Tools.Snapping;
                Status = Tools.Snapping ? "Snapping on" : "Snapping off";
                return true;

            case "ui.copy":
                Copy();
                return true;

            case "ui.cut":
                _ = CutAsync();
                return true;

            case "ui.paste":
                _ = PasteAsync(Playhead, null, insert: false);
                return true;

            case "ui.paste-insert":
                _ = PasteAsync(Playhead, null, insert: true);
                return true;

            case "ui.match-frame":
                MatchFrameAtPlayhead();
                return true;

            default:
                return false;
        }
    }

    /// <summary>A double click: a compound clip opens its sequence.</summary>
    public void DoubleClick(Point point)
    {
        if (HitAt(point).Clip is { Clip.SequenceId: { } nested })
        {
            PointerCancel();
            OpenSequence?.Invoke(nested, SequenceId);
        }
    }

    /// <summary>Goes back up to a sequence in the trail.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void GoTo(string? sequenceId)
    {
        if (sequenceId is not null && !string.Equals(sequenceId, SequenceId, StringComparison.Ordinal))
        {
            OpenSequence?.Invoke(sequenceId, null);
        }
    }

    /// <summary>Turns magnetic mode on or off for this sequence.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public Task ToggleMagneticAsync() => RunAsync(new SetTimelineMagneticCommand(!Magnetic, SequenceId));

    /// <summary>Turns snapping on or off.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void ToggleSnapping() => Invoke("ui.snap");

    /// <summary>Picks a tool.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void PickTool(TimelineTool tool) => Invoke(TimelineTools.All.First(entry => entry.Tool == tool).Action);

    /// <summary>The right-click menu for a point.</summary>
    public IReadOnlyList<TimelineMenuItem> MenuAt(Point point)
    {
        TimelineHit hit = HitAt(point);
        Flicks time = Geometry.TimeAt(point.X);
        bool canPaste = Clipboard?.GetClips() is not null;

        if (hit.Clip is not { } clip)
        {
            var items = new List<TimelineMenuItem>();
            if (hit.Row is { } row && Content.Track(row.TrackId) is { } track && track.Clips.Any(other => other.Start > time)
                && !track.Clips.Any(other => other.Start <= time && time < other.End))
            {
                items.Add(new TimelineMenuItem("Close gap", null, () => RunAsync(new CloseGapCommand(row.TrackId, time))));
                items.Add(TimelineMenuItem.Separator);
            }

            items.Add(new TimelineMenuItem("Paste here", "Ctrl+V", () => PasteAsync(time, hit.Row?.TrackId, insert: false), canPaste));
            items.Add(new TimelineMenuItem("Paste insert here", "Ctrl+Shift+V", () => PasteAsync(time, hit.Row?.TrackId, insert: true), canPaste));
            return items;
        }

        // A right click on something not selected selects it first, as every editor does.
        string[] ids = Selected.Contains(clip.Id)
            ? SelectedClipIds()
            : [.. Content.Companions(clip.Id).Select(view => view.Id)];

        if (!Selected.Contains(clip.Id))
        {
            Select(ids, SelectMode.Replace);
        }

        Flicks playhead = Playhead;
        string[] underPlayhead = [.. ids.Select(Content.Clip).OfType<ClipView>().Where(view => view.Start < playhead && playhead < view.End).Select(view => view.Id)];
        bool linked = ids.Select(Content.Clip).OfType<ClipView>().Any(view => view.Clip.LinkGroupId is not null);
        bool grouped = ids.Select(Content.Clip).OfType<ClipView>().Any(view => view.Clip.GroupId is not null);
        bool enabled = clip.Clip.Enabled;

        var menu = new List<TimelineMenuItem>
        {
            new("Cut", "Ctrl+X", CutAsync),
            new("Copy", "Ctrl+C", () =>
            {
                Copy();
                return Task.CompletedTask;
            }),
            new("Paste", "Ctrl+V", () => PasteAsync(playhead, null, insert: false), canPaste),
            TimelineMenuItem.Separator,
            new("Split at playhead", "Ctrl+K", () => RunAsync(Batch([.. underPlayhead.Select(id => (ICommand)new SplitClipCommand(id, playhead))], "Split")), underPlayhead.Length > 0),
            new("Freeze frame at playhead", null, () => RunAsync(new FreezeFrameCommand(clip.Id, playhead)), clip.Kind != TrackKind.Audio && clip.Start <= playhead && playhead < clip.End),
            new("Ripple delete", "Shift+Delete", () => RunAsync(new RippleDeleteClipsCommand([.. ids]))),
            new("Delete", "Delete", () => RunAsync(Batch([.. ids.Select(id => (ICommand)new RemoveClipCommand(id))], "Delete"))),
            TimelineMenuItem.Separator,
            linked
                ? new TimelineMenuItem("Unlink", null, () => RunAsync(new UnlinkClipsCommand([.. ids])))
                : new TimelineMenuItem("Link", null, () => RunAsync(new LinkClipsCommand([.. ids])), ids.Length > 1),
            grouped
                ? new TimelineMenuItem("Ungroup", null, () => RunAsync(new UngroupClipsCommand([.. ids])))
                : new TimelineMenuItem("Group", null, () => RunAsync(new GroupClipsCommand([.. ids])), ids.Length > 1),
            new("Nest", null, () => RunAsync(new NestClipsCommand([.. ids], NestName()))),
        };

        if (clip.Clip.SequenceId is { } nested)
        {
            menu.Add(new TimelineMenuItem("Open nested sequence", "Double-click", () =>
            {
                OpenSequence?.Invoke(nested, SequenceId);
                return Task.CompletedTask;
            }));
            menu.Add(new TimelineMenuItem("Unnest", null, () => RunAsync(new UnnestClipCommand(clip.Id))));
        }

        menu.Add(new TimelineMenuItem(enabled ? "Disable" : "Enable", null, () => RunAsync(Batch([.. ids.Select(id => (ICommand)new SetClipEnabledCommand(id, !enabled))], enabled ? "Disable" : "Enable"))));
        menu.Add(TimelineMenuItem.Separator);
        menu.Add(new TimelineMenuItem("Match frame", "F", () =>
        {
            MatchFrame(clip.Id, time);
            return Task.CompletedTask;
        }, clip.Clip.MediaId is not null || clip.Clip.SequenceId is not null));

        return menu;
    }

    /// <summary>Copies the selected clips to the clipboard.</summary>
    public void Copy()
    {
        string[] ids = SelectedClipIds();
        if (ids.Length == 0)
        {
            Status = "Select the clips to copy first.";
            return;
        }

        try
        {
            Clipboard?.SetClips(_session.Query(new CopyClipsQuery([.. ids])));
            Status = ids.Length == 1 ? "Copied a clip." : $"Copied {ids.Length} clips.";
        }
        catch (CommandException error)
        {
            Status = error.Message;
        }
    }

    /// <summary>Copies the selected clips and takes them off the timeline.</summary>
    public async Task CutAsync()
    {
        string[] ids = SelectedClipIds();
        Copy();

        if (ids.Length > 0)
        {
            await RunAsync(Batch([.. ids.Select(id => (ICommand)new RemoveClipCommand(id))], "Cut")).ConfigureAwait(true);
        }
    }

    /// <summary>Pastes what is on the clipboard at a time.</summary>
    public async Task PasteAsync(Flicks at, string? trackId, bool insert)
    {
        if (Clipboard?.GetClips() is not { } data)
        {
            Status = "There are no clips on the clipboard.";
            return;
        }

        await RunAsync(new PasteClipsCommand(data, at, trackId, insert, SequenceId)).ConfigureAwait(true);
    }

    /// <summary>Finds the source frame a clip shows at a time and says where it is.</summary>
    public void MatchFrame(string clipId, Flicks at)
    {
        try
        {
            MatchFrameInfo match = _session.Query(new MatchFrameQuery(clipId, at));
            string when = Core.Time.Timecode.Format(match.SourceTime, Content.Settings.FrameRate);

            if (match.SequenceId is { } nested)
            {
                OpenSequence?.Invoke(nested, SequenceId);
                _ = RunAsync(new SeekCommand(match.SourceTime));
                return;
            }

            string name = match.MediaId is { } mediaId ? _session.Project.MediaItem(mediaId)?.Name ?? mediaId : "the source";
            Status = $"Match frame: {name} at {when}";
            SourceMatched?.Invoke(this, match);
        }
        catch (CommandException error)
        {
            Status = error.Message;
        }
    }

    /// <summary>Raised when match frame finds a file's frame, for the source monitor to open.</summary>
    public event EventHandler<MatchFrameInfo>? SourceMatched;

    /// <summary>Sets the trail over this timeline; the tabs keep it.</summary>
    internal void SetTrail(ImmutableArray<SequenceCrumb> trail)
    {
        Trail = trail;
        OnPropertyChanged(nameof(Trail));
        OnPropertyChanged(nameof(HasTrail));
    }

    private static ICommand Batch(ImmutableArray<ICommand> commands, string label) =>
        commands.Length == 1 ? commands[0] : new BatchCommand([.. commands], label);

    private string NestName() => $"Nested sequence {_session.Project.Sequences.Length}";

    private string[] SelectedClipIds() => [.. Selected.Where(id => Content.Clip(id) is not null)];

    private void MatchFrameAtPlayhead()
    {
        Flicks playhead = Playhead;
        ClipView? clip = Content.Tracks
            .Where(track => track.Track.Kind != TrackKind.Audio)
            .Reverse()
            .Select(track => track.Clips.FirstOrDefault(view => view.Start <= playhead && playhead < view.End))
            .FirstOrDefault(view => view is not null);

        if (clip is null)
        {
            Status = "There is no picture under the playhead to match.";
            return;
        }

        MatchFrame(clip.Id, playhead);
    }

    /// <summary>What a press does with a tool other than select. False for the select behaviour.</summary>
    private bool ToolDown(TimelineHit hit, ClipView clip, ModifierKeys modifiers)
    {
        switch (Tools.Tool)
        {
            case TimelineTool.Razor:
                _gesture = Gesture.Razor;
                _grabbed = clip;
                _razorAll = modifiers.HasFlag(ModifierKeys.Shift);
                _snap = Snapper(null);
                PreviewRazor(_downAt);
                return true;

            case TimelineTool.Ripple when hit.Edge != ClipEdge.None:
                _gesture = Gesture.Ripple;
                _edge = hit.Edge;
                _trimmed = [.. SameEdge(clip, hit.Edge)];
                _snap = Snapper(_trimmed.Select(view => view.Id));
                return true;

            case TimelineTool.Roll when hit.Edge != ClipEdge.None:
                _rolls = RollPairs(clip, hit.Edge);
                if (_rolls.IsEmpty)
                {
                    Status = "Roll moves a cut between two clips that touch, and nothing touches this edge.";
                    _gesture = Gesture.None;
                    return true;
                }

                _gesture = Gesture.Roll;
                _cutTime = hit.Edge == ClipEdge.End ? clip.End : clip.Start;
                _snap = Snapper(_rolls.SelectMany(pair => new[] { pair.LeftId, pair.RightId }));
                return true;

            case TimelineTool.Slip:
                _gesture = Gesture.Slip;
                _trimmed = [.. Linked(clip)];
                SelectOnly(clip, modifiers);
                return true;

            case TimelineTool.Slide:
                _gesture = Gesture.Slide;
                _trimmed = [.. Linked(clip)];
                _snap = Snapper(_trimmed.Select(view => view.Id));
                SelectOnly(clip, modifiers);
                return true;

            case TimelineTool.RateStretch when hit.Edge == ClipEdge.End:
                _gesture = Gesture.Stretch;
                _trimmed = [.. SameEdge(clip, ClipEdge.End)];
                _snap = Snapper(_trimmed.Select(view => view.Id));
                return true;

            case TimelineTool.RateStretch when hit.Edge == ClipEdge.Start:
                Status = "Rate stretch drags a clip's end.";
                _gesture = Gesture.None;
                return true;

            case TimelineTool.Select:
                return false;

            default:
                // Ripple, roll and rate stretch on a clip's body select it, and do not move it.
                SelectOnly(clip, modifiers);
                _gesture = Gesture.None;
                return true;
        }
    }

    private void SelectOnly(ClipView clip, ModifierKeys modifiers)
    {
        string[] companions = [.. Content.Companions(clip.Id).Select(view => view.Id)];
        if (modifiers != ModifierKeys.None || !Selected.Contains(clip.Id))
        {
            Select(companions, ModeFor(modifiers));
        }
    }

    /// <summary>A clip and the clips linked to it: what a slip or a slide moves together.</summary>
    private IEnumerable<ClipView> Linked(ClipView clip) =>
        Content.Companions(clip.Id).Where(view =>
            view.Id == clip.Id || (clip.Clip.LinkGroupId is { } link && string.Equals(view.Clip.LinkGroupId, link, StringComparison.Ordinal)));

    /// <summary>The cuts a roll at a clip's edge moves: its own, and its linked clips' at the same place.</summary>
    private ImmutableArray<RollPair> RollPairs(ClipView clip, ClipEdge edge)
    {
        var pairs = ImmutableArray.CreateBuilder<RollPair>();
        foreach (ClipView companion in SameEdge(clip, edge))
        {
            if (Content.Track(companion.TrackId) is not { } track)
            {
                continue;
            }

            ClipView? other = edge == ClipEdge.End
                ? track.Clips.FirstOrDefault(view => view.Start == companion.End)
                : track.Clips.FirstOrDefault(view => view.End == companion.Start);

            if (other is not null)
            {
                pairs.Add(edge == ClipEdge.End
                    ? new RollPair(track.Id, companion.Id, other.Id)
                    : new RollPair(track.Id, other.Id, companion.Id));
            }
        }

        return pairs.ToImmutable();
    }

    private SnapService Snapper(IEnumerable<string>? exclude) =>
        Tools.Snapping ? SnapService.For(Content.Sequence, Playhead, exclude?.ToHashSet(StringComparer.Ordinal)) : SnapService.None;

    /// <summary>A time, snapped when something is near and snapping is not held off, with the guide drawn.</summary>
    private Flicks Snapped(Flicks time)
    {
        if (_bypassSnap || _snap.Find(time, Geometry) is not { } target)
        {
            SetGuide(null);
            return time;
        }

        SetGuide(target.Time);
        return target.Time;
    }

    private void SetGuide(Flicks? guide)
    {
        if (guide != Guide)
        {
            Guide = guide;
            Invalidate(TimelineLayers.Ghost);
        }
    }

    private void SetSelectedEdit(TimelineEdit? edit)
    {
        if (edit != SelectedEdit)
        {
            SelectedEdit = edit;
            OnPropertyChanged(nameof(SelectedEdit));
            Invalidate(TimelineLayers.Selection);
        }
    }

    /// <summary>The pointer moved during a tool's gesture.</summary>
    private void ToolMove(Point point)
    {
        if (!_dragged && Math.Abs(point.X - _downAt.X) < DragThreshold && Math.Abs(point.Y - _downAt.Y) < DragThreshold)
        {
            if (_gesture == Gesture.Razor)
            {
                PreviewRazor(point);
            }

            return;
        }

        _dragged = true;
        switch (_gesture)
        {
            case Gesture.Razor:
                PreviewRazor(point);
                break;
            case Gesture.Ripple:
                PreviewRipple(point);
                break;
            case Gesture.Roll:
                PreviewRoll(point);
                break;
            case Gesture.Slip:
                PreviewSlip(point);
                break;
            case Gesture.Slide:
                PreviewSlide(point);
                break;
            case Gesture.Stretch:
                PreviewStretch(point);
                break;
            case Gesture.Hand:
                double pixels = point.X - _downAt.X;
                SetGeometry(Geometry with
                {
                    Scroll = Flicks.Max(Flicks.Zero, _handScroll - Flicks.FromSeconds(pixels / Geometry.PixelsPerSecond)),
                    VerticalOffset = Math.Clamp(_handOffset - (point.Y - _downAt.Y), 0.0, VerticalMaximum),
                });
                break;
        }
    }

    /// <summary>The button came up on a tool's gesture.</summary>
    private void ToolUp(Gesture gesture, Point point)
    {
        switch (gesture)
        {
            case Gesture.Razor:
                PreviewRazor(point);
                Commit();
                return;

            case Gesture.Ripple when !_dragged:
                SetSelectedEdit(new TimelineEdit(
                    TimelineTool.Ripple,
                    _edge,
                    [.. _trimmed.Select(view => view.Id)],
                    [],
                    _edge == ClipEdge.Start ? _trimmed[0].Start : _trimmed[0].End));
                Status = "Edit picked: , and . trim it a frame, or type + or - and a number.";
                return;

            case Gesture.Roll when !_dragged:
                SetSelectedEdit(new TimelineEdit(TimelineTool.Roll, ClipEdge.None, [], _rolls, _cutTime));
                Status = "Edit picked: , and . roll it a frame, or type + or - and a number.";
                return;

            case Gesture.Hand:
                return;

            default:
                if (_dragged)
                {
                    ToolMove(point);
                    Commit();
                }

                return;
        }
    }

    private void PreviewRazor(Point point)
    {
        if (_grabbed is not { } grabbed)
        {
            return;
        }

        Flicks at = Snapped(Geometry.TimeAt(point.X));
        IEnumerable<ClipView> candidates = _razorAll
            ? Content.Tracks.Where(track => !track.Track.Locked).SelectMany(track => track.Clips)
            : Content.Companions(grabbed.Id).Where(view => view.Id == grabbed.Id || view.Clip.LinkGroupId is not null);

        ClipView[] cut = [.. candidates.Where(view => view.Start < at && at < view.End)];
        _pending = [.. cut.Select(view => (ICommand)new SplitClipCommand(view.Id, at))];
        SetGuide(at);
    }

    private void PreviewRipple(Point point)
    {
        Flicks to = Snapped(Geometry.TimeAt(point.X));
        string[] ids = [.. _trimmed.Select(view => view.Id)];
        EditResult<Sequence> result = EditOps.RippleTrim(Content.Sequence, ids, _edge, to, clip => SourceDuration(clip));

        _pending = [new RippleTrimClipsCommand([.. ids], _edge, to)];
        ShowResult(result, ids);
    }

    private void PreviewRoll(Point point)
    {
        Flicks by = Snapped(Geometry.TimeAt(point.X)) - _cutTime;
        Sequence sequence = Content.Sequence;
        string? refused = null;
        var commands = new List<ICommand>(_rolls.Length);

        foreach (RollPair pair in _rolls)
        {
            EditResult<Track> rolled = EditOps.Roll(sequence.Track(pair.TrackId)!, pair.LeftId, pair.RightId, by, SourceDuration(Content.Clip(pair.LeftId)!.Clip));
            if (rolled.IsOk)
            {
                sequence = sequence.ReplaceTrack(rolled.Value);
            }
            else
            {
                refused ??= rolled.Error!.Message;
            }

            commands.Add(new RollClipsCommand(pair.LeftId, pair.RightId, by));
        }

        _pending = [.. commands];
        ShowResult(refused is null ? sequence : new EditError("refused", refused), [.. _rolls.SelectMany(pair => new[] { pair.LeftId, pair.RightId })]);
    }

    private void PreviewSlip(Point point)
    {
        // Dragging right shows earlier material, as if pulling the film through the gate.
        Flicks by = Geometry.TimeAt(_downAt.X) - Geometry.TimeAt(point.X);
        Sequence sequence = Content.Sequence;
        string? refused = null;

        foreach (ClipView clip in _trimmed)
        {
            EditResult<Track> slipped = EditOps.Slip(sequence.Track(clip.TrackId)!, clip.Id, by, SourceDuration(clip.Clip));
            if (slipped.IsOk)
            {
                sequence = sequence.ReplaceTrack(slipped.Value);
            }
            else
            {
                refused ??= slipped.Error!.Message;
            }
        }

        _pending = [.. _trimmed.Select(clip => (ICommand)new SlipClipCommand(clip.Id, by))];
        Ghost = new TimelineGhost([.. _trimmed.Select(clip => new GhostClip(clip.TrackId, clip.Start, clip.End))], refused);

        if (refused is null && sequence.Track(_trimmed[0].TrackId)?.Clip(_trimmed[0].Id) is { } shown)
        {
            Rational fps = Content.Settings.FrameRate;
            Status = $"Slip: source {Core.Time.Timecode.Format(shown.SourceIn, fps)} to {Core.Time.Timecode.Format(shown.SourceOut, fps)}";
        }

        Invalidate(TimelineLayers.Ghost);
    }

    private void PreviewSlide(Point point)
    {
        Flicks by = Geometry.TimeAt(point.X) - Geometry.TimeAt(_downAt.X);
        if (!_bypassSnap && _snap.FindFor(_trimmed.SelectMany(clip => new[] { clip.Start + by, clip.End + by }), Geometry) is { } snap)
        {
            by += snap.Correction;
            SetGuide(snap.Target.Time);
        }
        else
        {
            SetGuide(null);
        }

        Sequence sequence = Content.Sequence;
        string? refused = null;
        foreach (ClipView clip in _trimmed)
        {
            EditResult<Track> slid = EditOps.Slide(sequence.Track(clip.TrackId)!, clip.Id, by);
            if (slid.IsOk)
            {
                sequence = sequence.ReplaceTrack(slid.Value);
            }
            else
            {
                refused ??= slid.Error!.Message;
            }
        }

        _pending = [.. _trimmed.Select(clip => (ICommand)new SlideClipCommand(clip.Id, by))];
        Ghost = new TimelineGhost([.. _trimmed.Select(clip => new GhostClip(clip.TrackId, clip.Start + by, clip.End + by))], refused);
        Invalidate(TimelineLayers.Ghost);
    }

    private void PreviewStretch(Point point)
    {
        Flicks to = Snapped(Geometry.TimeAt(point.X));
        Sequence sequence = Content.Sequence;
        string? refused = null;
        var commands = new List<ICommand>(_trimmed.Length);

        foreach (ClipView clip in _trimmed)
        {
            Flicks duration = to - clip.Start;
            EditResult<Track> stretched = EditOps.RateStretch(sequence.Track(clip.TrackId)!, clip.Id, duration);
            if (stretched.IsOk)
            {
                sequence = sequence.ReplaceTrack(stretched.Value);
            }
            else
            {
                refused ??= stretched.Error!.Message;
            }

            commands.Add(new RateStretchClipCommand(clip.Id, duration));
        }

        _pending = [.. commands];
        Ghost = new TimelineGhost([.. _trimmed.Select(clip => new GhostClip(clip.TrackId, clip.Start, Flicks.Max(clip.Start, to)))], refused);
        Invalidate(TimelineLayers.Ghost);
    }

    /// <summary>Draws where a sequence-wide edit would leave every clip it touches.</summary>
    private void ShowResult(EditResult<Sequence> result, IReadOnlyCollection<string> edited)
    {
        if (!result.IsOk)
        {
            Ghost = new TimelineGhost(
                [.. edited.Select(Content.Clip).OfType<ClipView>().Select(clip => new GhostClip(clip.TrackId, clip.Start, clip.End))],
                result.Error!.Message);
            Invalidate(TimelineLayers.Ghost);
            return;
        }

        Sequence after = result.Value;
        var ghosts = new List<GhostClip>();
        foreach (Track track in after.Tracks)
        {
            Track? before = Content.Sequence.Track(track.Id);
            foreach (Clip clip in track.Clips)
            {
                if (edited.Contains(clip.Id) || before?.Clip(clip.Id) is not { } old || old.Range != clip.Range)
                {
                    ghosts.Add(new GhostClip(track.Id, clip.Start, clip.End));
                }
            }
        }

        Ghost = new TimelineGhost([.. ghosts], null);
        Invalidate(TimelineLayers.Ghost);
    }

    private Flicks? SourceDuration(Clip clip) =>
        clip.MediaId is { } mediaId ? _session.Project.MediaItem(mediaId)?.Duration
        : clip.SequenceId is { } nested ? _session.Project.Sequence(nested)?.Duration
        : null;

    private async Task TrimEditAsync(int frames)
    {
        if (SelectedEdit is not { } edit || frames == 0)
        {
            return;
        }

        Flicks by = Flicks.FromFrames(Math.Abs(frames), Content.Settings.FrameRate);
        if (frames < 0)
        {
            by = Flicks.Zero - by;
        }

        ICommand command = edit.Tool == TimelineTool.Roll
            ? Batch([.. edit.Rolls.Select(pair => (ICommand)new RollClipsCommand(pair.LeftId, pair.RightId, by))], "Roll")
            : new RippleTrimClipsCommand([.. edit.ClipIds], edit.Edge, edit.Time + by);

        if (await RunAsync(command).ConfigureAwait(true))
        {
            // A ripple of a start keeps the clip where it begins, so the edit stays put.
            bool moves = edit.Tool == TimelineTool.Roll || edit.Edge == ClipEdge.End;
            SetSelectedEdit(edit with { Time = moves ? edit.Time + by : edit.Time });
        }
    }

    private bool EntryKey(Key key)
    {
        int digit = key switch
        {
            >= Key.D0 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => -1,
        };

        if (digit >= 0)
        {
            _entry += digit.ToString(CultureInfo.InvariantCulture);
            ShowEntry();
            return true;
        }

        switch (key)
        {
            case Key.OemPeriod or Key.Decimal when !_entry!.Contains('.', StringComparison.Ordinal):
                _entry += ".";
                ShowEntry();
                return true;

            case Key.Back:
                _entry = _entry!.Length > 1 ? _entry[..^1] : null;
                if (_entry is null)
                {
                    Status = string.Empty;
                }
                else
                {
                    ShowEntry();
                }

                return true;

            case Key.Enter:
                string typed = _entry!;
                _entry = null;
                Status = string.Empty;
                _ = CommitEntryAsync(typed);
                return true;

            case Key.Escape:
                _entry = null;
                Status = string.Empty;
                return true;

            default:
                // Anything else ends the entry and does what it does.
                _entry = null;
                Status = string.Empty;
                return false;
        }
    }

    private void ShowEntry() =>
        Status = SelectedEdit is null
            ? $"Nudge by {_entry} frames (seconds.frames), Enter to apply"
            : $"Trim by {_entry} frames (seconds.frames), Enter to apply";

    /// <summary>Frames from what was typed: 12 is twelve frames, 1.12 a second and twelve frames.</summary>
    internal static int? ParseEntry(string typed, Rational fps)
    {
        if (typed.Length < 2)
        {
            return null;
        }

        int sign = typed[0] == '-' ? -1 : 1;
        string[] parts = typed[1..].Split('.');
        long seconds = parts.Length == 2 && parts[0].Length > 0 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        string framePart = parts.Length == 2 ? parts[1] : parts[0];
        long frames = framePart.Length > 0 ? long.Parse(framePart, CultureInfo.InvariantCulture) : 0;
        long perSecond = (long)Math.Round((double)fps.Num / fps.Den);
        long total = (seconds * perSecond) + frames;

        return total == 0 ? null : (int)(sign * total);
    }

    private async Task CommitEntryAsync(string typed)
    {
        if (ParseEntry(typed, Content.Settings.FrameRate) is not { } frames)
        {
            return;
        }

        if (SelectedEdit is not null)
        {
            await TrimEditAsync(frames).ConfigureAwait(true);
            return;
        }

        string[] ids = SelectedClipIds();
        if (ids.Length > 0)
        {
            await RunAsync(new NudgeClipsCommand([.. ids], frames)).ConfigureAwait(true);
        }
    }
}
