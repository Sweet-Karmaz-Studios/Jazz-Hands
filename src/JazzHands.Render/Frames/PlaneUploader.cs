using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Serilog;

namespace JazzHands.Render.Frames;

/// <summary>
/// One decoded plane in system memory: where it is, how wide a row is, and how many rows.
/// </summary>
/// <remarks>
/// A pointer rather than a span, because these arrive several at a time and a span of spans is
/// not a thing. The memory belongs to the decoded frame and must outlive the upload, which it
/// does: the caller holds the frame until the copy returns.
/// </remarks>
/// <param name="Data">The first byte of the plane, in unmanaged memory the decoder owns.</param>
/// <param name="Stride">Bytes from the start of one row to the start of the next, which is not the row's width.</param>
/// <param name="Width">Samples across.</param>
/// <param name="Height">Rows.</param>
public readonly record struct SourcePlane(IntPtr Data, int Stride, int Width, int Height)
{
    /// <summary>True when there is nothing to upload.</summary>
    public bool IsEmpty => Data == IntPtr.Zero || Width <= 0 || Height <= 0;
}

/// <summary>
/// Puts software decoded planes into textures.
/// </summary>
/// <remarks>
/// The software fallback path's last step. A decoder that could not use the GPU hands back planes
/// in system memory, and the compositor samples textures, so somebody has to move the bytes; this
/// is that somebody, and it is a named cost rather than a hidden one.
///
/// Row by row rather than in one block, because a decoded plane's stride is the decoder's
/// business (it pads for alignment) and a mapped texture's is the driver's, and they are rarely
/// the same number.
/// </remarks>
public sealed class PlaneUploader(RenderDevice device)
{
    private readonly ILogger _log = Log.ForContext<PlaneUploader>();

    /// <summary>Planes uploaded, for the diagnostics that say how much of a timeline is on the fallback path.</summary>
    public long Uploaded { get; private set; }

    /// <summary>Bytes written, for the same reason.</summary>
    public long BytesUploaded { get; private set; }

    /// <summary>
    /// Writes decoded planes into a frame texture.
    /// </summary>
    /// <param name="target">A texture rented with <see cref="FrameTextureUsage.Upload"/>.</param>
    /// <param name="planes">One entry per plane of the target's layout, in the same order.</param>
    public void Upload(FrameTexture target, ReadOnlySpan<SourcePlane> planes)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (planes.Length != target.PlaneCount)
        {
            throw new ArgumentException(
                $"The target has {target.PlaneCount} planes and {planes.Length} were given.",
                nameof(planes));
        }

        ID3D11DeviceContext context = device.ImmediateContext;

        for (int index = 0; index < planes.Length; index++)
        {
            UploadPlane(context, target.Plane(index), planes[index], target.Layout.Planes[index]);
        }

        Uploaded += planes.Length;
    }

    private unsafe void UploadPlane(
        ID3D11DeviceContext context,
        ID3D11Texture2D texture,
        SourcePlane plane,
        PixelPlane description)
    {
        int sampleBytes = description.Channels * (texture.Description.Format is Vortice.DXGI.Format.R16_UNorm
            or Vortice.DXGI.Format.R16G16_UNorm ? 2 : 1);

        int rowBytes = plane.Width * sampleBytes;
        int rows = Math.Min(plane.Height, (int)texture.Description.Height);

        if (plane.IsEmpty || rowBytes <= 0 || rows <= 0)
        {
            return;
        }

        // Discard rather than a partial write: the whole plane is replaced, and telling the
        // driver so lets it hand back a fresh allocation instead of waiting for the last read.
        MappedSubresource mapped = context.Map(texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);

        try
        {
            var destination = (byte*)mapped.DataPointer;
            var source = (byte*)plane.Data;
            int destinationStride = (int)mapped.RowPitch;
            int copy = Math.Min(rowBytes, destinationStride);

            for (int row = 0; row < rows; row++)
            {
                Buffer.MemoryCopy(
                    source + ((long)row * plane.Stride),
                    destination + ((long)row * destinationStride),
                    destinationStride,
                    copy);
            }

            BytesUploaded += (long)copy * rows;
        }
        finally
        {
            context.Unmap(texture, 0);
        }
    }

    /// <summary>Logs what the fallback path has cost so far.</summary>
    public void LogTotals() => _log.Debug(
        "Uploaded {Planes} planes, {Megabytes:F1} MB, on the software decode path",
        Uploaded,
        BytesUploaded / (1024.0 * 1024));
}
