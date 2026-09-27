using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace JazzHands.Plugins.Clap;

/// <summary>A plugin a CLAP file offers: what its factory's descriptor says.</summary>
/// <param name="Id">Its identifier, reverse domain style (<c>com.u-he.diva</c>).</param>
/// <param name="Name">Its name.</param>
/// <param name="Vendor">Who makes it.</param>
/// <param name="Version">Its version.</param>
/// <param name="Description">What it says it does.</param>
/// <param name="Features">Its feature tags (<c>audio-effect</c>, <c>reverb</c>, <c>instrument</c>).</param>
public sealed record ClapDescriptor(string Id, string Name, string Vendor, string Version, string Description, IReadOnlyList<string> Features);

/// <summary>One of a plugin's parameters.</summary>
/// <param name="Id">Its CLAP id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Module">Where it sits in the plugin's own grouping, slash separated.</param>
/// <param name="Min">Its lowest plain value.</param>
/// <param name="Max">Its highest.</param>
/// <param name="Default">Its default.</param>
/// <param name="Flags">clap_param_info_flags.</param>
public sealed record ClapParameter(uint Id, string Name, string Module, double Min, double Max, double Default, uint Flags)
{
    /// <summary>True for one the person may change: not hidden, not read only.</summary>
    public bool IsEditable => (Flags & (ClapConstants.ParamIsHidden | ClapConstants.ParamIsReadonly)) == 0;
}

/// <summary>
/// A loaded .clap file (Phase 46): the library, its entry initialised, and its plugin factory.
/// </summary>
/// <remarks>
/// The entry's init runs once when the file is opened and deinit when it is disposed, as CLAP asks.
/// Loading runs the plugin's own code, which can crash the process: the editor never does this in
/// its own process, only in jazz-plugin-host.exe (see <see cref="PluginProcess"/>).
/// </remarks>
public sealed unsafe class ClapLibrary : IDisposable
{
    private readonly nint _handle;
    private readonly ClapPluginEntry* _entry;
    private readonly ClapPluginFactory* _factory;
    private bool _disposed;

    private ClapLibrary(string path, nint handle, ClapPluginEntry* entry, ClapPluginFactory* factory)
    {
        Path = path;
        _handle = handle;
        _entry = entry;
        _factory = factory;
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>Opens a .clap file: loads it, initialises its entry and finds its plugin factory.</summary>
    public static ClapLibrary Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        nint handle = NativeLibrary.Load(path);
        if (!NativeLibrary.TryGetExport(handle, "clap_entry", out nint symbol))
        {
            NativeLibrary.Free(handle);
            throw new InvalidDataException($"{path} is not a CLAP plugin: it has no clap_entry.");
        }

        var entry = (ClapPluginEntry*)symbol;
        if (entry->ClapVersion.Major < 1)
        {
            NativeLibrary.Free(handle);
            throw new InvalidDataException($"{path} was built for CLAP {entry->ClapVersion.Major}.{entry->ClapVersion.Minor}, which this host does not speak.");
        }

        using (var utf8 = new Utf8(path))
        {
            if (entry->Init(utf8.Pointer) == 0)
            {
                NativeLibrary.Free(handle);
                throw new InvalidDataException($"{path} refused to start (its entry's init failed).");
            }
        }

        ClapPluginFactory* factory;
        using (var id = new Utf8(ClapConstants.PluginFactoryId))
        {
            factory = (ClapPluginFactory*)entry->GetFactory(id.Pointer);
        }

        if (factory is null)
        {
            entry->Deinit();
            NativeLibrary.Free(handle);
            throw new InvalidDataException($"{path} offers no plugin factory.");
        }

        return new ClapLibrary(path, handle, entry, factory);
    }

    /// <summary>The plugins the file offers.</summary>
    public IReadOnlyList<ClapDescriptor> Plugins
    {
        get
        {
            var plugins = new List<ClapDescriptor>();
            uint count = _factory->GetPluginCount(_factory);
            for (uint index = 0; index < count; index++)
            {
                ClapPluginDescriptor* d = _factory->GetPluginDescriptor(_factory, index);
                if (d is null)
                {
                    continue;
                }

                var features = new List<string>();
                for (byte** feature = d->Features; feature is not null && *feature is not null; feature++)
                {
                    features.Add(Utf8.Read(*feature));
                }

                plugins.Add(new ClapDescriptor(Utf8.Read(d->Id), Utf8.Read(d->Name), Utf8.Read(d->Vendor), Utf8.Read(d->Version), Utf8.Read(d->Description), features));
            }

            return plugins;
        }
    }

