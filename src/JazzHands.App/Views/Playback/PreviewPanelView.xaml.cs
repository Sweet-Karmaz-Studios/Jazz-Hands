using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JazzHands.App.Controls.Preview;
using JazzHands.App.ViewModels.Playback;
using JazzHands.Render.Passes;

namespace JazzHands.App.Views.Playback;

/// <summary>
/// The preview panel's view: hosts the D3DImage and keeps its back buffer the size of the stage.
/// </summary>
/// <remarks>
/// The presenter lives exactly as long as the view is loaded, because AvalonDock unloads a panel
/// when it is floated or docked somewhere else and loads it again after. Resizing is debounced to
/// 100 ms, as the d3d-wpf-interop skill asks: dragging a splitter otherwise reallocates two 4K
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

        _model = model;

        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
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
        if (_presenter is not null || _model?.Engine.Device is not { } device)
        {
            return;
        }

        _presenter = new PreviewPresenter(device);
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

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
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
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the picture takes keyboard focus away from whatever text box had it, so the
        // playback keys work.
        Stage.Focus();

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

        if (e.ChangedButton == MouseButton.Middle && _model is { Zoom: not PreviewZoom.Fit })
        {
            _panFrom = e.GetPosition(Stage);
            Stage.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
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
        if (_panFrom is not null && e.ChangedButton == MouseButton.Middle)
        {
            _panFrom = null;
            Stage.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
