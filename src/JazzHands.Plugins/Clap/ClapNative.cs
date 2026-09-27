using System.Runtime.InteropServices;

namespace JazzHands.Plugins.Clap;

// The CLAP 1.2 ABI as C# structs (third_party/clap/include, the pinned 1.2.10 headers): every
// field in header order with its C type, so sequential layout on x64 gives the same offsets as
// MSVC. Function pointers are unmanaged calls in the platform's convention. Only what the host
// uses is here: the entry, the plugin factory, the plugin, the host, process, events, and the
// audio-ports, params, latency, state, render and gui extensions.

#pragma warning disable SA1600, SA1602, CA1051, SA1307, SA1310, SA1401 // a C ABI, named as the headers name it

/// <summary>clap_version_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ClapVersion
{
    public uint Major;
    public uint Minor;
    public uint Revision;

    public static ClapVersion Current => new() { Major = 1, Minor = 2, Revision = 10 };
}

/// <summary>clap_plugin_entry_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginEntry
{
    public ClapVersion ClapVersion;
    public delegate* unmanaged<byte*, byte> Init;
    public delegate* unmanaged<void> Deinit;
    public delegate* unmanaged<byte*, void*> GetFactory;
}

/// <summary>clap_plugin_factory_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginFactory
{
    public delegate* unmanaged<ClapPluginFactory*, uint> GetPluginCount;
    public delegate* unmanaged<ClapPluginFactory*, uint, ClapPluginDescriptor*> GetPluginDescriptor;
    public delegate* unmanaged<ClapPluginFactory*, ClapHost*, byte*, ClapPlugin*> CreatePlugin;
}

/// <summary>clap_plugin_descriptor_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginDescriptor
{
    public ClapVersion ClapVersion;
    public byte* Id;
    public byte* Name;
    public byte* Vendor;
    public byte* Url;
    public byte* ManualUrl;
    public byte* SupportUrl;
    public byte* Version;
    public byte* Description;
    public byte** Features;
}

/// <summary>clap_plugin_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPlugin
{
    public ClapPluginDescriptor* Desc;
    public void* PluginData;
    public delegate* unmanaged<ClapPlugin*, byte> Init;
    public delegate* unmanaged<ClapPlugin*, void> Destroy;
    public delegate* unmanaged<ClapPlugin*, double, uint, uint, byte> Activate;
    public delegate* unmanaged<ClapPlugin*, void> Deactivate;
    public delegate* unmanaged<ClapPlugin*, byte> StartProcessing;
    public delegate* unmanaged<ClapPlugin*, void> StopProcessing;
    public delegate* unmanaged<ClapPlugin*, void> Reset;
    public delegate* unmanaged<ClapPlugin*, ClapProcess*, int> Process;
    public delegate* unmanaged<ClapPlugin*, byte*, void*> GetExtension;
    public delegate* unmanaged<ClapPlugin*, void> OnMainThread;
}

/// <summary>clap_host_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapHost
{
    public ClapVersion ClapVersion;
    public void* HostData;
    public byte* Name;
    public byte* Vendor;
    public byte* Url;
    public byte* Version;
    public delegate* unmanaged<ClapHost*, byte*, void*> GetExtension;
    public delegate* unmanaged<ClapHost*, void> RequestRestart;
    public delegate* unmanaged<ClapHost*, void> RequestProcess;
    public delegate* unmanaged<ClapHost*, void> RequestCallback;
}

/// <summary>clap_process_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapProcess
{
    public long SteadyTime;
    public uint FramesCount;
    public void* Transport;
    public ClapAudioBuffer* AudioInputs;
    public ClapAudioBuffer* AudioOutputs;
    public uint AudioInputsCount;
    public uint AudioOutputsCount;
    public ClapInputEvents* InEvents;
    public ClapOutputEvents* OutEvents;
}

/// <summary>clap_audio_buffer_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapAudioBuffer
{
    public float** Data32;
    public double** Data64;
    public uint ChannelCount;
    public uint Latency;
    public ulong ConstantMask;
}

/// <summary>clap_event_header_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ClapEventHeader
{
    public uint Size;
    public uint Time;
    public ushort SpaceId;
    public ushort Type;
    public uint Flags;
}

/// <summary>clap_event_param_value_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapEventParamValue
{
    public ClapEventHeader Header;
    public uint ParamId;
    public void* Cookie;
    public int NoteId;
    public short PortIndex;
    public short Channel;
    public short Key;
    public double Value;
}

/// <summary>clap_input_events_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapInputEvents
{
    public void* Ctx;
    public delegate* unmanaged<ClapInputEvents*, uint> Size;
    public delegate* unmanaged<ClapInputEvents*, uint, ClapEventHeader*> Get;
}

/// <summary>clap_output_events_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapOutputEvents
{
    public void* Ctx;
    public delegate* unmanaged<ClapOutputEvents*, ClapEventHeader*, byte> TryPush;
}