    /// <summary>Makes one of the file's plugins.</summary>
    public ClapInstance Create(string pluginId) => new(this, pluginId);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entry->Deinit();
        NativeLibrary.Free(_handle);
    }

    /// <summary>The factory, for <see cref="ClapInstance"/>.</summary>
    internal ClapPluginFactory* Factory => _factory;
}

/// <summary>
/// One plugin made from a <see cref="ClapLibrary"/>: activated at a rate and block size, processing
/// stereo or mono float blocks with parameter changes, reporting its latency, saving and loading its
/// state. Every call but <see cref="Process"/> belongs on one thread (CLAP's main thread);
/// <see cref="Process"/> on another (its audio thread), never at the same time as a parameter flush.
/// </summary>
public sealed unsafe class ClapInstance : IDisposable
{
    private const int MaxEvents = 256;

    private readonly ClapPlugin* _plugin;
    private readonly ClapHost* _host;
    private readonly nint _hostName;
    private readonly ClapPluginParams* _params;
    private readonly ClapPluginLatency* _latency;
    private readonly ClapPluginState* _state;
    private readonly ClapPluginRender* _render;
    private readonly ClapEventParamValue* _events;
    private readonly EventList* _eventList;
    private readonly ClapInputEvents* _inEvents;
    private readonly ClapOutputEvents* _outEvents;
    private readonly ClapAudioBuffer* _inBuffer;
    private readonly ClapAudioBuffer* _outBuffer;
    private readonly float** _inPlanes;
    private readonly float** _outPlanes;
    private readonly ClapProcess* _process;
    private bool _active;
    private bool _processing;
    private bool _disposed;
    private long _steadyTime;

    internal ClapInstance(ClapLibrary library, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        _hostName = Marshal.StringToCoTaskMemUTF8("Jazz Hands");
        _host = (ClapHost*)NativeMemory.AllocZeroed((nuint)sizeof(ClapHost));
        _host->ClapVersion = ClapVersion.Current;
        _host->Name = (byte*)_hostName;
        _host->Vendor = (byte*)_hostName;
        _host->Url = (byte*)_hostName;
        _host->Version = (byte*)_hostName;
        _host->GetExtension = &HostGetExtension;
        _host->RequestRestart = &HostRequest;
        _host->RequestProcess = &HostRequest;
        _host->RequestCallback = &HostRequest;

        using (var id = new Utf8(pluginId))
        {
            _plugin = library.Factory->CreatePlugin(library.Factory, _host, id.Pointer);
        }

        if (_plugin is null)
        {
            Free();
            throw new InvalidDataException($"{library.Path} has no plugin '{pluginId}'.");
        }

        if (_plugin->Init(_plugin) == 0)
        {
            _plugin->Destroy(_plugin);
            Free();
            throw new InvalidDataException($"The plugin '{pluginId}' refused to start (its init failed).");
        }

        Id = pluginId;
        Name = Utf8.Read(_plugin->Desc->Name);
        _params = (ClapPluginParams*)Extension(ClapConstants.ExtParams);
        _latency = (ClapPluginLatency*)Extension(ClapConstants.ExtLatency);
        _state = (ClapPluginState*)Extension(ClapConstants.ExtState);
        _render = (ClapPluginRender*)Extension(ClapConstants.ExtRender);

        _events = (ClapEventParamValue*)NativeMemory.AllocZeroed((nuint)(sizeof(ClapEventParamValue) * MaxEvents));
        _eventList = (EventList*)NativeMemory.AllocZeroed((nuint)sizeof(EventList));
        _eventList->Events = _events;
        _inEvents = (ClapInputEvents*)NativeMemory.AllocZeroed((nuint)sizeof(ClapInputEvents));
        _inEvents->Ctx = _eventList;
        _inEvents->Size = &EventsSize;
        _inEvents->Get = &EventsGet;
        _outEvents = (ClapOutputEvents*)NativeMemory.AllocZeroed((nuint)sizeof(ClapOutputEvents));
        _outEvents->TryPush = &EventsDiscard;
        _inBuffer = (ClapAudioBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(ClapAudioBuffer));
        _outBuffer = (ClapAudioBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(ClapAudioBuffer));
        _inPlanes = (float**)NativeMemory.AllocZeroed((nuint)(sizeof(float*) * 8));
        _outPlanes = (float**)NativeMemory.AllocZeroed((nuint)(sizeof(float*) * 8));
        _process = (ClapProcess*)NativeMemory.AllocZeroed((nuint)sizeof(ClapProcess));
    }

