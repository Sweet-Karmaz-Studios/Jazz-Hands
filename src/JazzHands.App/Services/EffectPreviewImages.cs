using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JazzHands.Core.Effects;
using JazzHands.Engine.Effects;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>Pictures of what each effect and generator does, for the effects browser.</summary>
public interface IEffectPreviewImages
{
    /// <summary>Raised on the UI thread with a type id when its picture is ready.</summary>
    event EventHandler<string>? Ready;

    /// <summary>A type's picture, or null until it is ready (or for a sound effect, which has none).</summary>
    ImageSource? Find(string typeId);
}

/// <summary>
/// The engine's <see cref="EffectPreviews"/>, rendered on WARP on a background thread of low
/// priority as the editor starts, and handed to the effects browser as frozen bitmaps.
/// </summary>
/// <remarks>
/// WARP rather than the GPU: the pictures are small, the whole set takes about a second, and
/// playback keeps the GPU to itself. Nothing is kept on disk; a set this cheap is not worth a
/// cache to invalidate when an effect changes. A type that fails to render is logged and left
/// without a picture; the browser shows its name either way.
/// </remarks>
public sealed class EffectPreviewImages : IEffectPreviewImages, IDisposable
{
    private readonly ILogger _log = Log.ForContext<EffectPreviewImages>();
    private readonly ConcurrentDictionary<string, ImageSource> _images = new(StringComparer.Ordinal);
    private readonly IUiDispatcher _ui;
    private readonly EffectRegistry _registry;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _worker;

    /// <summary>Creates the service; <see cref="Start"/> begins rendering.</summary>
    public EffectPreviewImages(IUiDispatcher ui, EffectRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(ui);

        _ui = ui;
        _registry = registry ?? EffectCatalog.Registry;
    }

    /// <inheritdoc />
    public event EventHandler<string>? Ready;

    /// <inheritdoc />
    public ImageSource? Find(string typeId) => _images.GetValueOrDefault(typeId);

    /// <summary>Starts rendering every preview in the background, once.</summary>
    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Effect previews" };
        _worker.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private void Run()
    {
        try
        {
            using var previews = new EffectPreviews(registry: _registry);
            foreach (EffectDescriptor descriptor in _registry.All.Where(EffectPreviews.HasPreview))
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                Render(previews, descriptor.TypeId);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Warning(exception, "The effect previews could not be rendered");
        }
    }

    private void Render(EffectPreviews previews, string typeId)
    {
        try
        {
            if (previews.Render(typeId) is not { } bgra)
            {
                return;
            }

            var image = BitmapSource.Create(EffectPreviews.Width, EffectPreviews.Height, 96, 96, PixelFormats.Bgra32, null, bgra, EffectPreviews.Width * 4);
            image.Freeze();
            _images[typeId] = image;
            _ui.Post(() => Ready?.Invoke(this, typeId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Warning(exception, "The preview of {TypeId} could not be rendered", typeId);
        }
    }
}
