using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Plugins.Clap;
using Serilog;

namespace JazzHands.Plugins;

/// <summary>
/// A CLAP plugin running in a process of its own, jazz-plugin-host.exe (Phase 46), so a plugin
/// that crashes takes nothing with it but itself.
/// </summary>
/// <remarks>
/// <para>
/// Control goes over the host's standard input and output, a JSON line each way: load, activate,
/// parameters, state, offline rendering. Audio goes through shared memory: this side writes a
/// block's input planes and parameter changes and sets the go event; the host runs the plugin
/// straight on the shared planes and sets the done event. Nothing on the audio side allocates or
/// locks; <see cref="Run"/> waits for done at most a timeout, and a host that has gone or not
/// answered in time marks the plugin <see cref="Failed"/>, after which it is bypassed.
/// </para>
/// <para>
/// Layout of the shared memory: frames, channels, event count and status (four ints), then
/// <see cref="MaxEvents"/> events of (id, offset, value), then the input planes and the output
/// planes, each <see cref="MaxFrames"/> floats.
/// </para>
/// </remarks>
public sealed unsafe class PluginProcess : IDisposable
{
    /// <summary>The largest block.</summary>
    public const int MaxFrames = 4096;

    /// <summary>The most parameter changes in a block.</summary>
    public const int MaxEvents = 256;

    /// <summary>The most channels.</summary>
    public const int MaxChannels = 8;

    internal const int HeaderBytes = 16;
    internal const int EventBytes = 16;

    private static readonly ILogger Log = Serilog.Log.ForContext<PluginProcess>();
    private readonly Process _host;
    private readonly Lock _control = new();
    private MemoryMappedFile? _memory;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;
    private EventWaitHandle? _go;
    private EventWaitHandle? _done;
    private int _channels;
    private bool _disposed;
    private volatile bool _failed;

    private PluginProcess(Process host, string library, string pluginId, JsonNode loaded)
    {
        _host = host;
        Library = library;
        PluginId = pluginId;
        Name = loaded["name"]?.GetValue<string>() ?? pluginId;
        Parameters = [.. (loaded["params"]?.AsArray() ?? []).Select(Parameter)];
        Latency = loaded["latency"]?.GetValue<int>() ?? 0;
    }

    /// <summary>The .clap file.</summary>
    public string Library { get; }

