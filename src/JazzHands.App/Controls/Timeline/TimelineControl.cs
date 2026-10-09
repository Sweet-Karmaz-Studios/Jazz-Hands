using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>
/// Draws a <see cref="TimelineViewModel"/> and hands it the mouse.
/// </summary>
/// <remarks>
/// No element per clip: each layer is one <see cref="DrawingVisual"/> redrawn only when the view
/// model says that layer changed, so five hundred clips are one pass and a moving playhead is a
/// line. The layers, bottom up: lanes, clips, markers, ruler, selection, ghost, playhead.
///
/// Everything that says where a thing is comes from <see cref="TimelineGeometry"/>; everything
/// the mouse does goes to the view model, which decides what it means. This only draws.
///
/// The clips layer is a visual per track, each recorded on its own and kept: an edit records
/// again only the tracks whose clips changed, and the rest stay as they are (Phase 32; an edit to
/// two thousand clips on screen went from about 7 ms of drawing to about half a millisecond). Scrolling, zooming,
/// a row moving and new thumbnails record every track again.
///
/// <see cref="LastClipsRender"/>, <see cref="ClipsRenders"/> and <see cref="TracksRecorded"/> are
/// what the performance tests read: how long drawing the clips took, how many times it happened,
/// and how many track drawings were made.
/// </remarks>
public sealed class TimelineControl : FrameworkElement
{
    private const double ClipInset = VolumeLine.ClipInset;
    private const double LabelPadding = 4.0;
    private const double ClipFontSize = 11.0;

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _lanes = new();
    private readonly DrawingVisual _clips = new();
    private readonly DrawingVisual _markers = new();
    private readonly DrawingVisual _ruler = new();
    private readonly DrawingVisual _selection = new();
    private readonly DrawingVisual _ghost = new();
    private readonly DrawingVisual _playhead = new();

    private readonly Dictionary<string, SolidColorBrush> _trackBrushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextDrawing> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextDrawing> _rulerLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackDrawing> _trackDrawings = new(StringComparer.Ordinal);
    private readonly List<string> _goneTracks = [];
    private readonly HashSet<string> _drawnTracks = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes the timeline visible to UI Automation (screen readers, and the UI suite, which drops
    /// media on it): a drawn element with no peer of its own is left out of the tree.
    /// </summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);

    private sealed class TimelinePeer(TimelineControl owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(TimelineControl);

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }

    private TimelineViewModel? _model;
    private TimelineLayers _dirty = TimelineLayers.All;
    private bool _renderingHooked;
    private Flicks _drawnPlayhead = Flicks.MinValue;
    private Palette? _palette;
    private Typeface? _typeface;
    private double _pixelsPerDip = 1.0;
    private ITimelineImagery _imagery = NoImagery.Instance;
    private int _imageryVersion;

    /// <summary>Creates the control.</summary>
    public TimelineControl()
    {
        _visuals = new VisualCollection(this) { _lanes, _clips, _markers, _ruler, _selection, _ghost, _playhead };

        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        DataContextChanged += (_, _) => Attach(DataContext as TimelineViewModel);
        Loaded += (_, _) => HookRendering(true);
        Unloaded += (_, _) => HookRendering(false);
    }

    /// <summary>
    /// Where thumbnails and waveforms come from: the attached view model's, which in the editor are
    /// the engine's caches. <see cref="NoImagery"/> until one is attached, which draws placeholders.
    /// </summary>
    public ITimelineImagery Imagery
    {
        get => _imagery;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _imagery.Changed -= OnImageryChanged;
            _imagery = value;
            _imagery.Changed += OnImageryChanged;
            _imageryVersion++;
            OnInvalidated(this, TimelineLayers.Clips);
        }
    }

    /// <summary>How long the clips layer took to draw last time, for the performance test.</summary>
    public TimeSpan LastClipsRender { get; private set; }

    /// <summary>How many times the clips layer has been drawn.</summary>
    public int ClipsRenders { get; private set; }

    /// <summary>How many track drawings the clips layer has recorded, kept ones not counted.</summary>
    public int TracksRecorded { get; private set; }

    /// <summary>How many times the playhead layer has been drawn.</summary>
    public int PlayheadRenders { get; private set; }

