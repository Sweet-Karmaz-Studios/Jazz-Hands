using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JazzHands.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.App.Spikes;

/// <summary>
/// The alternative the spike measures for comparison only: render in D3D11, copy the frame down
/// to system memory, and push it into a <see cref="WriteableBitmap"/>. Every frame crosses the
/// PCIe bus twice and lands on the UI thread, which is exactly what the D3DImage path avoids.
/// Kept so the decision against it is backed by a number rather than an assertion.
/// </summary>
internal sealed class WriteableBitmapPresenter : IDisposable
{
    private readonly RenderDevice _device;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11Texture2D _staging;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels;

    /// <summary>Creates the render target, its staging copy and the bitmap.</summary>
    public WriteableBitmapPresenter(RenderDevice device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _width = width;
        _height = height;
        _pixels = new byte[width * height * 4];

        _texture = device.CreateRenderTarget(width, height, Format.B8G8R8A8_UNorm);
        _staging = device.CreateStagingTexture(_texture);
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>The bitmap to put in an Image.</summary>
    public WriteableBitmap Bitmap { get; }

    /// <summary>The texture the renderer draws into.</summary>
    public ID3D11Texture2D Texture => _texture;

    /// <summary>Copies the frame off the GPU into managed memory. Render thread.</summary>
    public void ReadBack()
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.CopyResource(_staging, _texture);

        MappedSubresource mapped = context.Map(_staging, 0, MapMode.Read);
        try
        {
            unsafe
            {
                byte* source = (byte*)mapped.DataPointer;
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    new ReadOnlySpan<byte>(source + (y * (int)mapped.RowPitch), rowBytes)
                        .CopyTo(_pixels.AsSpan(y * rowBytes));
                }
            }
        }
        finally
        {
            context.Unmap(_staging, 0);
        }
    }

    /// <summary>Uploads the frame into the bitmap. UI thread.</summary>
    public bool Present()
    {
        Bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _staging.Dispose();
        _texture.Dispose();
    }
}