    /// <summary>The plugin's id.</summary>
    public string PluginId { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>Its parameters.</summary>
    public IReadOnlyList<ClapParameter> Parameters { get; }

    /// <summary>Samples its output lags its input by, as it said when activated.</summary>
    public int Latency { get; private set; }

    /// <summary>True once the host has gone or stopped answering: the plugin is bypassed from then on.</summary>
    public bool Failed => _failed;

    /// <summary>Why it failed, when it has.</summary>
    public string? Failure { get; private set; }

    /// <summary>Raised once, on whatever thread noticed, when the plugin fails.</summary>
    public event Action<PluginProcess>? Crashed;

    /// <summary>
    /// How long a request to the host (load, scan, activate, values, state) may take before the
    /// host is taken to have stopped answering and is stopped. Long enough for a plugin that loads
    /// a large library; sound itself is held to its own, much shorter, limit in <see cref="Run"/>.
    /// </summary>
    internal static TimeSpan ControlTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The host program beside this assembly.</summary>
    public static string HostPath => Path.Combine(AppContext.BaseDirectory, "jazz-plugin-host.exe");

    /// <summary>Starts a host and loads a plugin in it.</summary>
    public static PluginProcess Start(string library, string pluginId, string? hostPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        Process host = Launch(hostPath ?? HostPath);
        try
        {
            JsonNode reply = Ask(host, new JsonObject { ["op"] = "load", ["path"] = library, ["id"] = pluginId });
            return new PluginProcess(host, library, pluginId, reply);
        }
        catch
        {
            Kill(host);
            throw;
        }
    }

    /// <summary>
    /// Lists the plugins in a .clap file, loading it in a host of its own: a file that crashes on
    /// loading throws <see cref="PluginCrashedException"/> and leaves this process standing.
    /// </summary>
    public static IReadOnlyList<ClapDescriptor> Scan(string library, string? hostPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(library);
        Process host = Launch(hostPath ?? HostPath);
        try
        {
            JsonNode reply = Ask(host, new JsonObject { ["op"] = "scan", ["path"] = library });
            return [.. (reply["plugins"]?.AsArray() ?? []).Select(node => new ClapDescriptor(
                node!["id"]!.GetValue<string>(),
                node["name"]?.GetValue<string>() ?? string.Empty,
                node["vendor"]?.GetValue<string>() ?? string.Empty,
                node["version"]?.GetValue<string>() ?? string.Empty,
                node["description"]?.GetValue<string>() ?? string.Empty,
                [.. (node["features"]?.AsArray() ?? []).Select(feature => feature!.GetValue<string>())]))];
        }
        finally
        {
            Quit(host);
        }
    }

    /// <summary>Activates the plugin for a rate and a channel count, rendering offline (an export) or in real time.</summary>
    public void Activate(int sampleRate, int channels, bool offline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _channels = Math.Clamp(channels, 1, MaxChannels);
        string name = $"JazzHands.Plugin.{Environment.ProcessId}.{Guid.NewGuid():N}";
        long bytes = HeaderBytes + (MaxEvents * EventBytes) + (2L * MaxChannels * MaxFrames * sizeof(float));
        _memory = MemoryMappedFile.CreateNew(name, bytes);
        _view = _memory.CreateViewAccessor();
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        _go = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.go");
        _done = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.done");
        JsonNode reply = Control(new JsonObject
        {
            ["op"] = "activate",
            ["rate"] = sampleRate,
            ["channels"] = _channels,
            ["frames"] = MaxFrames,
            ["offline"] = offline,
            ["shared"] = name,
        });
        Latency = reply["latency"]?.GetValue<int>() ?? Latency;
    }

    /// <summary>A block's input plane for a channel, in the shared memory, to fill before <see cref="Run"/>.</summary>
    public Span<float> Input(int channel, int frames) =>
        new(_base + HeaderBytes + (MaxEvents * EventBytes) + ((long)channel * MaxFrames * sizeof(float)), frames);

    /// <summary>A block's output plane for a channel, after <see cref="Run"/>.</summary>
    public ReadOnlySpan<float> Output(int channel, int frames) =>
        new(_base + HeaderBytes + (MaxEvents * EventBytes) + ((long)(MaxChannels + channel) * MaxFrames * sizeof(float)), frames);

    /// <summary>
    /// Runs one block of <paramref name="frames"/> through the plugin with parameter changes at
    /// sample offsets. Audio thread; allocates nothing. False when it failed or did not answer in
    /// <paramref name="timeoutMilliseconds"/>, and then from then on.
    /// </summary>
    public bool Run(int frames, ReadOnlySpan<(uint Id, double Value, int Offset)> changes, int timeoutMilliseconds = 250)
    {
        if (_failed || _base is null || _go is null || _done is null || frames <= 0 || frames > MaxFrames)
        {
            return false;
        }

        int* header = (int*)_base;
        header[0] = frames;
        header[1] = _channels;
        int count = Math.Min(changes.Length, MaxEvents);
        header[2] = count;
        header[3] = 0;
        byte* events = _base + HeaderBytes;
        for (int index = 0; index < count; index++)
        {
            *(uint*)(events + (index * EventBytes)) = changes[index].Id;
            *(int*)(events + (index * EventBytes) + 4) = changes[index].Offset;
            *(double*)(events + (index * EventBytes) + 8) = changes[index].Value;
        }

        _go.Set();
        if (!_done.WaitOne(timeoutMilliseconds))
        {
            Fail(_host.HasExited ? $"{Name} stopped working (its process ended with code {_host.ExitCode}) and is bypassed." : $"{Name} stopped answering and is bypassed.");
            return false;
        }

        if (header[3] != 0)
        {
            Fail($"{Name} reported an error while processing and is bypassed.");
            return false;
        }

        return true;
    }

    /// <summary>The parameters' values now, by id.</summary>
    public IReadOnlyDictionary<uint, double> Values()
    {
        JsonNode reply = Control(new JsonObject { ["op"] = "values" });
        return (reply["values"]?.AsObject() ?? []).ToDictionary(pair => uint.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture), pair => pair.Value!.GetValue<double>());
    }