    /// <inheritdoc />
    protected override int VisualChildrenCount => _visuals.Count;

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>
    /// Draws whatever is dirty now, rather than on the next frame. The rendering loop calls this;
    /// tests call it to draw without one.
    /// </summary>
    public void DrawDirty()
    {
        if (_model is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _palette ??= new Palette(this);
        _typeface ??= new Typeface(TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        Flicks playhead = _model.Playhead;
        _model.Follow(playhead);

        if (playhead != _drawnPlayhead)
        {
            _dirty |= TimelineLayers.Playhead;
        }

        TimelineLayers dirty = _dirty;
        _dirty = TimelineLayers.None;

        if (dirty.HasFlag(TimelineLayers.Lanes))
        {
            DrawLanes();
        }

        if (dirty.HasFlag(TimelineLayers.Clips))
        {
            long started = Stopwatch.GetTimestamp();
            DrawClips();
            LastClipsRender = Stopwatch.GetElapsedTime(started);
            ClipsRenders++;
        }

        if (dirty.HasFlag(TimelineLayers.Markers))
        {
            DrawMarkers();
        }

        if (dirty.HasFlag(TimelineLayers.Ruler))
        {
            DrawRuler();
        }

        if (dirty.HasFlag(TimelineLayers.Selection))
        {
            DrawSelection();
        }

        if (dirty.HasFlag(TimelineLayers.Ghost))
        {
            DrawGhost();
        }

        if (dirty.HasFlag(TimelineLayers.Playhead))
        {
            DrawPlayhead(playhead);
            _drawnPlayhead = playhead;
            PlayheadRenders++;
        }
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _model?.SetViewport(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
        _dirty = TimelineLayers.All;
        DrawDirty();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // The element itself draws only a transparent fill, so the whole area takes the mouse.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);

        if (_model is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Focus();

        if (e.ClickCount == 2)
        {
            _model.DoubleClick(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        CaptureMouse();
        _model.PointerDown(e.GetPosition(this), Keyboard.Modifiers);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseRightButtonUp(e);

        if (_model is null)
        {
            return;
        }

        IReadOnlyList<TimelineMenuItem> items = _model.MenuAt(e.GetPosition(this));
        if (items.Count == 0)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = this };
        foreach (TimelineMenuItem item in items)
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new System.Windows.Controls.Separator());
                continue;
            }

            var entry = new System.Windows.Controls.MenuItem
            {
                Header = Views.MenuText.Escape(item.Header),
                InputGestureText = item.Shortcut ?? string.Empty,
                IsEnabled = item.Enabled,
            };

            Func<Task> run = item.Run!;
            entry.Click += async (_, _) => await run().ConfigureAwait(true);
            menu.Items.Add(entry);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        _model?.PointerMove(e.GetPosition(this), Keyboard.Modifiers);
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);

        if (_model is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _model.PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _model?.PointerCancel();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && _model is not null)
        {
            _model.PointerCancel();
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);

        if (_model is null)
        {
            return;
        }

        _model.Wheel(e.Delta, e.GetPosition(this), Keyboard.Modifiers);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnDragOver(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnDragOver(e);

        // A title preset is a new title on the video track under the pointer, at the pointer.
        if (EffectDragData.TitlePreset(e.Data) is { } titlePreset)
        {
            bool free = _model is not null && _model.TitleDragOver(titlePreset, e.GetPosition(this));
            e.Effects = free ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // Effects and presets from the effects panel go on the clip or track under the pointer.
        string? effect = EffectDragData.Effect(e.Data);
        string? preset = EffectDragData.Preset(e.Data);
        if (effect is not null || preset is not null)
        {
            bool fits = _model is not null && _model.EffectDragOver(effect, preset, e.GetPosition(this));
            e.Effects = fits ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        IReadOnlyList<string> ids = MediaDragData.Ids(e.Data);
        _model?.DraggedRange = MediaDragData.Range(e.Data);
        bool accepted = _model is not null && ids.Count > 0 && _model.DragOver(ids, e.GetPosition(this), (e.KeyStates & DragDropKeyStates.ControlKey) != 0);

        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _model?.DragLeave();
    }

    /// <inheritdoc />
    protected override async void OnDrop(DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnDrop(e);

        if (_model is not null && EffectDragData.TitlePreset(e.Data) is { } titlePreset)
        {
            e.Handled = true;
            await _model.DropTitleAsync(titlePreset, e.GetPosition(this)).ConfigureAwait(true);
            return;
        }

        string? effect = EffectDragData.Effect(e.Data);
        string? preset = EffectDragData.Preset(e.Data);
        if (_model is not null && (effect is not null || preset is not null))
        {
            e.Handled = true;
            await _model.DropEffectAsync(effect, preset, e.GetPosition(this)).ConfigureAwait(true);
            return;
        }

        IReadOnlyList<string> ids = MediaDragData.Ids(e.Data);
        if (_model is null || ids.Count == 0)
        {
            return;
        }

        e.Handled = true;
        _model.DraggedRange = MediaDragData.Range(e.Data);
        await _model.DropAsync(ids, e.GetPosition(this), (e.KeyStates & DragDropKeyStates.ControlKey) != 0).ConfigureAwait(true);
        _model.DraggedRange = null;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Brush Faded(Brush brush, double opacity)
    {
        Brush copy = brush.CloneCurrentValue();
        copy.Opacity = opacity;
        return Frozen(copy);
    }

    private static Color ParseColor(string text)
    {
        try
        {
            return ColorConverter.ConvertFromString(text) is Color color ? color : Colors.SteelBlue;
        }
        catch (FormatException)
        {
            return Colors.SteelBlue;
        }
    }

    private void Attach(TimelineViewModel? model)
    {
        if (_model is not null)
        {
            _model.Invalidated -= OnInvalidated;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }

        _model = model;

        if (_model is not null)
        {
            _model.Invalidated += OnInvalidated;
            _model.PropertyChanged += OnModelPropertyChanged;
            Imagery = _model.Imagery;

            if (ActualWidth > 0 && ActualHeight > 0)
            {
                _model.SetViewport(ActualWidth, ActualHeight);
            }
        }

        _dirty = TimelineLayers.All;
        DrawDirty();
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.Cursor) && _model is not null)
        {
            Cursor = _model.Cursor switch
            {
                TimelineCursor.TrimStart or TimelineCursor.TrimEnd or TimelineCursor.Fade => Cursors.SizeWE,
                TimelineCursor.Scrub => Cursors.IBeam,
                TimelineCursor.Move => Cursors.SizeAll,
                TimelineCursor.Razor => Cursors.Cross,
                TimelineCursor.Hand => Cursors.Hand,
                TimelineCursor.Slip => Cursors.ScrollWE,
                TimelineCursor.Volume => Cursors.SizeNS,
                _ => Cursors.Arrow,
            };
        }
    }

    private void OnInvalidated(object? sender, TimelineLayers layers)
    {
        _dirty |= layers;

        // Without a rendering loop (a test, a control not yet loaded) draw straight away.
        if (!_renderingHooked)
        {
            DrawDirty();
        }
    }

    private void HookRendering(bool hook)
    {
        if (hook == _renderingHooked)
        {
            return;
        }

        _renderingHooked = hook;

        if (hook)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => DrawDirty();

    private void OnImageryChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _imageryVersion++;
        OnInvalidated(this, TimelineLayers.Clips);
    });

    private SolidColorBrush TrackBrush(string color)
    {
        if (!_trackBrushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ParseColor(color));
            brush.Freeze();
            _trackBrushes[color] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Thumbnails on a picture clip and the waveform on a sound clip, from <see cref="Imagery"/>
    /// when it has them; otherwise a placeholder, and it is asked again on the next draw.
    /// </summary>
    private void DrawImagery(DrawingContext dc, ClipView clip, Rect body, Flicks visibleStart, Flicks visibleEnd, double pixelsPerSecond)
    {
        if (!clip.Clip.IsMedia || clip.MediaMissing || body.Width < 4 || body.Height < 12)
        {
            return;
        }

        Flicks from = Flicks.Max(clip.Start, visibleStart);
        Flicks to = Flicks.Min(clip.End, visibleEnd);
        Palette palette = _palette!;

        if (clip.Kind == TrackKind.Audio)
        {
            double middle = body.Top + (body.Height / 2);

            if (Imagery.TryGetWaveform(clip, from, to, (int)body.Width, out WaveformPeaks peaks) && peaks.Maximum.Length > 1)
            {
                var wave = new StreamGeometry();
                using (StreamGeometryContext context = wave.Open())
                {
                    double half = (body.Height / 2) - 2;
                    double step = body.Width / peaks.Maximum.Length;

                    context.BeginFigure(new Point(body.Left, middle - (peaks.Maximum[0] * half)), true, true);
                    for (int column = 1; column < peaks.Maximum.Length; column++)
                    {
                        context.LineTo(new Point(body.Left + (column * step), middle - (peaks.Maximum[column] * half)), false, false);
                    }

                    for (int column = peaks.Minimum.Length - 1; column >= 0; column--)
                    {
                        context.LineTo(new Point(body.Left + (column * step), middle - (peaks.Minimum[column] * half)), false, false);
                    }
                }

                wave.Freeze();
                dc.DrawGeometry(palette.Waveform, null, wave);
            }
            else
            {
                dc.DrawLine(palette.WaveformPlaceholder, new Point(body.Left + 2, middle), new Point(body.Right - 2, middle));
            }

            return;
        }

        double top = body.Top + ClipFontSize + 6;
        double height = body.Bottom - top - 2;
        if (height < 8)
        {
            return;
        }

        // A continuous filmstrip, the way every editor draws one: slots edge to edge from the
        // clip's start, each showing the picture taken nearest before its middle. The grid the
        // pictures are on is coarser than a slot at most zooms, so a picture can fill two or
        // three slots; while the strip is still filling, a slot shows the nearest one there is.
        if (Imagery.TryGetThumbnails(clip, from, to, pixelsPerSecond, height * 16.0 / 9.0, out IReadOnlyList<ThumbnailTile> tiles) && tiles.Count > 0)
        {
            TimelineGeometry geometry = _model!.Geometry;
            ThumbnailTile[] ordered = [.. tiles.OrderBy(tile => tile.TimelineTime)];
            ImageSource first = ordered[0].Image;
            double slot = first.Height > 0 ? height * first.Width / first.Height : height * 16.0 / 9.0;

            dc.PushClip(new RectangleGeometry(body));

            double clipLeft = geometry.XOf(clip.Start);
            double x = clipLeft + (Math.Floor((body.Left - clipLeft) / slot) * slot);
            int index = 0;

            for (; x < body.Right; x += slot)
            {
                Flicks middle = geometry.TimeAt(x + (slot / 2), snapToFrame: false);
                while (index + 1 < ordered.Length && ordered[index + 1].TimelineTime <= middle)
                {
                    index++;
                }

                dc.DrawImage(ordered[index].Image, new Rect(x, top, slot, height));
            }

            dc.Pop();
        }
    }

    /// <summary>A clip name, laid out once.</summary>
    private TextDrawing ClipLabel(string text, Brush brush) => Text(text, _labels, brush, ClipFontSize);

    /// <summary>
    /// Text laid out once and recorded at the origin, frozen. Laying a line out is the expensive
    /// part of drawing it, and <see cref="DrawingContext.DrawText"/> does it on every call; five
    /// hundred clip names a frame that way cost 15 ms, and drawn from here about one. The cap keeps
    /// a long session's churn of renamed clips bounded.
    /// </summary>
    private TextDrawing Text(string text, Dictionary<string, TextDrawing> cache, Brush brush, double size)
    {
        if (!cache.TryGetValue(text, out TextDrawing? label))
        {
            if (cache.Count > 4096)
            {
                cache.Clear();
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface!,
                size,
                brush,
                _pixelsPerDip)
            {
                // A caption keeps its two lines (Phase 39); anything else is one line.
                MaxLineCount = Math.Min(2, text.Count(character => character == '\n') + 1),
            };

            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawText(formatted, new Point(0, 0));
            }

            drawing.Freeze();
            label = new TextDrawing(drawing, formatted.WidthIncludingTrailingWhitespace);
            cache[text] = label;
        }

        return label;
    }

    /// <summary>Draws recorded text with its top left corner at a point.</summary>
    private static void DrawTextAt(DrawingContext dc, TextDrawing text, double x, double y)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawDrawing(text.Drawing);
        dc.Pop();
    }

    private void DrawLanes()
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        TimelineGeometry geometry = model.Geometry;
        double width = ActualWidth;
        double height = ActualHeight;

        using DrawingContext dc = _lanes.RenderOpen();
        dc.DrawRectangle(palette.Background, null, new Rect(0, 0, width, height));

        dc.PushClip(new RectangleGeometry(new Rect(0, TimelineGeometry.TracksTop, width, Math.Max(0, height - TimelineGeometry.TracksTop))));

        for (int index = 0; index < geometry.Rows.Length; index++)
        {
            TrackRow row = geometry.Rows[index];
            double top = geometry.TopOf(row);

            if (top > height || top + row.Height < TimelineGeometry.TracksTop)
            {
                continue;
            }

            dc.DrawRectangle(index % 2 == 0 ? palette.Lane : palette.LaneAlt, null, new Rect(0, top, width, row.Height));
            dc.DrawLine(palette.RowLine, new Point(0, top + row.Height - 0.5), new Point(width, top + row.Height - 0.5));
        }

        if (model.Content.Sequence.InOut is { } range)
        {
            double left = Math.Max(0, geometry.XOf(range.Start));
            double right = Math.Min(width, geometry.XOf(range.End));

            if (right > left)
            {
                dc.DrawRectangle(palette.InOut, null, new Rect(left, TimelineGeometry.TracksTop, right - left, height));
            }
        }

        dc.Pop();
    }

    private void DrawClips()
    {
        TimelineViewModel model = _model!;
        TimelineGeometry geometry = model.Geometry;
        double width = ActualWidth;
        double height = ActualHeight;
        (Flicks visibleStart, Flicks visibleEnd) = geometry.Visible(width);

        var area = new Rect(0, TimelineGeometry.TracksTop, width, Math.Max(0, height - TimelineGeometry.TracksTop));
        if (_clips.Clip is not RectangleGeometry { Rect: var clipped } || clipped != area)
        {
            var clip = new RectangleGeometry(area);
            clip.Freeze();
            _clips.Clip = clip;
        }

        _drawnTracks.Clear();
        foreach (TrackView track in model.Content.Tracks)
        {
            if (geometry.Row(track.Id) is not { } row)
            {
                continue;
            }

            double top = geometry.TopOf(row);
            if (top > height || top + row.Height < TimelineGeometry.TracksTop)
            {
                continue;
            }

            _drawnTracks.Add(track.Id);
            _trackDrawings.TryGetValue(track.Id, out TrackDrawing? kept);
            if (kept is null || !kept.Matches(track, geometry, top, row.Height, width, _imageryVersion, _palette!))
            {
                DrawingVisual visual = kept?.Visual ?? new DrawingVisual();
                using (DrawingContext recording = visual.RenderOpen())
                {
                    DrawTrack(recording, track, row, top, geometry, width, visibleStart, visibleEnd);
                }

                if (kept is null)
                {
                    _clips.Children.Add(visual);
                }

                _trackDrawings[track.Id] = new TrackDrawing(track, geometry.PixelsPerSecond, geometry.Scroll, top, row.Height, width, _imageryVersion, _palette!, visual);
                TracksRecorded++;
            }
        }

        // Tracks gone or scrolled out of view let go of their visuals.
        if (_trackDrawings.Count > _drawnTracks.Count)
        {
            _goneTracks.Clear();
            foreach ((string id, TrackDrawing drawing) in _trackDrawings)
            {
                if (!_drawnTracks.Contains(id))
                {
                    _clips.Children.Remove(drawing.Visual);
                    _goneTracks.Add(id);
                }
            }

            foreach (string id in _goneTracks)
            {
                _trackDrawings.Remove(id);
            }
        }
    }

    /// <summary>One track's clips and transitions, recorded into a drawing of its own.</summary>
    private void DrawTrack(DrawingContext dc, TrackView track, TrackRow row, double top, TimelineGeometry geometry, double width, Flicks visibleStart, Flicks visibleEnd)
    {
        Palette palette = _palette!;
        SolidColorBrush fill = TrackBrush(track.Track.Color);
        double clipTop = top + ClipInset;
        double clipHeight = Math.Max(1.0, row.Height - (ClipInset * 2));

        foreach (ClipView clip in Visible(track, visibleStart, visibleEnd))
        {
            double left = geometry.XOf(clip.Start);
            double right = geometry.XOf(clip.End);

            // Past the edges the body is clipped; drawing it wider than the control only
            // costs the rasterizer.
            double drawnLeft = Math.Max(left, -4.0);
            double drawnRight = Math.Min(right, width + 4.0);
            // Overlapping cues side by side, each in its lane of the row.
            (double laneTop, double laneHeight) = track.Lanes.Slice(clip.Clip.Id, clipTop, clipHeight);
            var body = new Rect(drawnLeft, laneTop, Math.Max(1.0, drawnRight - drawnLeft - 1.0), laneHeight);

            Brush bodyBrush = clip.MediaMissing ? palette.Missing : clip.Clip.Enabled ? fill : palette.Disabled;
            dc.DrawRoundedRectangle(bodyBrush, palette.ClipEdge, body, 3.0, 3.0);

            DrawImagery(dc, clip, body, visibleStart, visibleEnd, geometry.PixelsPerSecond);

            // A multicam clip's angle cuts, as the cuts they are.
            foreach (Flicks cut in clip.Cuts)
            {
                double x = geometry.XOf(cut);
                if (x > body.Left && x < body.Right)
                {
                    dc.DrawLine(palette.ClipEdge, new Point(x, body.Top), new Point(x, body.Bottom));
                }
            }

            Rect lineBody = VolumeLine.Body(geometry, row, clip);
            if (FadeHandles.Shown(clip, lineBody))
            {
                DrawFades(dc, clip, lineBody);
            }

            if (VolumeLine.Shown(clip, lineBody))
            {
                DrawVolumeLine(dc, clip, lineBody, width);
            }

            if (SpeedLine.Shown(clip, lineBody))
            {
                DrawSpeedLine(dc, clip, lineBody, width);
            }

            if (clip.Clip.LinkGroupId is not null && body.Width > 3)
            {
                dc.DrawRectangle(palette.LinkMark, null, new Rect(body.Left + 1, body.Bottom - 3, Math.Min(10.0, body.Width - 2), 2));
            }

            if (body.Width > 16 && body.Height > ClipFontSize + 2)
            {
                // A two line caption shows both lines where the clip is tall enough for them, and both
                // on one line with a slash where it is not (only its first line showed, 2026-09-30).
                string shown = clip.Label.Contains('\n', StringComparison.Ordinal) && body.Height < (ClipFontSize * 2.8) + 4
                    ? clip.Label.Replace("\n", " / ", StringComparison.Ordinal)
                    : clip.Label;
                TextDrawing label = ClipLabel(shown, palette.ClipText);
                double textLeft = Math.Max(body.Left, 0) + LabelPadding;
                double room = body.Right - LabelPadding - textLeft;

                if (room > 4)
                {
                    bool fits = label.Width <= room;
                    if (!fits)
                    {
                        dc.PushClip(new RectangleGeometry(new Rect(textLeft, body.Top, room, body.Height)));
                    }

                    DrawTextAt(dc, label, textLeft, body.Top + 2);

                    if (!fits)
                    {
                        dc.Pop();
                    }
                }
            }
        }

        foreach (TransitionView bar in track.Transitions)
        {
            if (bar.End >= visibleStart && bar.Start <= visibleEnd)
            {
                DrawTransition(dc, geometry.TransitionBand(row, bar.Start, bar.End), bar);
            }
        }
    }

    /// <summary>A track's visual and everything it was recorded for.</summary>
    private sealed record TrackDrawing(
        TrackView View,
        double PixelsPerSecond,
        Flicks Scroll,
        double Top,
        double RowHeight,
        double Width,
        int ImageryVersion,
        Palette Palette,
        DrawingVisual Visual)
    {
        /// <summary>True when the drawing still shows the track as it would be drawn now.</summary>
        public bool Matches(TrackView view, TimelineGeometry geometry, double top, double rowHeight, double width, int imageryVersion, Palette palette) =>
            ReferenceEquals(View, view)
            && PixelsPerSecond == geometry.PixelsPerSecond
            && Scroll == geometry.Scroll
            && Top == top
            && RowHeight == rowHeight
            && Width == width
            && ImageryVersion == imageryVersion
            && ReferenceEquals(Palette, palette);
    }

    /// <summary>
    /// A sound clip's fades: the part of the body a fade takes is shaded above a ramp from
    /// silence at the clip's edge to full at the fade's end, and each fade has a handle at the
    /// top, drawn where <see cref="FadeHandles"/> says so the view model grabs the same spot.
    /// </summary>
    private void DrawFades(DrawingContext dc, ClipView clip, Rect body)
    {
        TimelineGeometry geometry = _model!.Geometry;
        Palette palette = _palette!;

        foreach (bool fadeIn in (bool[])[true, false])
        {
            Flicks length = fadeIn ? FadeHandles.In(clip) : FadeHandles.Out(clip);
            if (length > Flicks.Zero)
            {
                double edge = fadeIn ? body.Left : body.Right;
                double end = geometry.XOf(fadeIn ? clip.Start + length : clip.End - length);
                var shade = new StreamGeometry();
                using (StreamGeometryContext context = shade.Open())
                {
                    context.BeginFigure(new Point(edge, body.Bottom), true, true);
                    context.LineTo(new Point(edge, body.Top), false, false);
                    context.LineTo(new Point(end, body.Top), false, false);
                }

                shade.Freeze();
                dc.DrawGeometry(palette.FadeShade, null, shade);
                dc.DrawLine(palette.FadePen, new Point(edge, body.Bottom), new Point(end, body.Top));
            }

            Point handle = FadeHandles.Handle(geometry, clip, body, fadeIn);
            double half = FadeHandles.Size / 2;
            dc.DrawRectangle(palette.FadeHandle, palette.ClipEdge, new Rect(handle.X - half, handle.Y - half, FadeHandles.Size, FadeHandles.Size));
        }
    }

    /// <summary>
    /// The rubber band: the clip's volume as a line across it, on the fader's scale, with a dot on
    /// each keyframe. A level is one straight line; a curve is followed every few pixels and
    /// through every keyframe, so its corners are where the keyframes are.
    /// </summary>
    private void DrawVolumeLine(DrawingContext dc, ClipView clip, Rect body, double width)
    {
        TimelineGeometry geometry = _model!.Geometry;
        Palette palette = _palette!;
        double from = Math.Max(body.Left, 0.0);
        double to = Math.Min(body.Right, width);
        if (to - from < 2.0)
        {
            return;
        }

        double[] keys = [.. VolumeLine.Keyframes(clip).Select(geometry.XOf)];
        var xs = new List<double> { from, to };
        if (keys.Length > 0)
        {
            for (double x = from + 3.0; x < to; x += 3.0)
            {
                xs.Add(x);
            }

            xs.AddRange(keys.Where(x => x > from && x < to));
            xs.Sort();
        }

        double Y(double x) => VolumeLine.Y(body, VolumeLine.Level(clip, geometry.TimeAt(x, snapToFrame: false)));

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(new Point(xs[0], Y(xs[0])), false, false);
            for (int index = 1; index < xs.Count; index++)
            {
                context.LineTo(new Point(xs[index], Y(xs[index])), true, false);
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, palette.VolumePen, line);

        foreach (double x in keys)
        {
            if (x >= from - 4.0 && x <= to + 4.0)
            {
                dc.DrawEllipse(palette.VolumeHandle, palette.ClipEdge, new Point(x, Y(Math.Clamp(x, from, to))), 3.5, 3.5);
            }
        }
    }

    /// <summary>A remapped clip's speed curve across it (Phase 45), with its points, sampled every few pixels.</summary>
    private void DrawSpeedLine(DrawingContext dc, ClipView clip, Rect body, double width)
    {
        TimelineGeometry geometry = _model!.Geometry;
        Palette palette = _palette!;
        double from = Math.Max(body.Left, 0.0);
        double to = Math.Min(body.Right, width);
        if (to - from < 2.0)
        {
            return;
        }

        double[] points = [.. SpeedLine.Points(clip).Select(geometry.XOf)];
        var xs = new List<double> { from, to };
        for (double x = from + 3.0; x < to; x += 3.0)
        {
            xs.Add(x);
        }

        xs.AddRange(points.Where(x => x > from && x < to));
        xs.Sort();

        double Y(double x) => SpeedLine.Y(body, SpeedLine.Level(clip, geometry.TimeAt(x, snapToFrame: false)));

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(new Point(xs[0], Y(xs[0])), false, false);
            for (int index = 1; index < xs.Count; index++)
            {
                context.LineTo(new Point(xs[index], Y(xs[index])), true, false);
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, palette.SpeedEdge, line);
        dc.DrawGeometry(null, palette.SpeedPen, line);

        foreach (double x in points)
        {
            if (x >= from - 4.0 && x <= to + 4.0)
            {
                dc.DrawRectangle(palette.SpeedPen.Brush, palette.ClipEdge, new Rect(x - 3.5, Y(Math.Clamp(x, from, to)) - 3.5, 7, 7));
            }
        }
    }

    /// <summary>
    /// A transition's bar: a pale band with the diagonal every editor marks one with, its name when
    /// it fits, and a warning stripe along the top when a clip is short of source for it.
    /// </summary>
    private void DrawTransition(DrawingContext dc, Rect band, TransitionView bar)
    {
        Palette palette = _palette!;
        dc.DrawRoundedRectangle(palette.Transition, palette.TransitionEdge, band, 2.0, 2.0);
        dc.DrawLine(palette.TransitionLine, band.BottomLeft, band.TopRight);

        if (bar.Holds)
        {
            dc.DrawRectangle(palette.Warning, null, new Rect(band.Left, band.Top, band.Width, 2.0));
        }

        if (band.Width > 48 && band.Height >= 12)
        {
            TextDrawing label = ClipLabel(bar.Name, palette.ClipText);
            double room = band.Width - (LabelPadding * 2);
            dc.PushClip(new RectangleGeometry(new Rect(band.Left + LabelPadding, band.Top, Math.Max(0, room), band.Height)));
            DrawTextAt(dc, label, band.Left + LabelPadding, band.Top + ((band.Height - ClipFontSize) / 2.0) - 2.0);
            dc.Pop();
        }
    }

    private static IEnumerable<ClipView> Visible(TrackView track, Flicks start, Flicks end)
    {
        // Clips on a track are in time order and do not overlap, so the first visible one is
        // found by bisection and the rest follow until one starts past the right edge.
        System.Collections.Immutable.ImmutableArray<ClipView> clips = track.Clips;
        int low = 0;
        int high = clips.Length;

        while (low < high)
        {
            int middle = (low + high) / 2;
            if (clips[middle].End <= start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (int index = low; index < clips.Length && clips[index].Start < end; index++)
        {
            yield return clips[index];
        }
    }

    private void DrawMarkers()
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        TimelineGeometry geometry = model.Geometry;
        double width = ActualWidth;

        using DrawingContext dc = _markers.RenderOpen();
        dc.DrawRectangle(palette.MarkerLane, null, new Rect(0, TimelineGeometry.RulerHeight, width, TimelineGeometry.MarkerLaneHeight));

        foreach (Marker marker in model.Content.Sequence.Markers)
        {
            double x = geometry.XOf(marker.Time);
            if (x < -200 || x > width + 4)
            {
                continue;
            }

            SolidColorBrush brush = TrackBrush(marker.Color);
            bool selected = model.Selected.Contains(marker.Id);
            double top = TimelineGeometry.RulerHeight + 2;
            double bottom = TimelineGeometry.TracksTop - 2;

            if (marker.Duration > Flicks.Zero)
            {
                double right = geometry.XOf(marker.Time + marker.Duration);
                dc.DrawRectangle(Faded(brush, 0.35), null, new Rect(x, top, Math.Max(1, right - x), bottom - top));
            }

            var flag = new StreamGeometry();
            using (StreamGeometryContext context = flag.Open())
            {
                context.BeginFigure(new Point(x, top), true, true);
                context.LineTo(new Point(x + 8, top), false, false);
                context.LineTo(new Point(x + 8, bottom - 4), false, false);
                context.LineTo(new Point(x, bottom), false, false);
            }

            flag.Freeze();
            dc.DrawGeometry(brush, selected ? palette.SelectionPen : null, flag);
            dc.DrawLine(palette.MarkerLine(brush), new Point(x + 0.5, TimelineGeometry.TracksTop), new Point(x + 0.5, ActualHeight));

            if (marker.Name.Length > 0)
            {
                DrawTextAt(dc, Text(marker.Name, _rulerLabels, palette.TextSecondary, 10.0), x + 11, top);
            }
        }
    }

    private void DrawRuler()
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        TimelineGeometry geometry = model.Geometry;
        double width = ActualWidth;
        Rational fps = geometry.FrameRate;

        using DrawingContext dc = _ruler.RenderOpen();
        dc.DrawRectangle(palette.Ruler, null, new Rect(0, 0, width, TimelineGeometry.RulerHeight));

        if (model.Content.Sequence.InOut is { } range)
        {
            double left = Math.Max(0, geometry.XOf(range.Start));
            double right = Math.Min(width, geometry.XOf(range.End));
            if (right > left)
            {
                dc.DrawRectangle(palette.InOutRuler, null, new Rect(left, TimelineGeometry.RulerHeight - 5, right - left, 5));
            }
        }

        (Flicks major, int minors) = RulerSpacing.For(geometry.PixelsPerSecond, fps);
        Flicks minor = major / minors;
        (Flicks start, Flicks end) = geometry.Visible(width);
        long first = start.Value / minor.Value;
        long last = (end.Value / minor.Value) + 1;

        for (long tick = first; tick <= last; tick++)
        {
            Flicks time = minor * tick;
            double x = Math.Round(geometry.XOf(time)) + 0.5;
            bool isMajor = tick % minors == 0;
            double length = isMajor ? 12.0 : 5.0;

            dc.DrawLine(palette.Tick, new Point(x, TimelineGeometry.RulerHeight - length), new Point(x, TimelineGeometry.RulerHeight));

            if (isMajor)
            {
                string text = Core.Time.Timecode.Format(time, fps);
                DrawTextAt(dc, Text(text, _rulerLabels, palette.TextSecondary, 10.0), x + 3, 2);
            }
        }

        dc.DrawLine(palette.RowLine, new Point(0, TimelineGeometry.RulerHeight - 0.5), new Point(width, TimelineGeometry.RulerHeight - 0.5));
    }

    private void DrawSelection()
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        TimelineGeometry geometry = model.Geometry;
        double width = ActualWidth;
        double height = ActualHeight;

        using DrawingContext dc = _selection.RenderOpen();
        dc.PushClip(new RectangleGeometry(new Rect(0, TimelineGeometry.TracksTop, width, Math.Max(0, height - TimelineGeometry.TracksTop))));

        // Clips another client just changed, outlined for a second so the person watching sees
        // where the remote edit landed. Drawn first, so the selection wins where both apply.
        foreach (string id in model.Flashing)
        {
            if (model.Content.Clip(id) is not { } changed || geometry.Row(changed.TrackId) is not { } changedRow)
            {
                continue;
            }

            double from = Math.Max(geometry.XOf(changed.Start), -4.0);
            double to = Math.Min(geometry.XOf(changed.End), width + 4.0);
            if (to >= 0 && from <= width)
            {
                double top = geometry.TopOf(changedRow) + ClipInset;
                dc.DrawRoundedRectangle(null, palette.RemotePen, new Rect(from + 1, top + 1, Math.Max(1, to - from - 3), Math.Max(1, changedRow.Height - (ClipInset * 2) - 2)), 3, 3);
            }
        }

        foreach (string id in model.Selected)
        {
            if (model.Content.Transition(id) is { } bar && geometry.Row(bar.TrackId) is { } barRow)
            {
                Rect band = geometry.TransitionBand(barRow, bar.Start, bar.End);
                dc.DrawRoundedRectangle(null, palette.SelectionPen, band, 2.0, 2.0);
                continue;
            }

            if (model.Content.Clip(id) is not { } clip || geometry.Row(clip.TrackId) is not { } row)
            {
                continue;
            }

            double left = Math.Max(geometry.XOf(clip.Start), -4.0);
            double right = Math.Min(geometry.XOf(clip.End), width + 4.0);
            if (right < 0 || left > width)
            {
                continue;
            }

            CueLanes lanes = model.Content.Track(clip.TrackId)?.Lanes ?? CueLanes.One;
            (double top, double tall) = lanes.Slice(clip.Clip.Id, geometry.TopOf(row) + ClipInset, Math.Max(1, row.Height - (ClipInset * 2)));
            dc.DrawRoundedRectangle(null, palette.SelectionPen, new Rect(left + 1, top + 1, Math.Max(1, right - left - 3), Math.Max(1, tall - 2)), 3, 3);
        }

        // An edit point picked with the ripple or roll tool: a bar on the edge the keys trim.
        if (model.SelectedEdit is { } edit)
        {
            IEnumerable<string> tracks = edit.Tool == TimelineTool.Roll
                ? edit.Rolls.Select(pair => pair.TrackId)
                : edit.ClipIds.Select(model.Content.Clip).OfType<ClipView>().Select(clip => clip.TrackId);

            double x = Math.Round(geometry.XOf(edit.Time)) + 0.5;
            double offset = edit.Tool == TimelineTool.Roll ? 0.0 : edit.Edge == ClipEdge.Start ? 2.0 : -2.0;

            foreach (string trackId in tracks)
            {
                if (geometry.Row(trackId) is { } row)
                {
                    double top = geometry.TopOf(row) + ClipInset;
                    dc.DrawLine(palette.EditPen, new Point(x + offset, top), new Point(x + offset, top + row.Height - (ClipInset * 2)));
                }
            }
        }

        dc.Pop();

        if (model.Box is { } box)
        {
            dc.DrawRectangle(palette.Box, palette.BoxPen, box);
        }
    }

