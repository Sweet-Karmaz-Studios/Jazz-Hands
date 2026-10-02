using System.Collections.Concurrent;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Render;
using JazzHands.Render.Compositing;
using JazzHands.Render.Scopes;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Engine.Frames;

/// <summary>One frame rendered for reading on the CPU: linear light, the delivered signal, the scopes.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Linear">Premultiplied linear BT.709, four floats a pixel, top row first.</param>
/// <param name="Bgra">The same frame encoded as delivered (BT.1886, eight bits), BGRA.</param>
/// <param name="Scopes">What the scopes read on the delivered frame.</param>
public sealed record StillFrame(int Width, int Height, float[] Linear, byte[] Bgra, ScopeReading Scopes);

/// <summary>
/// Renders single frames for queries that need to look at the picture: the eyedropper, the scopes
/// read headless, and later <c>jazz frame</c>.
/// </summary>
/// <remarks>
/// A frame server of its own on WARP, made when first asked for, so a session that never looks at
/// a frame never makes a device and a query never competes with playback for the GPU. Full
/// quality, bilinear, the way an export would draw it.
///
/// Callers arrive on any thread (a query on the caller's, a command on the session's), but a
/// frame server's demuxers and decoders belong to the thread that made them, and one used from
/// another throws and is then left out as unreadable. So every frame is drawn on the renderer's
/// own thread, made with the device, one at a time, and the caller waits for it.
/// </remarks>
public sealed class StillRenderer : IDisposable
{
    private readonly Lock _gate = new();
    private readonly RenderDevice? _given;
    private BlockingCollection<Action>? _work;
    private Thread? _thread;
    private RenderDevice? _device;
    private FrameServer? _frames;
    private ScopeRenderer? _scopes;
    private bool _disposed;

    /// <summary>Creates the renderer; the device, WARP unless one is given, is made on first use.</summary>
    public StillRenderer(RenderDevice? device = null)
    {
        _given = device;
    }

    /// <summary>Renders a sequence at a time and reads it back.</summary>
    /// <param name="project">The project.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="time">The sequence time.</param>
    /// <param name="projectPath">Where the project lives, for its media.</param>
    /// <param name="workingScopes">Measure the scopes on the working space (Phase 44) rather than the output.</param>
    /// <param name="compView">A comp graph node to look at instead of its graph's output (Phase 49), or null.</param>
    public StillFrame Render(Project project, Sequence sequence, Flicks time, string projectPath = "", bool workingScopes = false, string? compView = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);

        return OnOwnThread(() =>
        {
            _scopes ??= new ScopeRenderer(_device!);

            RenderTarget stack = _frames!.Render(project, sequence, time, new RenderOptions { Bicubic = false, CompView = compView }, projectPath);
            RenderTarget display = _frames.Compositor.Pool.Rent(stack.Width, stack.Height, Format.B8G8R8A8_UNorm);
            try
            {
                _frames.Compositor.Output(stack, display.View, stack.Width, stack.Height, new OutputSettings(DitherLevels: 0));
                float[] linear = ReadFloats(stack);
                byte[] bgra = ReadBytes(display);
                ScopeReading scopes;
                if (workingScopes)
                {
                    RenderTarget working = _frames.Compositor.Pool.Rent(stack.Width, stack.Height, Format.B8G8R8A8_UNorm);
                    try
                    {
                        _frames.Compositor.OutputWorking(stack, working.View, stack.Width, stack.Height);
                        scopes = _scopes.Measure(working.Texture);
                    }
                    finally
                    {
                        _frames.Compositor.Pool.Return(working);
                    }
                }
                else
                {
                    scopes = _scopes.Measure(display.Texture);
                }

                return new StillFrame(stack.Width, stack.Height, linear, bgra, scopes);
            }
            finally
            {
                _frames.Compositor.Pool.Return(display);
                _frames.Compositor.Pool.Return(stack);
            }
        });
    }

    /// <summary>
    /// Draws a frame exactly as the editor's preview draws it at this size: the same scale, the
    /// same sampling, BT.1886 and dithered for eight bits, so a PNG of it looks on screen as the
    /// preview does. BGRA, top row first. This is <c>jazz frame</c>.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="time">The sequence time.</param>
    /// <param name="width">The picture's width; the height follows the sequence's shape.</param>
    /// <param name="projectPath">Where the project lives, for its media.</param>
    public (int Width, int Height, byte[] Bgra) RenderPreview(Project project, Sequence sequence, Flicks time, int width, string projectPath = "", string? compView = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);

        ProjectSettings settings = project.SettingsFor(sequence);
        int height = Math.Max(2, (int)Math.Round(width * (double)settings.Height / settings.Width / 2) * 2);

        return OnOwnThread(() =>
        {
            RenderTarget display = _frames!.Compositor.Pool.Rent(width, height, Format.B8G8R8A8_UNorm);
            try
            {
                // The preview renders at a fraction of the sequence's size, then fits the panel.
                var options = new RenderOptions { Scale = Math.Min(1f, (float)width / settings.Width), CompView = compView };
                _frames.Render(project, sequence, time, options, display.View, width, height, OutputSettings.Preview, projectPath);
                return (width, height, ReadBytes(display));
            }
            finally
            {
                _frames.Compositor.Pool.Return(display);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            if (_work is { } work)
            {
                // What the thread made, it frees, after anything already asked for.
                work.Add(Release);
                work.CompleteAdding();
            }
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
            _work?.Dispose();
        }
    }

    /// <summary>Runs a piece of work on the renderer's own thread, starting it first time, and waits for it.</summary>
    private T OnOwnThread<T>(Func<T> job)
    {
        if (Thread.CurrentThread == _thread)
        {
            return job();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_work is null)
            {
                _work = new BlockingCollection<Action>();
                BlockingCollection<Action> queue = _work;
                _thread = new Thread(() =>
                {
                    foreach (Action action in queue.GetConsumingEnumerable())
                    {
                        action();
                    }
                })
                { IsBackground = true, Name = "Jazz still renderer" };
                _thread.Start();
            }

            _work.Add(() =>
            {
                try
                {
                    _device ??= _given ?? RenderDevice.Create(forceWarp: true);
                    _frames ??= new FrameServer(_device);
                    done.SetResult(job());
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            });
        }

        return done.Task.GetAwaiter().GetResult();
    }

    private void Release()
    {
        _scopes?.Dispose();
        _frames?.Dispose();
        if (_given is null)
        {
            _device?.Dispose();
        }
    }

    private unsafe float[] ReadFloats(RenderTarget target)
    {
        using ID3D11Texture2D staging = _device!.CreateStagingTexture(target.Texture);
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.CopyResource(staging, target.Texture);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            float[] pixels = new float[target.Width * target.Height * 4];
            for (int row = 0; row < target.Height; row++)
            {
                var halves = new ReadOnlySpan<Half>((byte*)mapped.DataPointer + (row * (int)mapped.RowPitch), target.Width * 4);
                for (int index = 0; index < halves.Length; index++)
                {
                    pixels[(row * target.Width * 4) + index] = (float)halves[index];
                }
            }

            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private unsafe byte[] ReadBytes(RenderTarget target)
    {
        using ID3D11Texture2D staging = _device!.CreateStagingTexture(target.Texture);
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.CopyResource(staging, target.Texture);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            byte[] pixels = new byte[target.Width * target.Height * 4];
            for (int row = 0; row < target.Height; row++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (row * (int)mapped.RowPitch), target.Width * 4).CopyTo(pixels.AsSpan(row * target.Width * 4));
            }

            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
