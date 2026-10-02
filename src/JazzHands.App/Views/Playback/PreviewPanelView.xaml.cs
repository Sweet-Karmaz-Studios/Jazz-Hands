using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JazzHands.App.Controls.Preview;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Playback;
using JazzHands.Render.Passes;

namespace JazzHands.App.Views.Playback;

/// <summary>
/// The preview panel's view: hosts the D3DImage and keeps its back buffer the size of the stage.
/// </summary>
/// <remarks>
/// The presenter lives exactly as long as the view is loaded, because AvalonDock unloads a panel
/// when it is floated or docked somewhere else and loads it again after. Resizing is debounced to
/// 100 ms: dragging a splitter otherwise reallocates two 4K
/// textures per mouse move.
/// </remarks>
public partial class PreviewPanelView : UserControl
{
    private readonly DispatcherTimer _resize;
    private PreviewPanelViewModel? _model;
    private PreviewPresenter? _presenter;
    private Point? _panFrom;
    private double _panX;
    private double _panY;

    /// <summary>Creates the view.</summary>
    public PreviewPanelView()
    {
        InitializeComponent();

        _resize = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            ApplySize();
        };

        DataContextChanged += (_, _) => Bind(DataContext as PreviewPanelViewModel);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();

        Stage.SizeChanged += (_, _) =>
        {
            UpdateOverlay();
            _resize.Stop();
            _resize.Start();
        };

        Stage.MouseDown += OnStageMouseDown;
        Stage.MouseMove += OnStageMouseMove;
        Stage.MouseUp += OnStageMouseUp;
        Stage.PreviewKeyDown += OnStageKeyDown;

