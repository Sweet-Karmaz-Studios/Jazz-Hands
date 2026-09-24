using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Render.Scopes;

namespace JazzHands.App.ViewModels.Grading;

/// <summary>A target on the vectorscope: where a colour bar lands, in the scope's 256 by 256 pixels.</summary>
/// <param name="Name">R, Mg, B, Cy, G or Yl, as every vectorscope labels them.</param>
/// <param name="X">Across.</param>
/// <param name="Y">Down.</param>
public readonly record struct ScopeTarget(string Name, double X, double Y);

/// <summary>
/// The Scopes panel: waveform, RGB parade, vectorscope and histogram of the frame on the preview,
/// with graticules, each shown or hidden.
/// </summary>
/// <remarks>
/// The engine measures only while the panel is on screen (<see cref="SetShown"/>), which is what
/// "pause when hidden" means: a hidden panel costs nothing. Readings arrive on the composition
/// thread at up to 30 a second; only the newest is kept, and one UI update is queued at a time, so
/// a busy UI thread skips readings rather than falling behind them. The pictures are written into
/// bitmaps made once; the histogram is four polylines.
/// </remarks>
public sealed partial class ScopesPanelViewModel : ToolViewModel
{
    /// <summary>The docking content id.</summary>
    public const string PanelId = "scopes";

    /// <summary>The histogram's height in its own units; its width is 256, a unit per code value.</summary>
    public const double HistogramHeight = 100;

    private readonly IPreviewEngine _preview;
    private readonly IUiDispatcher _ui;
    private ScopeReading? _latest;
    private int _queued;
    private bool _shown;

    [ObservableProperty]
    private bool _showWaveform = true;

    [ObservableProperty]
    private bool _showParade = true;

    [ObservableProperty]
    private bool _showVectorscope = true;

    [ObservableProperty]
    private bool _showHistogram = true;

    [ObservableProperty]
    private PointCollection _redHistogram = [];

    [ObservableProperty]
    private PointCollection _greenHistogram = [];

    [ObservableProperty]
    private PointCollection _blueHistogram = [];

    [ObservableProperty]
    private PointCollection _lumaHistogram = [];

    [ObservableProperty]
    private string _clipping = string.Empty;

    [ObservableProperty]
    private bool _hasReading;

    /// <summary>Creates the panel.</summary>
    public ScopesPanelViewModel(IPreviewEngine preview, IUiDispatcher ui)
        : base(PanelId, "Scopes")
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(ui);

        _preview = preview;
        _ui = ui;
        _preview.ScopesMeasured += OnMeasured;
    }

    /// <summary>The luma waveform, 512 by 256.</summary>
    public WriteableBitmap Waveform { get; } = Bitmap(ScopeRenderer.WaveWidth, 256);

    /// <summary>The RGB parade, three channels of 170 side by side.</summary>
    public WriteableBitmap Parade { get; } = Bitmap(3 * ScopeRenderer.ParadeWidth, 256);

    /// <summary>The vectorscope, 256 by 256.</summary>
    public WriteableBitmap Vectorscope { get; } = Bitmap(ScopeRenderer.VectorSize, ScopeRenderer.VectorSize);

    /// <summary>Where 75% colour bars land on the vectorscope.</summary>
    public IReadOnlyList<ScopeTarget> Targets { get; } =
    [
        Target("R", 0.75, 0, 0),
        Target("Mg", 0.75, 0, 0.75),
        Target("B", 0, 0, 0.75),
        Target("Cy", 0, 0.75, 0.75),
        Target("G", 0, 0.75, 0),
        Target("Yl", 0.75, 0.75, 0),
    ];

    /// <summary>
    /// The skin tone line from the centre of the vectorscope to its edge: the I axis, 123° from Cb.
    /// Faces of every complexion sit on or near it.
    /// </summary>
    public Point SkinLineEnd { get; } = new(128 + (128 * Math.Cos(123 * Math.PI / 180)), 128 - (128 * Math.Sin(123 * Math.PI / 180)));

    /// <summary>
    /// The view says whether the panel is on screen: the engine measures only then, and stops
    /// when the panel is closed, behind another tab or minimised.
    /// </summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        _preview.Scopes = shown;
    }

    /// <summary>Puts a reading on screen. Called on the UI thread.</summary>
    internal void Show(ScopeReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        Write(Waveform, reading.Waveform);
        Write(Parade, reading.Parade);
        Write(Vectorscope, reading.Vectorscope);

        // Each channel against the busiest bin of any, so their heights compare.
        double peak = Math.Max(1, new[] { reading.Red.Max(), reading.Green.Max(), reading.Blue.Max(), reading.Luma.Max() }.Max());
        RedHistogram = Polyline(reading.Red, peak);
        GreenHistogram = Polyline(reading.Green, peak);
        BlueHistogram = Polyline(reading.Blue, peak);
        LumaHistogram = Polyline(reading.Luma, peak);

        Clipping = reading.ClippedHigh < 0.0005 && reading.ClippedLow < 0.0005
            ? "No clipping"
            : $"Clipped {reading.ClippedHigh:P1} white, {reading.ClippedLow:P1} black";
        HasReading = true;
    }

    private void OnMeasured(object? sender, ScopeReading reading)
    {
        if (!_shown)
        {
            return;
        }

        Volatile.Write(ref _latest, reading);
        if (Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }

        _ui.Post(() =>
        {
            Volatile.Write(ref _queued, 0);
            if (Interlocked.Exchange(ref _latest, null) is { } newest)
            {
                Show(newest);
            }
        });
    }

    private static WriteableBitmap Bitmap(int width, int height) => new(width, height, 96, 96, PixelFormats.Bgra32, null);

    private static void Write(WriteableBitmap bitmap, ScopePicture picture) =>
        bitmap.WritePixels(new Int32Rect(0, 0, picture.Width, picture.Height), picture.Bgra, picture.Width * 4, 0);

    private static PointCollection Polyline(int[] counts, double peak)
    {
        var points = new PointCollection(counts.Length + 2) { new Point(0, HistogramHeight) };
        for (int value = 0; value < counts.Length; value++)
        {
            points.Add(new Point(value + 0.5, HistogramHeight * (1.0 - (counts[value] / peak))));
        }

        points.Add(new Point(counts.Length, HistogramHeight));
        points.Freeze();
        return points;
    }

    /// <summary>Where a BT.709 colour lands on the vectorscope, as the scope's shader places it.</summary>
    private static ScopeTarget Target(string name, double r, double g, double b)
    {
        double luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double cb = (b - luma) / 1.8556;
        double cr = (r - luma) / 1.5748;
        return new ScopeTarget(name, (cb + 0.5) * 255, (0.5 - cr) * 255);
    }
}
