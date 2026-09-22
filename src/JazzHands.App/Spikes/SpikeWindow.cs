using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JazzHands.App.Controls.Preview;
using JazzHands.Render;
using Serilog;

namespace JazzHands.App.Spikes;

/// <summary>
/// Spike S1. Renders a moving 4K test pattern on a background thread and presents it in WPF,
/// measuring presented frame rate, UI thread cost and recovery. Throwaway: it exists to produce
/// the numbers in Docs/SPIKES.md, and it is compiled only when JazzSpikes is on.
/// </summary>
internal sealed class SpikeWindow : Window
{
    private readonly ILogger _log = Log.ForContext<SpikeWindow>();
    private readonly SpikeOptions _options;
    private readonly RenderDevice _device;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new()
    {
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        Padding = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private readonly ManualResetEventSlim _frameReady = new(false);
    private readonly ManualResetEventSlim _framePresented = new(true);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Sampler _renderMs = new(200_000);
    private readonly Sampler _gpuWaitMs = new(200_000);
    private readonly Sampler _presentMs = new(200_000);
    private readonly Sampler _lockMs = new(200_000);
    private readonly Sampler _unlockMs = new(200_000);
    private readonly List<double> _dpiScales = [];
    private readonly List<string> _notes = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private PreviewSurface? _surface;
    private SpikeRenderer? _renderer;
    private WriteableBitmapPresenter? _fallback;
    private SwapChainPreview? _swapChain;
    private Thread? _renderThread;
    private TimerResolution? _timerResolution;
    private DispatcherTimer? _statusTimer;

    private long _renderedFrames;
    private long _presentedFrames;
    private long _presentedOffThread;
    private bool _deferredSwapChainBind;
    private long _compositionTicks;
    private long _measureStartRendered;
    private long _measureStartPresented;
    private double _measureStartSeconds;
    private TimeSpan _measureStartUiCpu;
    private TimeSpan _measureStartProcessCpu;
    private long _measureStartAllocated;
    private readonly int[] _measureStartCollections = new int[3];
    private bool _measuring;
    private int _frontBufferLossEvents;
    private double _lastRecoveryMs;
    private int _resizeEvents;
    private int _pendingWidth;
    private int _pendingHeight;
    private volatile bool _surfaceDirty;
    private bool _rebuildTriggered;
    private double _lastSweepSeconds;
    private int _sweepStep;
    private bool _reportWritten;

    /// <summary>Creates the spike window for the given options.</summary>
    public SpikeWindow(SpikeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _device = RenderDevice.Create();

        Title = $"Jazz Hands spike S1: {options.Mode}";
        Width = options.WindowWidth > 0 ? options.WindowWidth : 1600;
        Height = options.WindowHeight > 0 ? options.WindowHeight : 900;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (options.Position is { } position)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = position.X;
            Top = position.Y;
        }

        Topmost = options.Topmost;
        if (options.Maximize)
        {
            WindowState = WindowState.Maximized;
        }

        // A 4K surface shown smaller is scaled by WPF; ask for the cheap filter so the measurement
        // is about the bridge and not about WPF resampling.
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);

        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(_status);
        Content = root;

