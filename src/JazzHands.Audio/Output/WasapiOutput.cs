using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Serilog;

namespace JazzHands.Audio.Output;

/// <summary>A playback device, as the device list shows it.</summary>
/// <param name="Id">The endpoint identifier, which is what a setting stores.</param>
/// <param name="Name">The name Windows shows.</param>
/// <param name="IsDefault">True for the device Windows plays to by default.</param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Plays mixed audio through WASAPI in shared mode, event driven.
/// </summary>
/// <remarks>
/// One thread of its own, raised to the Pro Audio MMCSS class, waits for the device to say it has
/// room and fills exactly that much. The mix is handed over at the project rate and channel count
/// and Windows converts it to the device's format (<c>AUTOCONVERTPCM</c>), so a 44.1 kHz headset
/// and a 48 kHz project need nothing from us. That conversion is the one place the project's
/// samples are resampled by somebody else, and it only ever affects what is heard, never what is
/// exported.
///
/// The device is followed: when the Windows default changes, or the device in use is unplugged,
/// the loop tears down and opens the new one without the caller noticing more than a short gap.
/// Frames given to the old device and not yet played count as played, so the clock jumps forward
/// by at most a buffer rather than going backwards.
///
/// <see cref="FramesPlayed"/> comes from the device's own clock, sampled every time the loop
/// wakes and extrapolated between wakes with the performance counter, so the video side can read
/// a position to well under a millisecond without talking to COM.
/// </remarks>
public sealed partial class WasapiOutput : IAudioOutput
{
    private const long HundredNanosecondsPerSecond = 10_000_000;

    private readonly ILogger _log = Log.ForContext<WasapiOutput>();
    private volatile string? _deviceId;
    private readonly long _bufferHns;
    private readonly WaveFormat _format;
    private readonly AutoResetEvent _changed = new(false);
    private IAudioRenderCallback? _callback;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _deviceChanged;
    private volatile string _deviceName = "none";
    private string? _currentDeviceId;
    private AudioBuffer _planar;
    private long _submitted;
    private long _playedBase;
    private long _underruns;

    // The last clock reading, as a pair under a sequence number: the frames played and the
    // performance counter when they were read, so a reader can extrapolate.
    private long _clockSequence;
    private long _clockFrames;
    private long _clockTimestamp;

    /// <summary>Creates an output. Nothing is opened until <see cref="Start"/>.</summary>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The mix channel count.</param>
    /// <param name="deviceId">A device to use, or null to follow the Windows default.</param>
    /// <param name="bufferLength">How much the device holds. 20 ms by default: two of the engine's periods.</param>
    public WasapiOutput(int sampleRate, int channels, string? deviceId = null, TimeSpan? bufferLength = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, Dsp.MaxChannels);

        SampleRate = sampleRate;
        Channels = channels;
        _deviceId = deviceId;
        _bufferHns = (bufferLength ?? TimeSpan.FromMilliseconds(20)).Ticks;
        _format = new WaveFormatExtensible(sampleRate, 32, channels);
        _planar = new AudioBuffer(channels, 1);
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public bool IsRunning => _thread is not null;

    /// <inheritdoc />
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <inheritdoc />
    public string DeviceName => _deviceName;

    /// <summary>Times the output moved to another device.</summary>
    public int DeviceSwitches { get; private set; }

    /// <summary>
    /// The device chosen, or null to follow the Windows default. Setting it while playing moves the
    /// sound there at the next buffer, the way an unplugged device is handled, with no restart.
    /// </summary>
    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            if (string.Equals(_deviceId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _deviceId = value;
            _deviceChanged = true;
            _changed.Set();
        }
    }

    /// <inheritdoc />
    public long FramesPlayed
    {
        get
        {
            long frames;
            long timestamp;
            long sequence;

            do
            {
                sequence = Volatile.Read(ref _clockSequence);
                frames = Volatile.Read(ref _clockFrames);
                timestamp = Volatile.Read(ref _clockTimestamp);
            }
            while ((sequence & 1) != 0 || sequence != Volatile.Read(ref _clockSequence));

            if (timestamp == 0)
            {
                return frames;
            }

            // Between wakes the device keeps playing at its rate; never past what it was given.
            long elapsed = Stopwatch.GetTimestamp() - timestamp;
            long extrapolated = frames + (elapsed * SampleRate / Stopwatch.Frequency);
            return Math.Min(extrapolated, Interlocked.Read(ref _submitted));
        }
    }

    /// <summary>The playback devices Windows knows about.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID
            : null;