    private void DrawGhost()
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        TimelineGeometry geometry = model.Geometry;

        using DrawingContext dc = _ghost.RenderOpen();

        // What a drag snapped to, or where the razor would cut, from the ruler down.
        if (model.Guide is { } guide)
        {
            double x = Math.Round(geometry.XOf(guide)) + 0.5;
            dc.DrawLine(palette.GuidePen, new Point(x, 0), new Point(x, ActualHeight));
        }

        if (model.Ghost is not { } ghost)
        {
            return;
        }

        Brush fill = ghost.Refused is null ? palette.Ghost : palette.Refused;

        foreach (GhostClip clip in ghost.Clips)
        {
            if (geometry.Row(clip.TrackId) is not { } row)
            {
                continue;
            }

            double left = geometry.XOf(clip.Start);
            double right = geometry.XOf(clip.End);
            double top = geometry.TopOf(row) + ClipInset;
            dc.DrawRoundedRectangle(fill, palette.GhostPen, new Rect(left, top, Math.Max(1, right - left), Math.Max(1, row.Height - (ClipInset * 2))), 3, 3);
        }
    }

    private void DrawPlayhead(Flicks playhead)
    {
        TimelineViewModel model = _model!;
        Palette palette = _palette!;
        double x = Math.Round(model.Geometry.XOf(playhead)) + 0.5;

        using DrawingContext dc = _playhead.RenderOpen();
        if (x < -8 || x > ActualWidth + 8)
        {
            return;
        }

        dc.DrawLine(palette.PlayheadPen, new Point(x, 0), new Point(x, ActualHeight));

        var head = new StreamGeometry();
        using (StreamGeometryContext context = head.Open())
        {
            context.BeginFigure(new Point(x - 6, 0), true, true);
            context.LineTo(new Point(x + 6, 0), false, false);
            context.LineTo(new Point(x + 6, 8), false, false);
            context.LineTo(new Point(x, 14), false, false);
            context.LineTo(new Point(x - 6, 8), false, false);
        }

        head.Freeze();
        dc.DrawGeometry(palette.Playhead, null, head);
    }

    /// <summary>Every brush and pen the timeline draws with, from the theme, frozen once.</summary>
    private sealed class Palette
    {
        private readonly Dictionary<Brush, Pen> _markerLines = [];

        public Palette(FrameworkElement owner)
        {
            Brush Find(string key, Color fallback) =>
                owner.TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));

            Background = Find("Brush.Background.Base", Color.FromRgb(0x1B, 0x1B, 0x1B));
            Lane = Find("Brush.Timeline.Lane", Color.FromRgb(0x1F, 0x1F, 0x1F));
            LaneAlt = Find("Brush.Timeline.LaneAlt", Color.FromRgb(0x23, 0x23, 0x23));
            Ruler = Find("Brush.Timeline.Ruler", Color.FromRgb(0x26, 0x26, 0x26));
            MarkerLane = Find("Brush.Background.Panel", Color.FromRgb(0x22, 0x22, 0x22));
            InOut = Find("Brush.Timeline.InOut", Color.FromArgb(0x26, 0x4C, 0x9A, 0xFF));
            InOutRuler = Faded(Find("Brush.Accent", Color.FromRgb(0x4C, 0x9A, 0xFF)), 0.8);
            Playhead = Find("Brush.Timeline.Playhead", Color.FromRgb(0xE0, 0x5C, 0x5C));
            Box = Find("Brush.Timeline.Box", Color.FromArgb(0x33, 0x4C, 0x9A, 0xFF));
            Ghost = Find("Brush.Timeline.Ghost", Color.FromArgb(0x66, 0x4C, 0x9A, 0xFF));
            Refused = Find("Brush.Timeline.Refused", Color.FromArgb(0x66, 0xE0, 0x5C, 0x5C));
            ClipText = Find("Brush.Timeline.ClipText", Color.FromRgb(0xF2, 0xF2, 0xF2));
            TextSecondary = Find("Brush.Text.Secondary", Color.FromRgb(0x9A, 0x9A, 0x9A));
            Missing = Faded(Find("Brush.Error", Color.FromRgb(0xE0, 0x5C, 0x5C)), 0.55);
            Disabled = Find("Brush.Border.Strong", Color.FromRgb(0x4C, 0x4C, 0x4C));
            LinkMark = Faded(Find("Brush.Text.Primary", Color.FromRgb(0xE6, 0xE6, 0xE6)), 0.7);
            Waveform = Faded(Find("Brush.Text.Primary", Color.FromRgb(0xE6, 0xE6, 0xE6)), 0.45);
            WaveformPlaceholder = Frozen(new Pen(Faded(Find("Brush.Text.Primary", Color.FromRgb(0xE6, 0xE6, 0xE6)), 0.25), 1.0));

            ClipEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0))), 1.0));
            RowLine = Frozen(new Pen(Find("Brush.Border", Color.FromRgb(0x3A, 0x3A, 0x3A)), 1.0));
            Tick = Frozen(new Pen(Find("Brush.Timeline.Tick", Color.FromRgb(0x5A, 0x5A, 0x5A)), 1.0));
            SelectionPen = Frozen(new Pen(Find("Brush.Timeline.Selection", Color.FromRgb(0xF2, 0xF2, 0xF2)), 2.0));
            RemotePen = Frozen(new Pen(Find("Brush.Timeline.Remote", Color.FromRgb(0xFF, 0xB8, 0x4C)), 2.0));
            BoxPen = Frozen(new Pen(Find("Brush.Accent", Color.FromRgb(0x4C, 0x9A, 0xFF)), 1.0));
            GhostPen = Frozen(new Pen(Find("Brush.Text.Primary", Color.FromRgb(0xE6, 0xE6, 0xE6)), 1.0) { DashStyle = DashStyles.Dash });
            PlayheadPen = Frozen(new Pen(Playhead, 1.0));
            GuidePen = Frozen(new Pen(Find("Brush.Warning", Color.FromRgb(0xF2, 0xC1, 0x4E)), 1.0));
            EditPen = Frozen(new Pen(Find("Brush.Accent", Color.FromRgb(0x4C, 0x9A, 0xFF)), 4.0));
            Transition = Faded(Find("Brush.Timeline.Transition", Color.FromRgb(0xD8, 0xD8, 0xE8)), 0.55);
            TransitionEdge = Frozen(new Pen(Faded(Find("Brush.Text.Primary", Color.FromRgb(0xE6, 0xE6, 0xE6)), 0.8), 1.0));
            TransitionLine = Frozen(new Pen(Faded(Find("Brush.Background.Base", Color.FromRgb(0x1B, 0x1B, 0x1B)), 0.45), 1.0));
            Warning = Find("Brush.Warning", Color.FromRgb(0xF2, 0xC1, 0x4E));
            VolumePen = Frozen(new Pen(Find("Brush.Label.Yellow", Color.FromRgb(0xE8, 0xC5, 0x47)), 1.25));
            SpeedPen = Frozen(new Pen(Find("Brush.Accent", Color.FromRgb(0x4C, 0x8D, 0xFF)), 1.5));
            SpeedEdge = Frozen(new Pen(Faded(Find("Brush.Background.Base", Color.FromRgb(0x1B, 0x1B, 0x1B)), 0.8), 5.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            VolumeHandle = Find("Brush.Label.Yellow", Color.FromRgb(0xE8, 0xC5, 0x47));
            FadeShade = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)));
            FadePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))), 1.0));
            FadeHandle = Find("Brush.Text.Primary", Color.FromRgb(0xE8, 0xE9, 0xEC));
        }

        public Brush Background { get; }

        public Brush Lane { get; }

        public Brush LaneAlt { get; }

        public Brush Ruler { get; }

        public Brush MarkerLane { get; }

        public Brush InOut { get; }

        public Brush InOutRuler { get; }

        public Brush Playhead { get; }

        public Brush Box { get; }

        public Brush Ghost { get; }

        public Brush Refused { get; }

        public Pen GuidePen { get; }

        public Pen EditPen { get; }

        public Brush Transition { get; }

        public Pen TransitionEdge { get; }

        public Pen TransitionLine { get; }

        public Brush Warning { get; }

        public Brush ClipText { get; }

        public Brush TextSecondary { get; }

        public Brush Missing { get; }

        public Brush Disabled { get; }

        public Brush LinkMark { get; }

        public Brush Waveform { get; }

        public Pen WaveformPlaceholder { get; }

        public Pen VolumePen { get; }

        /// <summary>The speed lane's line (Phase 45).</summary>
        public Pen SpeedPen { get; }

        /// <summary>A dark edge under the speed line, so it shows over any picture.</summary>
        public Pen SpeedEdge { get; }

        public Brush VolumeHandle { get; }

        /// <summary>Over the part of a sound clip a fade takes.</summary>
        public Brush FadeShade { get; }

        /// <summary>A fade's ramp.</summary>
        public Pen FadePen { get; }

        /// <summary>A fade's handle.</summary>
        public Brush FadeHandle { get; }

        public Pen ClipEdge { get; }

        public Pen RowLine { get; }

        public Pen Tick { get; }

        public Pen SelectionPen { get; }

        public Pen RemotePen { get; }

        public Pen BoxPen { get; }

        public Pen GhostPen { get; }

        public Pen PlayheadPen { get; }

        public Pen MarkerLine(Brush brush)
        {
            if (!_markerLines.TryGetValue(brush, out Pen? pen))
            {
                pen = Frozen(new Pen(Faded(brush, 0.4), 1.0));
                _markerLines[brush] = pen;
            }

            return pen;
        }
    }
}