    /// <summary>Sets parameters while the plugin is not processing a block.</summary>
    public void Set(IEnumerable<(uint Id, double Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = new JsonArray([.. values.Select(value => (JsonNode?)new JsonObject { ["id"] = value.Id, ["value"] = value.Value })]);
        Control(new JsonObject { ["op"] = "set", ["values"] = list });
    }

    /// <summary>The plugin's state, as it saves it.</summary>
    public byte[] SaveState() => Convert.FromBase64String(Control(new JsonObject { ["op"] = "save" })["state"]?.GetValue<string>() ?? string.Empty);

    /// <summary>Gives the plugin a state; false when it refused it.</summary>
    public bool LoadState(byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Control(new JsonObject { ["op"] = "load-state", ["state"] = Convert.ToBase64String(state) })["loaded"]?.GetValue<bool>() == true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_failed)
        {
            Quit(_host);
        }
        else
        {
            Kill(_host);
        }

        if (_base is not null)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view?.Dispose();
        _memory?.Dispose();
        _go?.Dispose();
        _done?.Dispose();
        _host.Dispose();
    }

    private static ClapParameter Parameter(JsonNode? node) => new(
        node!["id"]!.GetValue<uint>(),
        node["name"]?.GetValue<string>() ?? string.Empty,
        node["module"]?.GetValue<string>() ?? string.Empty,
        node["min"]?.GetValue<double>() ?? 0,
        node["max"]?.GetValue<double>() ?? 1,
        node["default"]?.GetValue<double>() ?? 0,
        node["flags"]?.GetValue<uint>() ?? 0);

    private static Process Launch(string hostPath)
    {
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException($"The plugin host is not at {hostPath}.", hostPath);
        }

        var start = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        Process host = Process.Start(start) ?? throw new InvalidOperationException("The plugin host did not start.");
        host.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                Log.Warning("Plugin host {Pid}: {Line}", host.Id, line);
            }
        };
        host.BeginErrorReadLine();
        return host;
    }

    private static JsonNode Ask(Process host, JsonObject request)
    {
        host.StandardInput.WriteLine(request.ToJsonString());
        host.StandardInput.Flush();

        // A plugin that hangs while loading or answering must not hang whoever asked: found
        // 2026-10-02, a request read with no limit could wait for ever, its host left running.
        string? line;
        try
        {
            line = ReadLine(host.StandardOutput, ControlTimeout);
        }
        catch (TimeoutException)
        {
            Kill(host);
            throw new PluginCrashedException($"The plugin host stopped answering while asked to {request["op"]} (nothing in {ControlTimeout.TotalSeconds:0.#} s), so it was stopped.");
        }

        if (line is null)
        {
            host.WaitForExit(2000);
            throw new PluginCrashedException(host.HasExited
                ? $"The plugin host ended (code {host.ExitCode}) while asked to {request["op"]}."
                : $"The plugin host stopped answering while asked to {request["op"]}.");
        }

        JsonNode reply = JsonNode.Parse(line) ?? throw new InvalidDataException("The plugin host answered nothing.");
        return reply["ok"]?.GetValue<bool>() == true
            ? reply
            : throw new InvalidOperationException(reply["error"]?.GetValue<string>() ?? "The plugin host refused.");
    }

    /// <summary>A line from a reader, or <see cref="TimeoutException"/> when none comes in time; null at its end.</summary>
    internal static string? ReadLine(TextReader reader, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Task<string?> read = reader.ReadLineAsync();
        return read.Wait(timeout) ? read.Result : throw new TimeoutException("No line came in time.");
    }

    private static void Quit(Process host)
    {
        try
        {
            if (!host.HasExited)
            {
                host.StandardInput.WriteLine(new JsonObject { ["op"] = "quit" }.ToJsonString());
                host.StandardInput.Flush();
                if (!host.WaitForExit(2000))
                {
                    Kill(host);
                }
            }
        }
        catch (IOException)
        {
            Kill(host);
        }
    }

    private static void Kill(Process host)
    {
        try
        {
            if (!host.HasExited)
            {
                host.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private JsonNode Control(JsonObject request)
    {
        if (_failed)
        {
            throw new PluginCrashedException(Failure ?? $"{Name} has stopped working.");
        }

        lock (_control)
        {
            try
            {
                return Ask(_host, request);
            }
            catch (PluginCrashedException exception)
            {
                Fail(exception.Message);
                throw;
            }
        }
    }

    private void Fail(string why)
    {
        if (_failed)
        {
            return;
        }

        Failure = why;
        _failed = true;
        Crashed?.Invoke(this);
    }
}

/// <summary>A plugin's host process ended or stopped answering.</summary>
public sealed class PluginCrashedException : Exception
{
    /// <summary>Creates one.</summary>
    public PluginCrashedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates one.</summary>
    public PluginCrashedException()
    {
    }

    /// <summary>Creates one.</summary>
    public PluginCrashedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
