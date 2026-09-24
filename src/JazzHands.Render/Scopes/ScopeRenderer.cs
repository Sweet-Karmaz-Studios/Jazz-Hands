using System.Runtime.InteropServices;
using JazzHands.Render.Shaders;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace JazzHands.Render.Scopes;

/// <summary>One scope's picture: BGRA, top row first, alpha the trace's strength.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Bgra">Width x height x 4 bytes.</param>
public sealed record ScopePicture(int Width, int Height, byte[] Bgra);

/// <summary>
/// What the scopes measured on one frame: the histograms as counts, and the waveform, parade and
/// vectorscope as pictures.
/// </summary>
/// <param name="FrameWidth">The frame measured.</param>
/// <param name="FrameHeight">Its height.</param>
/// <param name="Red">Pixels at each of 256 red code values.</param>
/// <param name="Green">Green.</param>
/// <param name="Blue">Blue.</param>
/// <param name="Luma">BT.709 luma.</param>
/// <param name="Waveform">Luma by position across the frame, white at the top.</param>
/// <param name="Parade">Red, green and blue waveforms side by side.</param>
/// <param name="Vectorscope">Cb across and Cr up, red towards the top left.</param>
/// <param name="SampledPixels">How many pixels were counted: all of them, or a quarter on frames over a megapixel.</param>
public sealed record ScopeReading(
    int FrameWidth,
    int FrameHeight,
    int[] Red,
    int[] Green,
    int[] Blue,
    int[] Luma,
    ScopePicture Waveform,
    ScopePicture Parade,
    ScopePicture Vectorscope,
    long SampledPixels)
{
    /// <summary>The share of pixels whose luma is at code value 255 or over, 0 to 1.</summary>
    public double ClippedHigh => (double)Luma[255] / Math.Max(1, SampledPixels);

    /// <summary>The share at code value 0.</summary>
    public double ClippedLow => (double)Luma[0] / Math.Max(1, SampledPixels);
}

/// <summary>
/// Runs the scopes on the GPU: histogram, waveform, parade and vectorscope from an encoded frame.
/// </summary>
/// <remarks>
/// One compute pass counts every pixel with atomic adds into a raw buffer; three small passes turn
/// the counts into pictures. Results come back through a ring of two staging sets, so the
/// composition thread hands in a frame and collects the one before without waiting on the GPU;
/// <see cref="Measure"/> waits, for tests and headless queries. The frame is read as it is: pass
/// the BT.1886 program texture, and the scopes show the delivered signal. Thread affine, like the
/// device context it drives.
/// </remarks>
public sealed class ScopeRenderer : IDisposable
{
    /// <summary>Columns of the luma waveform.</summary>
    public const int WaveWidth = 512;

    /// <summary>Columns of each channel of the parade.</summary>
    public const int ParadeWidth = 170;

    /// <summary>The vectorscope's size.</summary>
    public const int VectorSize = 256;

    private const int HistogramCounts = 1024;
    private const int WaveBase = HistogramCounts;
    private const int ParadeBase = WaveBase + (256 * WaveWidth);
    private const int VectorBase = ParadeBase + (256 * 3 * ParadeWidth);
    private const int TotalCounts = VectorBase + (VectorSize * VectorSize);

    private readonly RenderDevice _device;
    private readonly ID3D11Buffer _counts;
    private readonly ID3D11UnorderedAccessView _countsView;
    private readonly ID3D11Buffer _constants;
    private readonly Picture _waveform;
    private readonly Picture _parade;
    private readonly Picture _vector;
    private readonly Slot[] _slots;
    private readonly Queue<int> _pending = new();
    private ID3D11ComputeShader? _accumulate;
    private ID3D11ComputeShader? _drawWave;
    private ID3D11ComputeShader? _drawParade;
    private ID3D11ComputeShader? _drawVector;
    private int _generation = -1;
    private int _next;
    private bool _disposed;

