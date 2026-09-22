using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Probe;

namespace JazzHands.Media.Decode;

/// <summary>Where a decoded frame's pixels live.</summary>
public enum FrameLocation
{
    /// <summary>In a Direct3D 11 texture array owned by the decoder. The compositor samples it directly.</summary>
    Gpu,

    /// <summary>In system memory planes. Software decode, or a deliberate download.</summary>
    Cpu,
}

/// <summary>
/// One decoded video frame, still owned by the decoder's pool.
/// </summary>
/// <remarks>
/// A GPU frame is a slice of a texture array that NVDEC decoded into. Nothing is copied: the
/// compositor builds a shader resource view over <see cref="Texture"/> at
/// <see cref="TextureIndex"/> and samples it. The slot belongs to the decoder's pool until this
/// frame is disposed, so hold it no longer than a render pass.
/// </remarks>
public sealed unsafe class VideoFrame : IDisposable
{
    private readonly FramePool? _pool;
    private Interop.AvFrame? _frame;

    internal VideoFrame(Interop.AvFrame frame, FramePool? pool, Flicks pts, Flicks duration, ColorInfo color)
    {
        _frame = frame;
        _pool = pool;
        Pts = pts;
        Duration = duration;
        Color = color;

        AVFrame* raw = frame.Handle;
        Width = raw->width;
        Height = raw->height;
        PixelFormat = (AVPixelFormat)raw->format;
        Location = PixelFormat == AVPixelFormat.AV_PIX_FMT_D3D11 ? FrameLocation.Gpu : FrameLocation.Cpu;
    }

    /// <summary>Presentation time on the source timeline.</summary>
    public Flicks Pts { get; }

    /// <summary>How long the frame is shown, from the stream's frame rate.</summary>
    public Flicks Duration { get; }

    /// <summary>Coded width in pixels.</summary>
    public int Width { get; }

    /// <summary>Coded height in pixels.</summary>
    public int Height { get; }

    /// <summary>The FFmpeg pixel format the frame came out in.</summary>
    public AVPixelFormat PixelFormat { get; }

    /// <summary>Whether the pixels are on the GPU or in system memory.</summary>
    public FrameLocation Location { get; }

    /// <summary>Colour signalling, carried from the stream so the compositor converts correctly.</summary>
    public ColorInfo Color { get; }

    /// <summary>True when this frame is still usable.</summary>
    public bool IsValid => _frame is not null;

    /// <summary>
    /// The decoder's Direct3D 11 texture array, for a GPU frame. This is an
    /// <c>ID3D11Texture2D*</c>; the render layer wraps it rather than Media taking a dependency
    /// on Direct3D.
    /// </summary>
    public IntPtr Texture => Location == FrameLocation.Gpu
        ? (IntPtr)Handle->data[0]
        : throw new InvalidOperationException("This frame is in system memory, not a Direct3D texture.");

    /// <summary>The array slice inside <see cref="Texture"/> that holds this frame.</summary>
    public int TextureIndex => Location == FrameLocation.Gpu
        ? (int)Handle->data[1]
        : throw new InvalidOperationException("This frame is in system memory, not a Direct3D texture.");

    /// <summary>The underlying AVFrame. Internal because ownership is the pool's, not the caller's.</summary>
    internal AVFrame* Handle => _frame is null
        ? throw new ObjectDisposedException(nameof(VideoFrame))
        : _frame.Handle;

    /// <summary>A plane of a CPU frame, as a span over unmanaged memory. No copy.</summary>
    /// <param name="plane">Plane index: 0 luma, 1 and 2 chroma for planar YUV.</param>
    public ReadOnlySpan<byte> GetPlane(int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        if (Location != FrameLocation.Cpu)
        {
            throw new InvalidOperationException("This frame is on the GPU; download it before reading planes.");
        }

        AVFrame* frame = Handle;
        int stride = frame->linesize[(uint)plane];
        if (stride <= 0)
        {
            return [];
        }

        int height = plane == 0 ? frame->height : ChromaHeight(frame);
        return new ReadOnlySpan<byte>(frame->data[(uint)plane], stride * height);
    }

    /// <summary>The byte stride of a plane.</summary>
    public int GetStride(int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        return Handle->linesize[(uint)plane];
    }

    /// <summary>
    /// Copies a GPU frame down into system memory and returns it as a new frame.
    /// </summary>
    /// <remarks>
    /// This is the copy the rest of the design exists to avoid, so it is spelled out rather than
    /// hidden behind a property. It belongs to thumbnails, software encoders and tests, and never
    /// to the preview path. The caller disposes the result; this frame is untouched.
    /// </remarks>
    public VideoFrame DownloadToCpu()
    {
        if (Location == FrameLocation.Cpu)
        {
            throw new InvalidOperationException("This frame is already in system memory.");
        }

        var destination = new Interop.AvFrame();
        try
        {
            Interop.Av.Check(
                ffmpeg.av_hwframe_transfer_data(destination.Handle, Handle, 0),
                "av_hwframe_transfer_data");

            Interop.Av.Check(
                ffmpeg.av_frame_copy_props(destination.Handle, Handle),
                "av_frame_copy_props");

            return new VideoFrame(destination, pool: null, Pts, Duration, Color);
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>Returns the frame to its pool. Always dispose: a leaked frame starves the decoder.</summary>
    public void Dispose()
    {
        if (_frame is null)
        {
            return;
        }

        Interop.AvFrame frame = _frame;
        _frame = null;

        if (_pool is not null)
        {
            _pool.Return(frame);
        }
        else
        {
            frame.Dispose();
        }
    }

    private int ChromaHeight(AVFrame* frame)
    {
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(PixelFormat);
        int shift = descriptor is not null ? descriptor->log2_chroma_h : 1;
        return (frame->height + (1 << shift) - 1) >> shift;
    }
}