        _pendingWidth = options.Width;
        _pendingHeight = options.Height;

        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
        DpiChanged += OnDpiChanged;
        SizeChanged += OnSizeChanged;
    }

    private double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dpiScales.Add(VisualTreeHelper.GetDpi(this).DpiScaleX);
        _log.Information(
            "Spike S1 starting: mode {Mode}, {Width}x{Height} at {Fps} fps on {Adapter}, WPF render tier {Tier}",
            _options.Mode,
            _options.Width,
            _options.Height,
            _options.TargetFps,
            _device.AdapterName,
            RenderCapability.Tier >> 16);

        if (_options.Mode == SpikeMode.D3DImage)
        {
            _surface = new PreviewSurface(_device);
            _surface.SurfaceRecreated += OnSurfaceRecreated;
            _surface.Resize(_options.Width, _options.Height);
            _image.Source = _surface.Image;

            _renderer = new SpikeRenderer(_device) { RebindTargetEachFrame = !_options.NoRebind };
            _renderer.SetTarget(_surface.Texture!, _options.Width, _options.Height);
        }
        else if (_options.Mode == SpikeMode.SwapChain)
        {
            _swapChain = new SwapChainPreview(_device);
            ((Grid)Content).Children.Insert(0, _swapChain);
            _image.Visibility = Visibility.Collapsed;
            _swapChain.UpdateLayout();
            _renderer = new SpikeRenderer(_device);
            _deferredSwapChainBind = true;
            _notes.Add($"Offscreen render size {_options.Width}x{_options.Height}, scaled into the swap chain.");
        }
        else
        {
            _fallback = new WriteableBitmapPresenter(_device, _options.Width, _options.Height);
            _image.Source = _fallback.Bitmap;

            _renderer = new SpikeRenderer(_device);
            _renderer.SetTarget(_fallback.Texture, _options.Width, _options.Height);
        }

        CompositionTarget.Rendering += OnCompositionRendering;

        if (!_options.NoStatus)
        {
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();
        }
        else
        {
            _status.Visibility = Visibility.Collapsed;
        }

        if (_options.Topmost)
        {
            Activate();
        }

        _timerResolution = new TimerResolution();
        StartRenderThread();
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(RenderLoop)
        {
            Name = "jazz-spike-render",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        double frameSeconds = 1.0 / Math.Max(1, _options.TargetFps);
        var frameTimer = Stopwatch.StartNew();
        double nextDeadline = 0;
        var stepTimer = new Stopwatch();

        while (!_shutdown.IsCancellationRequested)
        {
            // Pace to the target rate: sleep the bulk of the wait, spin the last millisecond so
            // the cadence does not drift with the 15 ms scheduler tick.
            double now = frameTimer.Elapsed.TotalSeconds;
            if (now < nextDeadline)
            {
                double remaining = nextDeadline - now;
                if (remaining > 0.002)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(remaining - 0.002));
                }

                var spin = new SpinWait();
                while (frameTimer.Elapsed.TotalSeconds < nextDeadline && !_shutdown.IsCancellationRequested)
                {
                    spin.SpinOnce();
                }
            }

            nextDeadline = Math.Max(nextDeadline + frameSeconds, frameTimer.Elapsed.TotalSeconds);

            // Wait for WPF to finish reading the previous frame; there is no keyed mutex here.
            // The swap chain path presents inline on this thread, so it needs no handshake.
            bool needsHandshake = _options.Mode != SpikeMode.SwapChain && !_options.NoHandshake;
            if (needsHandshake && !_framePresented.Wait(100))
            {
                continue;
            }

            if (_shutdown.IsCancellationRequested)
            {
                break;
            }

            if (_surfaceDirty)
            {
                continue;
            }

            if (_deferredSwapChainBind)
            {
                continue;
            }

            stepTimer.Restart();
            _renderer?.Render(_renderedFrames);
            double drawMs = stepTimer.Elapsed.TotalMilliseconds;

            stepTimer.Restart();
            switch (_options.Mode)
            {
                case SpikeMode.D3DImage:
                    _surface?.WaitForRenderCompletion();
                    break;

                case SpikeMode.WriteableBitmap:
                    _fallback?.ReadBack();
                    break;

                case SpikeMode.SwapChain:
                    // Present happens right here on the render thread; WPF is not involved.
                    if (_swapChain?.Present() == true)
                    {
                        Interlocked.Increment(ref _presentedOffThread);
                        if (_measuring)
                        {
                            _presentMs.Add(_swapChain.LastPresentMs);
                        }
                    }

                    break;

                default:
                    break;
            }

            double waitMs = stepTimer.Elapsed.TotalMilliseconds;

            Interlocked.Increment(ref _renderedFrames);
            if (_measuring)
            {
                _renderMs.Add(drawMs);
                _gpuWaitMs.Add(waitMs);
            }

            if (_options.Mode == SpikeMode.SwapChain)
            {
                continue;
            }

            _framePresented.Reset();
            _frameReady.Set();

            if (_options.PresentOnDispatcher)
            {
                // Ask the UI thread to take the frame at render priority, rather than doing the
                // work inside the CompositionTarget.Rendering callback.
                Dispatcher.InvokeAsync(PresentOnce, DispatcherPriority.Render);
            }
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        _compositionTicks++;

        if (!_measuring && ElapsedSeconds >= _options.WarmupSeconds)
        {
            StartMeasuring();
        }

        if (_options.ResizeSweep && _measuring && ElapsedSeconds - _lastSweepSeconds > 1.0)
        {
            _lastSweepSeconds = ElapsedSeconds;
            _sweepStep = (_sweepStep + 1) % 4;
            Width = 1200 + (_sweepStep * 160);
            Height = 700 + (_sweepStep * 90);
        }

        if (_options.ForceRebuild && !_rebuildTriggered && _measuring &&
            ElapsedSeconds >= _options.WarmupSeconds + (_options.Seconds / 2))
        {
            _rebuildTriggered = true;
            ForceSurfaceRebuild();
        }

        if (_deferredSwapChainBind && _swapChain is { } chain && chain.ActualWidth >= 1)
        {
            // The child window only gets its real size after a layout pass, so size the swap chain
            // and bind the render target on the first composition tick rather than during Loaded.
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            chain.Resize(
                Math.Max(1, (int)(chain.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)(chain.ActualHeight * dpi.DpiScaleY)));

            if (chain.BackBuffer is { } buffer && chain.PixelWidth > 1)
            {
                _renderer?.SetTarget(buffer, chain.PixelWidth, chain.PixelHeight);
                _renderer?.SetOffscreenSize(_device, _options.Width, _options.Height);
                _deferredSwapChainBind = false;
                _notes.Add($"Swap chain sized to {chain.PixelWidth}x{chain.PixelHeight}.");
            }
        }

        if (_surfaceDirty)
        {
            ApplyPendingResize();
        }

        if (_frameReady.IsSet && !_options.PresentOnDispatcher)
        {
            PresentOnce();
        }

        if (_options.Seconds > 0 && _measuring && ElapsedSeconds >= _options.WarmupSeconds + _options.Seconds)
        {
            Finish();
        }
    }

    private void PresentOnce()
    {
        if (_frameReady.IsSet)
        {
            var timer = Stopwatch.StartNew();
            bool presented = _options.Mode == SpikeMode.D3DImage
                ? _surface?.Present() ?? false
                : _fallback?.Present() ?? false;
            double ms = timer.Elapsed.TotalMilliseconds;

            if (presented)
            {
                _presentedFrames++;
                if (_measuring)
                {
                    _presentMs.Add(ms);
                    if (_surface is { } surface)
                    {
                        _lockMs.Add(surface.LastLockMs);
                        _unlockMs.Add(surface.LastUnlockMs);
                    }
                }

                // Only release the render thread once WPF has actually taken the frame.
                _frameReady.Reset();
                _framePresented.Set();
            }
        }

        if (_options.Seconds > 0 && _measuring && ElapsedSeconds >= _options.WarmupSeconds + _options.Seconds)
        {
            Finish();
        }
    }

    private void StartMeasuring()
    {
        _measuring = true;
        _measureStartSeconds = ElapsedSeconds;
        _measureStartRendered = Interlocked.Read(ref _renderedFrames);
        _measureStartPresented = _options.Mode == SpikeMode.SwapChain
            ? Interlocked.Read(ref _presentedOffThread)
            : _presentedFrames;
        _measureStartUiCpu = ThreadCpu.ForCurrentThread();
        _measureStartProcessCpu = ThreadCpu.ForProcess();
        _measureStartAllocated = GC.GetTotalAllocatedBytes(precise: false);
        for (int generation = 0; generation < 3; generation++)
        {
            _measureStartCollections[generation] = GC.CollectionCount(generation);
        }

        _log.Information("Warmup over; measuring for {Seconds} s", _options.Seconds);
    }

    private void ForceSurfaceRebuild()
    {
        if (_surface is null)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        _surfaceDirty = true;
        _framePresented.Set();

        int width = _surface.PixelWidth;
        int height = _surface.PixelHeight;
        _surface.Dispose();
        _surface = new PreviewSurface(_device);
        _surface.SurfaceRecreated += OnSurfaceRecreated;
        _surface.Resize(width, height);
        _image.Source = _surface.Image;
        _renderer?.SetTarget(_surface.Texture!, width, height);
        _surfaceDirty = false;

        _lastRecoveryMs = timer.Elapsed.TotalMilliseconds;
        _notes.Add($"Forced surface rebuild took {_lastRecoveryMs:F1} ms.");
        _log.Information("Forced surface rebuild took {Ms:F1} ms", _lastRecoveryMs);
    }

    private void OnSurfaceRecreated(object? sender, EventArgs e)
    {
        _frontBufferLossEvents++;
        if (_surface?.Texture is { } texture)
        {
            _renderer?.SetTarget(texture, _surface.PixelWidth, _surface.PixelHeight);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_options.FollowWindowSize && !_options.ResizeSweep)
        {
            return;
        }

        if (_options.Mode == SpikeMode.SwapChain)
        {
            // The swap chain host resizes itself from the layout pass.
            _resizeEvents++;
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(16, (int)(e.NewSize.Width * dpi.DpiScaleX));
        int height = Math.Max(16, (int)(e.NewSize.Height * dpi.DpiScaleY));

        if (width == _pendingWidth && height == _pendingHeight)
        {
            return;
        }

        _pendingWidth = width;
        _pendingHeight = height;
        _surfaceDirty = true;
        _framePresented.Set();
    }

    private void ApplyPendingResize()
    {
        if (_surface is null || _renderer is null)
        {
            _surfaceDirty = false;
            return;
        }

        _surface.Resize(_pendingWidth, _pendingHeight);
        _renderer.SetTarget(_surface.Texture!, _pendingWidth, _pendingHeight);
        _resizeEvents++;
        _surfaceDirty = false;
        _framePresented.Set();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        double scale = e.NewDpi.DpiScaleX;
        if (!_dpiScales.Contains(scale))
        {
            _dpiScales.Add(scale);
        }

        _notes.Add($"DPI changed from {e.OldDpi.DpiScaleX:F2} to {scale:F2}.");
        _log.Information("DPI changed to {Scale:F2}", scale);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Finish();
                Close();
                break;

            case Key.R:
                ForceSurfaceRebuild();
                break;

            default:
                break;
        }
    }

    private void UpdateStatus()
    {
        double elapsed = ElapsedSeconds - _measureStartSeconds;
        long presented = _presentedFrames - _measureStartPresented;
        long rendered = Interlocked.Read(ref _renderedFrames) - _measureStartRendered;
        double fps = _measuring && elapsed > 0 ? presented / elapsed : 0;

        _status.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             mode        {_options.Mode}
             surface     {_surface?.PixelWidth ?? _options.Width}x{_surface?.PixelHeight ?? _options.Height}
             adapter     {_device.AdapterName}
             state       {(_measuring ? "measuring" : "warmup")} {elapsed:F1} s
             presented   {presented} ({fps:F1} fps)
             rendered    {rendered}
             dropped     {rendered - presented}
             composition {_compositionTicks}
             dpi         {VisualTreeHelper.GetDpi(this).DpiScaleX:F2}
             keys        R rebuild, Esc finish
             """);
    }

    private void Finish()
    {
        if (_reportWritten)
        {
            return;
        }

        _reportWritten = true;

        double elapsed = ElapsedSeconds - _measureStartSeconds;
        long presented = _options.Mode == SpikeMode.SwapChain
            ? Interlocked.Read(ref _presentedOffThread) - _measureStartPresented
            : _presentedFrames - _measureStartPresented;
        long rendered = Interlocked.Read(ref _renderedFrames) - _measureStartRendered;
        TimeSpan uiCpu = ThreadCpu.ForCurrentThread() - _measureStartUiCpu;
        TimeSpan processCpu = ThreadCpu.ForProcess() - _measureStartProcessCpu;
        long allocated = GC.GetTotalAllocatedBytes(precise: false) - _measureStartAllocated;

        var report = new SpikeReport
        {
            Mode = _options.Mode.ToString(),
            Size = $"{_surface?.PixelWidth ?? _options.Width}x{_surface?.PixelHeight ?? _options.Height}",
            Seconds = Math.Round(elapsed, 3),
            RenderedFrames = rendered,
            PresentedFrames = presented,
            PresentedFps = elapsed > 0 ? Math.Round(presented / elapsed, 2) : 0,
            DroppedFrames = rendered - presented,
            MeanRenderMs = Math.Round(_renderMs.Mean(), 3),
            P99RenderMs = Math.Round(_renderMs.Percentile(99), 3),
            MeanGpuWaitMs = Math.Round(_gpuWaitMs.Mean(), 3),
            MeanPresentMs = Math.Round(_presentMs.Mean(), 4),
            MeanLockMs = Math.Round(_lockMs.Mean(), 4),
            P50LockMs = Math.Round(_lockMs.Percentile(50), 4),
            P90LockMs = Math.Round(_lockMs.Percentile(90), 4),
            P99LockMs = Math.Round(_lockMs.Percentile(99), 4),
            MeanUnlockMs = Math.Round(_unlockMs.Mean(), 4),
            P99UnlockMs = Math.Round(_unlockMs.Percentile(99), 4),
            P99PresentMs = Math.Round(_presentMs.Percentile(99), 4),
            UiThreadCpuPercent = elapsed > 0 ? Math.Round(uiCpu.TotalSeconds / elapsed * 100, 2) : 0,
            ProcessCpuPercent = elapsed > 0 ? Math.Round(processCpu.TotalSeconds / elapsed * 100, 2) : 0,
            BytesAllocatedPerFrame = presented > 0 ? Math.Round((double)allocated / presented, 1) : 0,
            Collections =
            [
                GC.CollectionCount(0) - _measureStartCollections[0],
                GC.CollectionCount(1) - _measureStartCollections[1],
                GC.CollectionCount(2) - _measureStartCollections[2],
            ],
            PeakWorkingSetMb = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0, 1),
            FrontBufferLossEvents = _frontBufferLossEvents,
            LastRecoveryMs = Math.Round(_lastRecoveryMs, 2),
            ResizeEvents = _resizeEvents,
            DpiScales = [.. _dpiScales],
            Notes = [.. _notes, $"Adapter: {_device.AdapterName}.", $"Composition ticks: {_compositionTicks}.", $"WPF render tier: {RenderCapability.Tier >> 16}.", $"Window: {ActualWidth}x{ActualHeight} at dpi {VisualTreeHelper.GetDpi(this).DpiScaleX:F2}."],
        };

        string json = report.ToJson();
        _log.Information("Spike S1 report:{NewLine}{Report}", Environment.NewLine, json);

        if (_options.ReportPath is { } path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, json);
        }

        if (_options.Seconds > 0)
        {
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Finish();

        CompositionTarget.Rendering -= OnCompositionRendering;
        _statusTimer?.Stop();
        _shutdown.Cancel();
        _framePresented.Set();
        _renderThread?.Join(TimeSpan.FromSeconds(2));

        _timerResolution?.Dispose();
        _swapChain?.Dispose();
        _renderer?.Dispose();
        _fallback?.Dispose();
        _surface?.Dispose();
        _device.Dispose();
        _frameReady.Dispose();
        _framePresented.Dispose();
        _shutdown.Dispose();
    }
}