        TitleEditor.PreviewKeyDown += OnTitleEditorKeyDown;
        TitleEditor.LostKeyboardFocus += (_, _) =>
        {
            if (_model?.Titles is { IsEditingText: true } titles)
            {
                _ = titles.CommitTextAsync();
            }
        };
    }

    /// <summary>The presenter while the view is loaded, for tests.</summary>
    internal PreviewPresenter? Presenter => _presenter;

    /// <summary>The magnification a zoom setting means, in screen pixels per sequence pixel. Zero fits.</summary>
    internal static double Magnification(PreviewZoom zoom) => zoom switch
    {
        PreviewZoom.Actual => 1.0,
        PreviewZoom.Double => 2.0,
        _ => 0.0,
    };

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplySize();
    }

    private void Bind(PreviewPanelViewModel? model)
    {
        _model?.PropertyChanged -= OnModelChanged;
        _model?.Titles?.PropertyChanged -= OnTitlesChanged;

        _model = model;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
            _model.Titles?.PropertyChanged += OnTitlesChanged;
            NoDevice.Visibility = _model.Engine.Device is null ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateOverlay();

        // A templated view can be loaded before its data context arrives, and then Loaded has
        // already come and gone with nothing to attach to.
        if (IsLoaded)
        {
            Detach();
            Attach();
        }
    }

    private void Attach()
    {
        if (_presenter is not null || _model is null || _model.IsSuspended || _model.Engine.Device is not { } device)
        {
            return;
        }

        _presenter = new PreviewPresenter(device, () => _model?.Display ?? JazzHands.Render.Color.DisplayTransfer.Srgb);
        _presenter.Surface.SurfaceRecreated += OnSurfaceRecreated;
        Picture.Source = _presenter.Surface.Image;

        ApplySize();
        _model.Engine.AddTarget(_presenter);
    }

    private void Detach()
    {
        _resize.Stop();

        if (_presenter is null)
        {
            return;
        }

        _model?.Engine.RemoveTarget(_presenter);
        _presenter.Surface.SurfaceRecreated -= OnSurfaceRecreated;
        Picture.Source = null;
        _presenter.Dispose();
        _presenter = null;
    }

    private void OnSurfaceRecreated(object? sender, EventArgs e) => _model?.Engine.Refresh();

    /// <summary>Media, or a stretch from the source monitor, dragged over the picture overwrites at the playhead when dropped.</summary>
    private void OnStageDragOver(object sender, DragEventArgs e)
    {
        e.Effects = MediaDragData.Ids(e.Data).Count == 1 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnStageDrop(object sender, DragEventArgs e)
    {
        if (_model is null || MediaDragData.Ids(e.Data) is not [var mediaId])
        {
            return;
        }

        e.Handled = true;
        var range = MediaDragData.Range(e.Data);
        _ = _model.OverwriteAtPlayheadAsync(mediaId, range?.In, range?.Out);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewPanelViewModel.IsSuspended):
                // Hidden: the presenter and its shared surfaces go, and come back at the next show.
                if (_model?.IsSuspended == true)
                {
                    Detach();
                }
                else if (IsLoaded)
                {
                    Attach();
                }

                break;

            case nameof(PreviewPanelViewModel.Zoom):
                _panX = 0;
                _panY = 0;
                ApplyZoom();
                break;

            case nameof(PreviewPanelViewModel.SequenceWidth) or nameof(PreviewPanelViewModel.SequenceHeight):
                UpdateOverlay();
                break;
        }
    }

    /// <summary>Makes the back buffer the stage's size in device pixels, then redraws.</summary>
    private void ApplySize()
    {
        if (_presenter is null || Stage.ActualWidth < 1 || Stage.ActualHeight < 1)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(Stage.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(Stage.ActualHeight * dpi.DpiScaleY);

        _presenter.Resize(width, height);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_model is null)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _presenter?.SetZoom(Magnification(_model.Zoom), _panX * dpi.DpiScaleX, _panY * dpi.DpiScaleY);
        UpdateOverlay();
        _model.Engine.Refresh();
    }

    /// <summary>Puts the overlay's rectangle where the presenter will draw the picture.</summary>
    private void UpdateOverlay()
    {
        if (_model is null || Stage.ActualWidth < 1 || Stage.ActualHeight < 1)
        {
            Overlay.Picture = Rect.Empty;
            TitleFrame.Picture = Rect.Empty;
            MaskFrame.Picture = Rect.Empty;
            GizmoFrame.Picture = Rect.Empty;
            return;
        }

        // The same arithmetic the presenter does, in device pixels, turned back into this
        // element's units, so the guides land on the picture at any zoom and any DPI.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Round(Stage.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(Stage.ActualHeight * dpi.DpiScaleY));
        double magnification = Magnification(_model.Zoom);

        QuadRect placement = magnification <= 0
            ? QuadRect.Fit(_model.SequenceWidth, _model.SequenceHeight, width, height)
            : QuadRect.Zoom(_model.SequenceWidth, _model.SequenceHeight, width, height, magnification, _panX * dpi.DpiScaleX, _panY * dpi.DpiScaleY);

        Overlay.Picture = new Rect(
            placement.Left * Stage.ActualWidth,
            placement.Top * Stage.ActualHeight,
            placement.Width * Stage.ActualWidth,
            placement.Height * Stage.ActualHeight);
        Overlay.SequenceSize = new Size(_model.SequenceWidth, _model.SequenceHeight);
        TitleFrame.Picture = Overlay.Picture;
        TitleFrame.SequenceSize = Overlay.SequenceSize;
        MaskFrame.Picture = Overlay.Picture;
        MaskFrame.SequenceSize = Overlay.SequenceSize;
        GizmoFrame.Picture = Overlay.Picture;
        GizmoFrame.SequenceSize = Overlay.SequenceSize;
        PlaceTitleEditor();
    }

    /// <summary>How near a grip counts as on it: seven screen units, in sequence pixels.</summary>
    private float GripTolerance() =>
        (float)((TitleHandlesOverlay.Grip + 2.5) * Overlay.SequenceSize.Width / Math.Max(1.0, Overlay.Picture.Width));

    /// <summary>Puts the text box over the title while its text is being edited, or hides it.</summary>
    private void PlaceTitleEditor()
    {
        if (_model?.Titles is not { IsEditingText: true, Corners: { Count: 4 } corners } || Overlay.Picture.IsEmpty)
        {
            TitleEditor.Visibility = Visibility.Collapsed;
            return;
        }

        Rect over = TitleHandlesOverlay.Bounds(corners, Overlay.Picture, Overlay.SequenceSize);
        TitleEditor.Margin = new Thickness(Math.Max(0, over.X), Math.Max(0, over.Y), 0, 0);
        TitleEditor.Width = Math.Max(TitleEditor.MinWidth, over.Width);
        TitleEditor.MinHeight = Math.Max(24, over.Height);
        TitleEditor.Visibility = Visibility.Visible;
    }

    private void OnTitlesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TitleHandlesViewModel.IsEditingText) or nameof(TitleHandlesViewModel.Corners)))
        {
            return;
        }

        bool opening = e.PropertyName == nameof(TitleHandlesViewModel.IsEditingText) && _model?.Titles?.IsEditingText == true;
        PlaceTitleEditor();
        if (opening)
        {
            TitleEditor.Focus();
            TitleEditor.SelectAll();
        }
    }

    private void OnTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (_model?.Titles is not { } titles)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            titles.CancelTextEdit();
            Stage.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _ = titles.CommitTextAsync();
            Stage.Focus();
            e.Handled = true;
        }
    }

    /// <summary>While a mask is being drawn, Esc drops it and Enter closes it.</summary>
    private void OnStageKeyDown(object sender, KeyEventArgs e)
    {
        if (_model?.MaskHandles is not { IsDrawing: true } masks)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            masks.CancelDrawing();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            masks.Close();
            e.Handled = true;
        }
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the picture takes keyboard focus away from whatever text box had it, so the
        // playback keys work.
        Stage.Focus();

        // In a multicam grid, a click on an angle cuts to it (Phase 41).
        if (e.ChangedButton == MouseButton.Left && _model is { IsMulticamView: true } multicam && !Overlay.Picture.IsEmpty)
        {
            Point at = e.GetPosition(Stage);
            if (Overlay.Picture.Contains(at)
                && multicam.ClickGrid((at.X - Overlay.Picture.X) / Overlay.Picture.Width, (at.Y - Overlay.Picture.Y) / Overlay.Picture.Height))
            {
                e.Handled = true;
                return;
            }
        }

        // While the inspector is picking a point, a click on the picture is that point.
        if (_model is { IsPicking: true } picking)
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                picking.CancelPick();
            }
            else if (e.ChangedButton == MouseButton.Left
                && Controls.Preview.PreviewOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize) is { } point)
            {
                picking.PickAt(point);
            }

            e.Handled = true;
            return;
        }

        // The selected 3D clip's handle (Phase 49a): a press on an arrow, the square, the ring or a knob drags it.
        if (e.ChangedButton == MouseButton.Left && _model?.Gizmo is { IsShown: true } gizmo && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            Gizmo3DGrip grip = gizmo.HitTest(at, GripTolerance());
            if (grip != Gizmo3DGrip.None && gizmo.Begin(grip, at))
            {
                Stage.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // The masks: a drawing tool takes every press; with the select tool a press on a mask drags it.
        if (e.ChangedButton == MouseButton.Left && _model?.MaskHandles is { } masks && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            if (masks.Press(at, e.ClickCount, GripTolerance()))
            {
                Stage.CaptureMouse();
                e.Handled = true;
                return;
            }

            MaskGrip grip = masks.HitTest(at, GripTolerance());
            if (grip.Kind != MaskGripKind.None && masks.Begin(grip, at))
            {
                Stage.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // The selected title's frame: a double-click inside edits the text, a press on it drags.
        if (e.ChangedButton == MouseButton.Left && _model?.Titles is { } titles && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            TitleGrip grip = titles.HitTest(at, GripTolerance());
            if (grip == TitleGrip.Move && e.ClickCount == 2)
            {
                titles.BeginTextEdit();
                e.Handled = true;
                return;
            }

            if (grip != TitleGrip.None && titles.Begin(grip, at))
            {
                Stage.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton == MouseButton.Middle && _model is { Zoom: not PreviewZoom.Fit })
        {
            _panFrom = e.GetPosition(Stage);
            Stage.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_model?.Gizmo is { IsShown: true } gizmo && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            if (gizmo.IsDragging)
            {
                gizmo.Move(at, snap: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                return;
            }

            Gizmo3DGrip over = gizmo.HitTest(at, GripTolerance());
            if (over != Gizmo3DGrip.None)
            {
                Stage.Cursor = over switch
                {
                    Gizmo3DGrip.Move => Cursors.SizeAll,
                    Gizmo3DGrip.TurnX => Cursors.SizeNS,
                    Gizmo3DGrip.TurnY => Cursors.SizeWE,
                    _ => Cursors.Hand,
                };
                return;
            }
        }

        if (_model?.MaskHandles is { } masks && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            if (masks.IsDragging)
            {
                masks.Move(at, broken: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                return;
            }

            if (masks.IsDrawing && e.LeftButton == MouseButtonState.Pressed)
            {
                masks.Pull(at);
                return;
            }

            if (masks.Tool != MaskTool.Select)
            {
                Stage.Cursor = Cursors.Cross;
                return;
            }

            MaskGrip grip = masks.HitTest(at, GripTolerance());
            if (grip.Kind != MaskGripKind.None)
            {
                Stage.Cursor = grip.Kind switch
                {
                    MaskGripKind.Body => Cursors.SizeAll,
                    MaskGripKind.Feather => Cursors.SizeNS,
                    _ => Cursors.Hand,
                };
                return;
            }
        }

        if (_model?.Titles is { } titles && !Overlay.Picture.IsEmpty)
        {
            System.Numerics.Vector2 at = TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize);
            if (titles.IsDragging)
            {
                titles.Move(at, snap: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                return;
            }

            Stage.Cursor = titles.HitTest(at, GripTolerance()) switch
            {
                TitleGrip.Move => Cursors.SizeAll,
                TitleGrip.Scale => Cursors.SizeNWSE,
                TitleGrip.Rotate => Cursors.Hand,
                _ => null,
            };
        }

        if (_panFrom is not { } from)
        {
            return;
        }

        Point here = e.GetPosition(Stage);
        _panX += here.X - from.X;
        _panY += here.Y - from.Y;
        _panFrom = here;
        ApplyZoom();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _model?.Gizmo is { IsDragging: true } gizmo)
        {
            gizmo.End();
            Stage.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _model?.MaskHandles is { } masks && (masks.IsDragging || masks.IsDrawing))
        {
            masks.End(TitleHandlesOverlay.ToSequence(e.GetPosition(Stage), Overlay.Picture, Overlay.SequenceSize));
            Stage.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _model?.Titles is { IsDragging: true } titles)
        {
            titles.End();
            Stage.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_panFrom is not null && e.ChangedButton == MouseButton.Middle)
        {
            _panFrom = null;
            Stage.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