/// <summary>How far apart the ruler's ticks are at a zoom.</summary>
public static class RulerSpacing
{
    /// <summary>The narrowest the gap between labelled ticks may get, in pixels.</summary>
    public const double MinimumMajorPixels = 90.0;

    /// <summary>
    /// The major tick interval and how many minor ticks divide it: the shortest round interval
    /// (a frame, a few frames, seconds, minutes) whose labels do not collide.
    /// </summary>
    public static (Flicks Major, int Minors) For(double pixelsPerSecond, Rational fps)
    {
        Flicks frame = Flicks.FromFrames(1, fps);
        long framesPerSecond = Math.Max(1, (long)Math.Round(fps.ToDouble()));

        (Flicks Interval, int Minors)[] candidates =
        [
            (frame, 1),
            (frame * 2, 2),
            (frame * 5, 5),
            (frame * 10, 10),
            (Flicks.FromFrames(framesPerSecond / 2 is > 0 and var half ? half : 1, fps), 5),
            (Flicks.OneSecond, 5),
            (Flicks.OneSecond * 2, 4),
            (Flicks.OneSecond * 5, 5),
            (Flicks.OneSecond * 10, 10),
            (Flicks.OneSecond * 15, 3),
            (Flicks.OneSecond * 30, 6),
            (Flicks.OneSecond * 60, 6),
            (Flicks.OneSecond * 120, 4),
            (Flicks.OneSecond * 300, 5),
            (Flicks.OneSecond * 600, 10),
            (Flicks.OneSecond * 1800, 6),
            (Flicks.OneSecond * 3600, 6),
        ];

        foreach ((Flicks interval, int minors) in candidates)
        {
            if (interval.ToSeconds() * pixelsPerSecond >= MinimumMajorPixels)
            {
                return (interval, minors);
            }
        }

        return (Flicks.OneSecond * 7200, 4);
    }
}

/// <summary>Text recorded once, and how wide it is.</summary>
/// <param name="Drawing">The glyphs, at the origin, frozen.</param>
/// <param name="Width">The advance width.</param>
internal sealed record TextDrawing(Drawing Drawing, double Width);