    /// <summary>Creates the buffers on a device.</summary>
    public ScopeRenderer(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        ID3D11Device d3d = device.Device;

        _counts = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = TotalCounts * 4,
            BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferAllowRawViews,
            Usage = ResourceUsage.Default,
        });
        _countsView = d3d.CreateUnorderedAccessView(_counts, new UnorderedAccessViewDescription
        {
            Format = Format.R32_Typeless,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = TotalCounts, Flags = BufferUnorderedAccessViewFlags.Raw },
        });
        _constants = d3d.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<ScopeConstants>(),
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Default,
        });

        _waveform = new Picture(d3d, WaveWidth, 256);
        _parade = new Picture(d3d, 3 * ParadeWidth, 256);
        _vector = new Picture(d3d, VectorSize, VectorSize);
        _slots = [new Slot(d3d, _waveform, _parade, _vector), new Slot(d3d, _waveform, _parade, _vector)];
    }

    /// <summary>
    /// Measures a frame and copies the results for collecting later. When two are already waiting,
    /// the older is dropped: the scopes show the latest frame, not every frame.
    /// </summary>
    /// <param name="frame">An encoded frame, readable as a shader resource.</param>
    public void Submit(ID3D11Texture2D frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureShaders();

        Texture2DDescription description = frame.Description;
        int width = (int)description.Width;
        int height = (int)description.Height;
        ID3D11DeviceContext context = _device.ImmediateContext;
        int step = (long)width * height > 1_000_000 ? 2 : 1;
        int sampledWidth = (width + step - 1) / step;
        int sampledHeight = (height + step - 1) / step;

        var constants = new ScopeConstants
        {
            Width = (uint)width,
            Height = (uint)height,
            WaveWidth = WaveWidth,
            ParadeWidth = ParadeWidth,
            WaveBase = WaveBase,
            ParadeBase = ParadeBase,
            VectorBase = VectorBase,
            Step = (uint)step,

            // A column of the waveform gets height x (width / columns) samples; a bin holding about a
            // hundred and fiftieth of them glows most of the way, so a channel spread over the whole
            // range still reads. The vectorscope spreads the whole frame.
            WaveGain = 150.0f / Math.Max(1.0f, sampledHeight * (sampledWidth / (float)WaveWidth)),
            ParadeGain = 150.0f / Math.Max(1.0f, sampledHeight * (sampledWidth / (float)ParadeWidth)),
            VectorGain = 4000.0f / Math.Max(1.0f, sampledWidth * (float)sampledHeight),
        };
        context.UpdateSubresource(in constants, _constants);

        using ID3D11ShaderResourceView frameView = _device.Device.CreateShaderResourceView(frame);

        context.ClearState();
        context.ClearUnorderedAccessView(_countsView, new Vortice.Mathematics.Int4(0, 0, 0, 0));
        context.CSSetConstantBuffer(0, _constants);
        context.CSSetShaderResource(0, frameView);
        context.CSSetUnorderedAccessView(0, _countsView);
        context.CSSetShader(_accumulate);
        context.Dispatch((uint)((sampledWidth + 15) / 16), (uint)((sampledHeight + 15) / 16), 1);
        context.CSSetShaderResource(0, null);

        Draw(context, _drawWave!, _waveform);
        Draw(context, _drawParade!, _parade);
        Draw(context, _drawVector!, _vector);
        context.ClearState();

        int index = _next;
        _next = (_next + 1) % _slots.Length;
        if (_pending.Count == _slots.Length)
        {
            _pending.Dequeue();
        }

        _slots[index].Copy(context, _counts, width, height, sampledWidth * sampledHeight);
        _pending.Enqueue(index);
    }

    /// <summary>
    /// The oldest submitted frame's results, or null when none is waiting or, unless
    /// <paramref name="wait"/>, the GPU has not finished it yet.
    /// </summary>
    public ScopeReading? Collect(bool wait = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending.TryPeek(out int index))
        {
            return null;
        }

        ScopeReading? reading = _slots[index].Read(_device.ImmediateContext, wait);
        if (reading is not null)
        {
            _pending.Dequeue();
        }

        return reading;
    }

    /// <summary>Measures a frame and waits for the results.</summary>
    public ScopeReading Measure(ID3D11Texture2D frame)
    {
        _pending.Clear();
        Submit(frame);
        return Collect(wait: true)!;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Slot slot in _slots)
        {
            slot.Dispose();
        }

        _waveform.Dispose();
        _parade.Dispose();
        _vector.Dispose();
        ReleaseShaders();
        _constants.Dispose();
        _countsView.Dispose();
        _counts.Dispose();
    }

    private static void Draw(ID3D11DeviceContext context, ID3D11ComputeShader shader, Picture picture)
    {
        context.CSSetUnorderedAccessView(1, picture.View);
        context.CSSetShader(shader);
        context.Dispatch((uint)((picture.Width + 15) / 16), (uint)((picture.Height + 15) / 16), 1);
        context.CSSetUnorderedAccessView(1, null);
    }

    private void EnsureShaders()
    {
        if (_accumulate is not null && _generation == ShaderLibrary.Generation)
        {
            return;
        }

        ReleaseShaders();
        _accumulate = ShaderLibrary.ComputeShader(_device, "Scopes.hlsl", "CsAccumulate");
        _drawWave = ShaderLibrary.ComputeShader(_device, "Scopes.hlsl", "CsWaveform");
        _drawParade = ShaderLibrary.ComputeShader(_device, "Scopes.hlsl", "CsParade");
        _drawVector = ShaderLibrary.ComputeShader(_device, "Scopes.hlsl", "CsVector");
        _generation = ShaderLibrary.Generation;
    }

    private void ReleaseShaders()
    {
        _accumulate?.Dispose();
        _drawWave?.Dispose();
        _drawParade?.Dispose();
        _drawVector?.Dispose();
        _accumulate = _drawWave = _drawParade = _drawVector = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScopeConstants
    {
        public uint Width;
        public uint Height;
        public uint WaveWidth;
        public uint ParadeWidth;
        public uint WaveBase;
        public uint ParadeBase;
        public uint VectorBase;
        public uint Step;
        public float WaveGain;
        public float VectorGain;
        public float ParadeGain;
        public float Padding3;
    }

    /// <summary>A scope picture the draw passes write.</summary>
    private sealed class Picture : IDisposable
    {
        public Picture(ID3D11Device device, int width, int height)
        {
            Width = width;
            Height = height;
            Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Usage = ResourceUsage.Default,
            });
            View = device.CreateUnorderedAccessView(Texture);
        }

        public int Width { get; }

        public int Height { get; }

        public ID3D11Texture2D Texture { get; }

        public ID3D11UnorderedAccessView View { get; }

        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }

    /// <summary>One set of staging copies: the histogram counts and the three pictures.</summary>
    private sealed class Slot : IDisposable
    {
        private readonly ID3D11Buffer _histogram;
        private readonly (Picture Source, ID3D11Texture2D Staging)[] _pictures;
        private int _width;
        private int _height;
        private long _sampled;

        public Slot(ID3D11Device device, Picture waveform, Picture parade, Picture vector)
        {
            _histogram = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = HistogramCounts * 4,
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            _pictures = [.. new[] { waveform, parade, vector }.Select(picture => (picture, device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)picture.Width,
                Height = (uint)picture.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            })))];
        }

        public void Copy(ID3D11DeviceContext context, ID3D11Buffer counts, int width, int height, long sampled)
        {
            _width = width;
            _height = height;
            _sampled = sampled;
            context.CopySubresourceRegion(_histogram, 0, 0, 0, 0, counts, 0, new Vortice.Mathematics.Box(0, 0, 0, HistogramCounts * 4, 1, 1));
            foreach ((Picture source, ID3D11Texture2D staging) in _pictures)
            {
                context.CopyResource(staging, source.Texture);
            }
        }

        public unsafe ScopeReading? Read(ID3D11DeviceContext context, bool wait)
        {
            // The histogram is copied last of all the reads below would need; if it is not ready,
            // nothing is.
            MapFlags flags = wait ? MapFlags.None : MapFlags.DoNotWait;
            Result result = context.Map(_histogram, 0, MapMode.Read, flags, out MappedSubresource mapped);
            if (result.Failure)
            {
                return null;
            }

            int[] counts = new int[HistogramCounts];
            try
            {
                new ReadOnlySpan<int>((void*)mapped.DataPointer, HistogramCounts).CopyTo(counts);
            }
            finally
            {
                context.Unmap(_histogram, 0);
            }

            var pictures = new ScopePicture[_pictures.Length];
            for (int index = 0; index < _pictures.Length; index++)
            {
                (Picture source, ID3D11Texture2D staging) = _pictures[index];
                MappedSubresource picture = context.Map(staging, 0, MapMode.Read);
                try
                {
                    byte[] bgra = new byte[source.Width * source.Height * 4];
                    for (int row = 0; row < source.Height; row++)
                    {
                        new ReadOnlySpan<byte>((byte*)picture.DataPointer + (row * (int)picture.RowPitch), source.Width * 4).CopyTo(bgra.AsSpan(row * source.Width * 4));
                    }

                    pictures[index] = new ScopePicture(source.Width, source.Height, bgra);
                }
                finally
                {
                    context.Unmap(staging, 0);
                }
            }

            return new ScopeReading(
                _width,
                _height,
                counts[0..256],
                counts[256..512],
                counts[512..768],
                counts[768..1024],
                pictures[0],
                pictures[1],
                pictures[2],
                _sampled);
        }

        public void Dispose()
        {
            _histogram.Dispose();
            foreach ((_, ID3D11Texture2D staging) in _pictures)
            {
                staging.Dispose();
            }
        }
    }
}