/// <summary>clap_param_info_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapParamInfo
{
    public uint Id;
    public uint Flags;
    public void* Cookie;
    public fixed byte Name[256];
    public fixed byte Module[1024];
    public double MinValue;
    public double MaxValue;
    public double DefaultValue;
}

/// <summary>clap_plugin_params_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginParams
{
    public delegate* unmanaged<ClapPlugin*, uint> Count;
    public delegate* unmanaged<ClapPlugin*, uint, ClapParamInfo*, byte> GetInfo;
    public delegate* unmanaged<ClapPlugin*, uint, double*, byte> GetValue;
    public delegate* unmanaged<ClapPlugin*, uint, double, byte*, uint, byte> ValueToText;
    public delegate* unmanaged<ClapPlugin*, uint, byte*, double*, byte> TextToValue;
    public delegate* unmanaged<ClapPlugin*, ClapInputEvents*, ClapOutputEvents*, void> Flush;
}

/// <summary>clap_plugin_latency_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginLatency
{
    public delegate* unmanaged<ClapPlugin*, uint> Get;
}

/// <summary>clap_istream_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapIStream
{
    public void* Ctx;
    public delegate* unmanaged<ClapIStream*, void*, ulong, long> Read;
}

/// <summary>clap_ostream_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapOStream
{
    public void* Ctx;
    public delegate* unmanaged<ClapOStream*, void*, ulong, long> Write;
}

/// <summary>clap_plugin_state_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginState
{
    public delegate* unmanaged<ClapPlugin*, ClapOStream*, byte> Save;
    public delegate* unmanaged<ClapPlugin*, ClapIStream*, byte> Load;
}

/// <summary>clap_plugin_render_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginRender
{
    public delegate* unmanaged<ClapPlugin*, byte> HasHardRealtimeRequirement;
    public delegate* unmanaged<ClapPlugin*, int, byte> Set;
}

/// <summary>clap_audio_port_info_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapAudioPortInfo
{
    public uint Id;
    public fixed byte Name[256];
    public uint Flags;
    public uint ChannelCount;
    public byte* PortType;
    public uint InPlacePair;
}

/// <summary>clap_plugin_audio_ports_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginAudioPorts
{
    public delegate* unmanaged<ClapPlugin*, byte, uint> Count;
    public delegate* unmanaged<ClapPlugin*, uint, byte, ClapAudioPortInfo*, byte> Get;
}

/// <summary>clap_window_t (Windows: an HWND).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapWindow
{
    public byte* Api;
    public nint Handle;
}

/// <summary>clap_plugin_gui_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClapPluginGui
{
    public delegate* unmanaged<ClapPlugin*, byte*, byte, byte> IsApiSupported;
    public delegate* unmanaged<ClapPlugin*, byte**, byte*, byte> GetPreferredApi;
    public delegate* unmanaged<ClapPlugin*, byte*, byte, byte> Create;
    public delegate* unmanaged<ClapPlugin*, void> Destroy;
    public delegate* unmanaged<ClapPlugin*, double, byte> SetScale;
    public delegate* unmanaged<ClapPlugin*, uint*, uint*, byte> GetSize;
    public delegate* unmanaged<ClapPlugin*, byte> CanResize;
    public delegate* unmanaged<ClapPlugin*, void*, byte> GetResizeHints;
    public delegate* unmanaged<ClapPlugin*, uint*, uint*, byte> AdjustSize;
    public delegate* unmanaged<ClapPlugin*, uint, uint, byte> SetSize;
    public delegate* unmanaged<ClapPlugin*, ClapWindow*, byte> SetParent;
    public delegate* unmanaged<ClapPlugin*, ClapWindow*, byte> SetTransient;
    public delegate* unmanaged<ClapPlugin*, byte*, void> SuggestTitle;
    public delegate* unmanaged<ClapPlugin*, byte> Show;
    public delegate* unmanaged<ClapPlugin*, byte> Hide;
}

/// <summary>The constants the host uses.</summary>
internal static class ClapConstants
{
    public const string PluginFactoryId = "clap.plugin-factory";
    public const string ExtParams = "clap.params";
    public const string ExtLatency = "clap.latency";
    public const string ExtState = "clap.state";
    public const string ExtRender = "clap.render";
    public const string ExtAudioPorts = "clap.audio-ports";
    public const string ExtGui = "clap.gui";
    public const string WindowApiWin32 = "win32";
    public const ushort CoreEventSpace = 0;
    public const ushort EventParamValue = 5;
    public const int ProcessError = 0;
    public const int RenderRealtime = 0;
    public const int RenderOffline = 1;
    public const uint ParamIsHidden = 1 << 2;
    public const uint ParamIsReadonly = 1 << 3;
    public const uint ParamIsBypass = 1 << 4;
}