    /// <summary>The plugin's id.</summary>
    public string Id { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>Samples its output lags its input by, as it says; 0 without the latency extension.</summary>
    public int Latency => _latency is null ? 0 : (int)_latency->Get(_plugin);

    /// <summary>Its parameters.</summary>
    public IReadOnlyList<ClapParameter> Parameters
    {
        get
        {
            var list = new List<ClapParameter>();
            if (_params is null)
            {
                return list;
            }

            uint count = _params->Count(_plugin);
            var info = default(ClapParamInfo);
            for (uint index = 0; index < count; index++)
            {
                if (_params->GetInfo(_plugin, index, &info) != 0)
                {
                    list.Add(new ClapParameter(info.Id, Utf8.Read(info.Name, 256), Utf8.Read(info.Module, 1024), info.MinValue, info.MaxValue, info.DefaultValue, info.Flags));
                }
            }

            return list;
        }
    }

    /// <summary>A parameter's value now, or NaN when the plugin will not say.</summary>
    public double Value(uint id)
    {
        double value = double.NaN;
        return _params is not null && _params->GetValue(_plugin, id, &value) != 0 ? value : double.NaN;
    }

    /// <summary>Sets parameters while the plugin is not processing, through its params flush.</summary>
    public void Set(ReadOnlySpan<(uint Id, double Value)> changes)
    {
        if (_params is null || changes.IsEmpty)
        {
            return;
        }

        if (_processing)
        {
            throw new InvalidOperationException("Parameters go in with the audio while the plugin is processing.");
        }

        Fill(changes, 0);
        _params->Flush(_plugin, _inEvents, _outEvents);
        _eventList->Count = 0;
    }

    /// <summary>Tells the plugin it is rendering offline (an export) or in real time.</summary>
    public void SetOffline(bool offline)
    {
        if (_render is not null)
        {
            _render->Set(_plugin, offline ? ClapConstants.RenderOffline : ClapConstants.RenderRealtime);
        }
    }

    /// <summary>Activates the plugin for a rate and a largest block, and starts it processing.</summary>
    public void Activate(double sampleRate, int maxFrames)
    {
        if (_active)
        {
            return;
        }

        if (_plugin->Activate(_plugin, sampleRate, 1, (uint)maxFrames) == 0)
        {
            throw new InvalidOperationException($"The plugin '{Id}' refused to start at {sampleRate} Hz.");
        }

        _active = true;
    }

    /// <summary>
    /// Runs one block: <paramref name="channels"/> input planes into as many output planes, with
    /// parameter changes landing at their sample offsets. False when the plugin reported an error.
    /// </summary>
    public bool Process(float** input, float** output, int channels, int frames, ReadOnlySpan<(uint Id, double Value, int Offset)> changes)
    {
        if (!_active)
        {
            return false;
        }

        if (!_processing)
        {
            _processing = _plugin->StartProcessing(_plugin) != 0;
        }

        int count = Math.Min(channels, 8);
        for (int channel = 0; channel < count; channel++)
        {
            _inPlanes[channel] = input[channel];
            _outPlanes[channel] = output[channel];
        }

        _inBuffer->Data32 = _inPlanes;
        _inBuffer->ChannelCount = (uint)count;
        _outBuffer->Data32 = _outPlanes;
        _outBuffer->ChannelCount = (uint)count;

        int events = 0;
        foreach ((uint id, double value, int offset) in changes)
        {
            if (events == MaxEvents)
            {
                break;
            }

            Event(events++, id, value, (uint)Math.Clamp(offset, 0, frames - 1));
        }

        _eventList->Count = events;
        _process->SteadyTime = _steadyTime;
        _process->FramesCount = (uint)frames;
        _process->AudioInputs = _inBuffer;
        _process->AudioOutputs = _outBuffer;
        _process->AudioInputsCount = 1;
        _process->AudioOutputsCount = 1;
        _process->InEvents = _inEvents;
        _process->OutEvents = _outEvents;
        int status = _plugin->Process(_plugin, _process);
        _eventList->Count = 0;
        _steadyTime += frames;
        return status != ClapConstants.ProcessError;
    }

    /// <summary>The plugin's state, as it saves it; empty without the state extension.</summary>
    public byte[] SaveState()
    {
        if (_state is null)
        {
            return [];
        }

        var stream = new MemoryStream();
        GCHandle handle = GCHandle.Alloc(stream);
        try
        {
            var output = new ClapOStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Write = &StreamWrite };
            return _state->Save(_plugin, &output) != 0 ? stream.ToArray() : [];
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Gives the plugin a state it saved; false when it refused it.</summary>
    public bool LoadState(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_state is null || data.Length == 0)
        {
            return false;
        }

        var stream = new MemoryStream(data, writable: false);
        GCHandle handle = GCHandle.Alloc(stream);
        try
        {
            var input = new ClapIStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Read = &StreamRead };
            return _state->Load(_plugin, &input) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Stops processing and deactivates.</summary>
    public void Deactivate()
    {
        if (_processing)
        {
            _plugin->StopProcessing(_plugin);
            _processing = false;
        }

        if (_active)
        {
            _plugin->Deactivate(_plugin);
            _active = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Deactivate();
        _plugin->Destroy(_plugin);
        Free();
    }

    private void* Extension(string id)
    {
        using var utf8 = new Utf8(id);
        return _plugin->GetExtension(_plugin, utf8.Pointer);
    }

    private void Fill(ReadOnlySpan<(uint Id, double Value)> changes, uint time)
    {
        int events = 0;
        foreach ((uint id, double value) in changes)
        {
            if (events == MaxEvents)
            {
                break;
            }

            Event(events++, id, value, time);
        }

        _eventList->Count = events;
    }

    private void Event(int index, uint id, double value, uint time)
    {
        ClapEventParamValue* e = _events + index;
        e->Header.Size = (uint)sizeof(ClapEventParamValue);
        e->Header.Time = time;
        e->Header.SpaceId = ClapConstants.CoreEventSpace;
        e->Header.Type = ClapConstants.EventParamValue;
        e->Header.Flags = 0;
        e->ParamId = id;
        e->Cookie = null;
        e->NoteId = -1;
        e->PortIndex = -1;
        e->Channel = -1;
        e->Key = -1;
        e->Value = value;
    }

    private void Free()
    {
        NativeMemory.Free(_host);
        Marshal.FreeCoTaskMem(_hostName);
        NativeMemory.Free(_events);
        NativeMemory.Free(_eventList);
        NativeMemory.Free(_inEvents);
        NativeMemory.Free(_outEvents);
        NativeMemory.Free(_inBuffer);
        NativeMemory.Free(_outBuffer);
        NativeMemory.Free(_inPlanes);
        NativeMemory.Free(_outPlanes);
        NativeMemory.Free(_process);
    }

    [UnmanagedCallersOnly]
    private static void* HostGetExtension(ClapHost* host, byte* id) => null;

    [UnmanagedCallersOnly]
    private static void HostRequest(ClapHost* host)
    {
    }

    [UnmanagedCallersOnly]
    private static uint EventsSize(ClapInputEvents* list) => (uint)((EventList*)list->Ctx)->Count;

    [UnmanagedCallersOnly]
    private static ClapEventHeader* EventsGet(ClapInputEvents* list, uint index)
    {
        var events = (EventList*)list->Ctx;
        return index < events->Count ? &events->Events[index].Header : null;
    }

    [UnmanagedCallersOnly]
    private static byte EventsDiscard(ClapOutputEvents* list, ClapEventHeader* e) => 1;

    [UnmanagedCallersOnly]
    private static long StreamWrite(ClapOStream* stream, void* buffer, ulong size)
    {
        var target = (MemoryStream)GCHandle.FromIntPtr((nint)stream->Ctx).Target!;
        target.Write(new ReadOnlySpan<byte>(buffer, (int)size));
        return (long)size;
    }

    [UnmanagedCallersOnly]
    private static long StreamRead(ClapIStream* stream, void* buffer, ulong size)
    {
        var source = (MemoryStream)GCHandle.FromIntPtr((nint)stream->Ctx).Target!;
        return source.Read(new Span<byte>(buffer, (int)Math.Min(size, int.MaxValue)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventList
    {
        public ClapEventParamValue* Events;
        public int Count;
    }
}

/// <summary>A string as NUL terminated UTF-8 in native memory, for as long as it is held.</summary>
internal readonly unsafe ref struct Utf8
{
    private readonly nint _memory;

    public Utf8(string text) => _memory = Marshal.StringToCoTaskMemUTF8(text);

    public byte* Pointer => (byte*)_memory;

    public static string Read(byte* text) => text is null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    public static string Read(byte* text, int capacity)
    {
        int length = 0;
        while (length < capacity && text[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(text, length);
    }

    public void Dispose() => Marshal.FreeCoTaskMem(_memory);
}
