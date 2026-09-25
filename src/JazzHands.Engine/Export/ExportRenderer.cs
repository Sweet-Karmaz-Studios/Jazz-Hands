using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Frames;
using JazzHands.Media.Decode;
using JazzHands.Media.Encode;
using JazzHands.Render;
using JazzHands.Render.Compositing;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Rational = JazzHands.Core.Time.Rational;

namespace JazzHands.Engine.Export;

/// <summary>Where rendered frames go: an in-process encoder, or ffmpeg.exe.</summary>
internal interface IFrameSink
{
    /// <summary>A frame to fill, waiting for one to come free.</summary>
    EncoderFrame Rent(CancellationToken cancellationToken);

    /// <summary>Hands a filled frame over, at its index in the output.</summary>
    void Submit(EncoderFrame frame, long index, CancellationToken cancellationToken);
}

/// <summary>
/// Renders the frames of an export plan through the compositor and reads them back as 4:2:0.
/// </summary>
/// <remarks>
/// Every frame is the frame the preview would show, rendered by a <see cref="FrameServer"/> of its
/// own on a device of its own: an export beside playback on the playback device would put its
/// work in front of every present. The compositor encodes each stack straight to BT.709 limited
/// range luma and chroma on the GPU (<see cref="Compositor.OutputYuv"/>), so what comes back to
/// the CPU is the frame the encoder wants, three bytes in two per pixel rather than eight.
///
/// This is one of the named exceptions to frames staying on the GPU: an encoder fed from system
/// memory. The read back runs three frames behind the render through a ring of staging textures,
/// so the CPU never waits for the frame the GPU is still drawing. Handing NVENC the texture itself
/// is spike S3's, in Phase 22.
///
/// Thread affine: made, run and disposed on one thread.
/// </remarks>
internal sealed class ExportRenderer : IDisposable
{
    private const int Ring = 3;

    private readonly ILogger _log = Log.ForContext<ExportRenderer>();
    private readonly RenderDevice _device;
    private readonly HardwareDeviceContext? _hardware;
    private readonly FrameServer _frames;
    private readonly Slot[] _slots = new Slot[Ring];
    private ID3D11Texture2D? _luma;
    private ID3D11Texture2D? _chroma;
    private ID3D11RenderTargetView? _lumaView;
    private ID3D11RenderTargetView? _chromaView;
    private int _width;
    private int _height;

    public ExportRenderer(ExportEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _device = RenderDevice.Create(environment.ForceWarp);
        if (environment.HardwareDecode && _device.SupportsVideo)
        {
            _hardware = HardwareDeviceContext.CreateShared(_device.Device.NativePointer, _device.ImmediateContext.NativePointer);
        }

        // Every frame is decoded once and never looked at again, so the cache only needs to hold
        // what a render in flight is using.
        _frames = new FrameServer(_device, _hardware, frameCacheBytes: 256L * 1024 * 1024);
    }

    /// <summary>The GPU the export renders on.</summary>
    public string Adapter => _device.AdapterName;

    /// <summary>How many frames a plan writes.</summary>
    public static long CountFrames(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Rational rate = plan.Video!.FrameRate;
        long total = 0;
        foreach (TimeRange range in plan.Ranges)
        {
            total += Math.Max(0, range.End.ToFrames(rate, RoundingMode.Nearest) - range.Start.ToFrames(rate, RoundingMode.Nearest));
        }

        return total;
    }

    /// <summary>Renders every frame of the plan into the sink, in order.</summary>
    public void Render(Project project, string projectPath, ExportPlan plan, IFrameSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);

        ExportVideo video = plan.Video ?? throw new ArgumentException("An encode plan has a video side.", nameof(plan));
        Sequence sequence = project.Sequence(plan.SequenceId)
            ?? throw new InvalidOperationException($"The sequence '{plan.SequenceId}' has gone.");
        ProjectSettings settings = project.SettingsFor(sequence);

        Allocate(video.Width, video.Height);

        var options = new RenderOptions
        {
            Scale = (float)video.Height / settings.Height,
            Bicubic = true,
            CacheLayers = false,
            Subtitles = plan.Subtitles?.Delivery == SubtitleDelivery.Burn,
        };
        var output = new OutputSettings(OutputEncoding.Bt1886, DitherLevels: 255);

        long index = 0;
        long read = 0;

