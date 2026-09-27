using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JazzHands.App.Controls.Preview;
using JazzHands.App.Services;
using JazzHands.App.ViewModels.Playback;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;

namespace JazzHands.App.Views.Playback;

/// <summary>
/// The source monitor (Phase 38): the source player's frames in a presenter like the program
/// preview's, a scrub bar, and a drag of the marked stretch to the timeline or the program monitor.
/// </summary>
public partial class SourcePanelView : UserControl
{
    private readonly DispatcherTimer _resize;
    private SourcePanelViewModel? _model;
    private PreviewPresenter? _presenter;
    private Point? _dragFrom;

    /// <summary>Creates the view.</summary>
    public SourcePanelView()
    {
        InitializeComponent();

        _resize = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            ApplySize();
        };

        DataContextChanged += (_, _) => Bind(DataContext as SourcePanelViewModel);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Stage.SizeChanged += (_, _) =>
        {
            _resize.Stop();
            _resize.Start();
        };
        Scrub.SizeChanged += (_, _) => PlaceMarks();

        Stage.MouseLeftButtonDown += (_, e) =>
        {
            Stage.Focus();
            _dragFrom = e.GetPosition(Stage);
        };
        Stage.MouseMove += OnStageMouseMove;
        Stage.MouseLeftButtonUp += (_, _) => _dragFrom = null;

        Scrub.MouseLeftButtonDown += (_, e) =>
        {
            Scrub.CaptureMouse();
            ScrubTo(e.GetPosition(Scrub).X);
        };
        Scrub.MouseMove += (_, e) =>
        {
            if (Scrub.IsMouseCaptured)
            {
                ScrubTo(e.GetPosition(Scrub).X);
            }
        };
        Scrub.MouseLeftButtonUp += (_, _) => Scrub.ReleaseMouseCapture();
    }

    private void Bind(SourcePanelViewModel? model)
    {
        _model?.PropertyChanged -= OnModelChanged;
        _model = model;
        _model?.PropertyChanged += OnModelChanged;
        PlaceMarks();

        if (IsLoaded)
        {
            Detach();
            Attach();
        }
    }

    private void Attach()
    {
        if (_presenter is not null || _model is null || _model.IsSuspended || _model.Screen?.Device is not { } device)
        {
            return;
        }

        _presenter = new PreviewPresenter(device, () => _model?.Display ?? JazzHands.Render.Color.DisplayTransfer.Srgb);
        _presenter.Surface.SurfaceRecreated += OnSurfaceRecreated;
        Picture.Source = _presenter.Surface.Image;
        ApplySize();
        _model.Screen.AddTarget(_presenter);
    }

    private void Detach()
    {
        _resize.Stop();
        if (_presenter is null)
        {
            return;
        }

        _model?.Screen?.RemoveTarget(_presenter);
        _presenter.Surface.SurfaceRecreated -= OnSurfaceRecreated;
        Picture.Source = null;
        _presenter.Dispose();
        _presenter = null;
    }

    private void OnSurfaceRecreated(object? sender, EventArgs e) => _model?.Screen?.Refresh();

    private void ApplySize()
    {
        if (_presenter is null || Stage.ActualWidth < 1 || Stage.ActualHeight < 1)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _presenter.Resize((int)Math.Round(Stage.ActualWidth * dpi.DpiScaleX), (int)Math.Round(Stage.ActualHeight * dpi.DpiScaleY));
        _model?.Screen?.Refresh();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SourcePanelViewModel.IsSuspended):
                if (_model?.IsSuspended == true)
                {
                    Detach();
                }
                else if (IsLoaded)
                {
                    Attach();
                }

                break;

            case nameof(SourcePanelViewModel.InFraction) or nameof(SourcePanelViewModel.OutFraction) or nameof(SourcePanelViewModel.HasSource):
                PlaceMarks();
                break;
        }
    }

    /// <summary>Lights the marked stretch on the scrub bar: from the in (or the start) to the out (or the end).</summary>
    private void PlaceMarks()
    {
        if (_model is null || !_model.HasSource || (_model.InFraction is null && _model.OutFraction is null))
        {
            Marked.Width = 0;
            return;
        }

        double from = (_model.InFraction ?? 0) * Scrub.ActualWidth;
        double to = (_model.OutFraction ?? 1) * Scrub.ActualWidth;
        Canvas.SetLeft(Marked, from);
        Marked.Width = Math.Max(0, to - from);
    }

    private void ScrubTo(double x)
    {
        if (_model is not null && Scrub.ActualWidth > 0)
        {
            _ = _model.ScrubAsync(x / Scrub.ActualWidth);
        }
    }

    /// <summary>A drag from the picture carries the marked stretch, as a drag from the Media panel carries a file.</summary>
    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed || _model?.Item is not MediaItem item)
        {
            return;
        }

        Vector moved = e.GetPosition(Stage) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragFrom = null;
        (Core.Time.Flicks start, Core.Time.Flicks end) = _model.MarkedRange;
        string path = ProjectPaths.Resolve(_model.ProjectPath, item.RelativePath);
        DragDrop.DoDragDrop(Stage, MediaDragData.Create(item.Id, start, end, path), DragDropEffects.Copy);
    }
}