        var devices = new List<AudioDeviceInfo>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return devices;
    }

    /// <summary>True when there is a playback device to open, so a test can skip rather than fail.</summary>
    public static bool HasDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Start(IAudioRenderCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_thread is not null)
        {
            throw new InvalidOperationException("The output is already running.");
        }

        _callback = callback;
        _stopping = false;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "Jazz audio output",
            Priority = ThreadPriority.Highest,
        };

        _thread.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_thread is not { } thread)
        {
            return;
        }

        _stopping = true;
        _changed.Set();
        thread.Join();
        _thread = null;
        _callback = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _changed.Dispose();
    }

    private void RenderLoop()
    {
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

        using var enumerator = new MMDeviceEnumerator();
        var notifications = new DeviceNotifications(this);
        enumerator.RegisterEndpointNotificationCallback(notifications);

        try
        {
            while (!_stopping)
            {
                try
                {
                    RunDevice(enumerator);
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException)
                {
                    // Unplugged mid-write, or no device at all. Wait for one rather than giving up:
                    // plugging headphones back in should bring the sound back.
                    _log.Warning(exception, "Audio output lost its device; retrying");
                    _changed.WaitOne(500);
                }

                if (_deviceChanged && !_stopping)
                {
                    DeviceSwitches++;
                }
            }
        }
        finally
        {
            enumerator.UnregisterEndpointNotificationCallback(notifications);

            if (mmcss != IntPtr.Zero)
            {
                AvRevertMmThreadCharacteristics(mmcss);
            }
        }
    }

    private MMDevice OpenDevice(MMDeviceEnumerator enumerator)
    {
        if (_deviceId is { } id)
        {
            try
            {
                MMDevice chosen = enumerator.GetDevice(id);
                if (chosen.State == DeviceState.Active)
                {
                    return chosen;
                }

                chosen.Dispose();
            }
            catch (COMException)
            {
            }

            _log.Warning("Audio device {Device} is not available; playing to the default device", id);
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void RunDevice(MMDeviceEnumerator enumerator)
    {
        _deviceChanged = false;

        using MMDevice device = OpenDevice(enumerator);
        using AudioClient client = device.AudioClient;

        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            _bufferHns,
            0,
            _format,
            Guid.Empty);

        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());

        int bufferFrames = client.BufferSize;
        if (_planar.Capacity < bufferFrames)
        {
            _planar = new AudioBuffer(Channels, bufferFrames);
        }

        AudioRenderClient render = client.AudioRenderClient;
        AudioClockClient clock = client.AudioClockClient;
        long frequency = (long)clock.Frequency;

        _currentDeviceId = device.ID;
        _deviceName = device.FriendlyName;
        _log.Information(
            "Audio output on {Device}: {Rate} Hz, {Channels} channels, {Frames} frame buffer",
            _deviceName,
            SampleRate,
            Channels,
            bufferFrames);

        // Prime the whole buffer so the device starts with something to play.
        Fill(render, bufferFrames);
        client.Start();

        WaitHandle[] handles = [wake, _changed];

        try
        {
            while (!_stopping && !_deviceChanged)
            {
                WaitHandle.WaitAny(handles, 200);

                if (_stopping || _deviceChanged)
                {
                    break;
                }

                int padding = client.CurrentPadding;
                if (padding == 0)
                {
                    // The device got to the end of what it had: whatever it played next was a gap.
                    Interlocked.Increment(ref _underruns);
                }

                int room = bufferFrames - padding;
                if (room > 0)
                {
                    Fill(render, room);
                }

                clock.GetPosition(out ulong position, out _);
                PublishClock(_playedBase + (long)((double)position * SampleRate / frequency));
            }
        }
        finally
        {
            client.Stop();

            // What the device was given and did not play is gone with it. Counting it as played
            // keeps the clock monotonic across a device change.
            _playedBase = Interlocked.Read(ref _submitted);
            PublishClock(_playedBase);
        }
    }

    private unsafe void Fill(AudioRenderClient render, int frames)
    {
        IntPtr target = render.GetBuffer(frames);
        float* samples = (float*)target;
        IAudioRenderCallback callback = _callback!;
        int channels = Channels;
        int done = 0;

        while (done < frames)
        {
            int count = Math.Min(frames - done, _planar.Capacity);

            try
            {
                callback.Render(_planar, count, Interlocked.Read(ref _submitted));
            }
            catch (Exception exception)
            {
                // A bug in the mix must not take the output thread down with it; the next
                // callback gets another chance and this one plays silence.
                _planar.Clear(0, count);
                _log.Error(exception, "The audio render callback threw");
            }

            // Planar to interleaved, which is what the device wants.
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<float> plane = _planar.Plane(channel, 0, count);
                float* write = samples + ((long)done * channels) + channel;

                for (int index = 0; index < count; index++)
                {
                    write[(long)index * channels] = plane[index];
                }
            }

            Interlocked.Add(ref _submitted, count);
            done += count;
        }

        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    private void PublishClock(long frames)
    {
        Interlocked.Increment(ref _clockSequence);
        Volatile.Write(ref _clockFrames, frames);
        Volatile.Write(ref _clockTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _clockSequence);
    }

    private void OnDeviceChanged(string? id)
    {
        bool ours = id is not null && string.Equals(id, _currentDeviceId, StringComparison.OrdinalIgnoreCase);
        if (ours || id is null)
        {
            _deviceChanged = true;
            _changed.Set();
        }
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>Windows telling us the default device changed or ours went away.</summary>
    private sealed class DeviceNotifications(WasapiOutput owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active)
            {
                owner.OnDeviceChanged(deviceId);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(string deviceId) => owner.OnDeviceChanged(deviceId);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Only when following the default: a device somebody chose stays chosen.
            if (flow == DataFlow.Render && role == Role.Multimedia && owner._deviceId is null)
            {
                owner.OnDeviceChanged(null);
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
        }
    }
}