        foreach (TimeRange range in plan.Ranges)
        {
            long first = range.Start.ToFrames(video.FrameRate, RoundingMode.Nearest);
            long end = range.End.ToFrames(video.FrameRate, RoundingMode.Nearest);

            for (long frame = first; frame < end; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RenderTarget stack = _frames.Render(project, sequence, Flicks.FromFrames(frame, video.FrameRate), options, projectPath);
                try
                {
                    _frames.Compositor.OutputYuv(stack, _lumaView!, _chromaView!, _width, _height, output);
                }
                finally
                {
                    _frames.Compositor.Pool.Return(stack);
                }

                Slot slot = _slots[index % Ring];
                ID3D11DeviceContext context = _device.ImmediateContext;
                context.CopyResource(slot.Luma, _luma!);
                context.CopyResource(slot.Chroma, _chroma!);
                index++;

                // Read the frame that has had two more frames' worth of time to finish.
                if (index - read >= Ring)
                {
                    ReadBack(read, sink, cancellationToken);
                    read++;
                }
            }
        }

        while (read < index)
        {
            ReadBack(read, sink, cancellationToken);
            read++;
        }

        _log.Debug("Rendered {Frames} frames at {Width}x{Height} on {Adapter}", index, _width, _height, _device.AdapterName);
    }

    public void Dispose()
    {
        foreach (Slot slot in _slots)
        {
            slot?.Dispose();
        }

        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _luma?.Dispose();
        _chroma?.Dispose();
        _frames.Dispose();
        _hardware?.Dispose();
        _device.Dispose();
    }

    private void Allocate(int width, int height)
    {
        if (_luma is not null && width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        ID3D11Device device = _device.Device;

        _luma = device.CreateTexture2D(Target(Format.R8_UNorm, width, height));
        _chroma = device.CreateTexture2D(Target(Format.R8G8_UNorm, width / 2, height / 2));
        _lumaView = device.CreateRenderTargetView(_luma);
        _chromaView = device.CreateRenderTargetView(_chroma);

        for (int index = 0; index < Ring; index++)
        {
            _slots[index] = new Slot(
                device.CreateTexture2D(Staging(Format.R8_UNorm, width, height)),
                device.CreateTexture2D(Staging(Format.R8G8_UNorm, width / 2, height / 2)));
        }
    }

    private unsafe void ReadBack(long index, IFrameSink sink, CancellationToken cancellationToken)
    {
        Slot slot = _slots[index % Ring];
        EncoderFrame frame = sink.Rent(cancellationToken);
        ID3D11DeviceContext context = _device.ImmediateContext;

        frame.MakeWritable();

        MappedSubresource luma = context.Map(slot.Luma, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CopyRows((byte*)luma.DataPointer, (int)luma.RowPitch, (byte*)frame.Plane(0), frame.Stride(0), _width, _height);
        }
        finally
        {
            context.Unmap(slot.Luma, 0);
        }

        MappedSubresource chroma = context.Map(slot.Chroma, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            if (frame.IsNv12)
            {
                CopyRows((byte*)chroma.DataPointer, (int)chroma.RowPitch, (byte*)frame.Plane(1), frame.Stride(1), _width, _height / 2);
            }
            else
            {
                Deinterleave((byte*)chroma.DataPointer, (int)chroma.RowPitch, frame, _width / 2, _height / 2);
            }
        }
        finally
        {
            context.Unmap(slot.Chroma, 0);
        }

        sink.Submit(frame, index, cancellationToken);
    }

    private static unsafe void CopyRows(byte* source, int sourcePitch, byte* target, int targetPitch, int bytes, int rows)
    {
        for (int row = 0; row < rows; row++)
        {
            Buffer.MemoryCopy(source + ((long)row * sourcePitch), target + ((long)row * targetPitch), targetPitch, bytes);
        }
    }

    private static unsafe void Deinterleave(byte* source, int sourcePitch, EncoderFrame frame, int width, int rows)
    {
        byte* u = (byte*)frame.Plane(1);
        byte* v = (byte*)frame.Plane(2);
        int uPitch = frame.Stride(1);
        int vPitch = frame.Stride(2);

        for (int row = 0; row < rows; row++)
        {
            byte* line = source + ((long)row * sourcePitch);
            byte* uLine = u + ((long)row * uPitch);
            byte* vLine = v + ((long)row * vPitch);
            for (int x = 0; x < width; x++)
            {
                uLine[x] = line[x * 2];
                vLine[x] = line[(x * 2) + 1];
            }
        }
    }

    private static Texture2DDescription Target(Format format, int width, int height) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget,
    };

    private static Texture2DDescription Staging(Format format, int width, int height) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Staging,
        CPUAccessFlags = CpuAccessFlags.Read,
    };

    private sealed class Slot(ID3D11Texture2D luma, ID3D11Texture2D chroma) : IDisposable
    {
        public ID3D11Texture2D Luma { get; } = luma;

        public ID3D11Texture2D Chroma { get; } = chroma;

        public void Dispose()
        {
            Luma.Dispose();
            Chroma.Dispose();
        }
    }
}
