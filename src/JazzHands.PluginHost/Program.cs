using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Nodes;
using JazzHands.Plugins;
using JazzHands.Plugins.Clap;

namespace JazzHands.PluginHost;

/// <summary>
/// jazz-plugin-host.exe (Phase 46): one CLAP plugin in a process of its own, driven by the editor
/// (<see cref="PluginProcess"/>) over standard input and output, a JSON line each way, with audio
/// through shared memory. A plugin that crashes ends this process and nothing else.
/// </summary>
internal static unsafe class Program
{
    private static ClapLibrary? _library;
    private static ClapInstance? _plugin;
    private static Thread? _audio;
    private static volatile bool _quitting;

    private static int Main()
    {
        TextReader input = Console.In;
        TextWriter output = Console.Out;
        while (input.ReadLine() is { } line)
        {
            JsonObject reply;
            try
            {
                JsonNode request = JsonNode.Parse(line) ?? throw new InvalidDataException("An empty request.");
                string op = request["op"]?.GetValue<string>() ?? string.Empty;
                if (op == "quit")
                {
                    break;
                }

                reply = Handle(op, request);
                reply["ok"] = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reply = new JsonObject { ["ok"] = false, ["error"] = exception.Message };
            }

            output.WriteLine(reply.ToJsonString());
            output.Flush();
        }

        _quitting = true;
        _audio?.Join(2000);
        _plugin?.Dispose();
        _library?.Dispose();
        return 0;
    }

    private static JsonObject Handle(string op, JsonNode request)
    {
        switch (op)
        {
            case "scan":
            {
                using ClapLibrary library = ClapLibrary.Open(request["path"]!.GetValue<string>());
                var plugins = new JsonArray([.. library.Plugins.Select(plugin => (JsonNode?)new JsonObject
                {
                    ["id"] = plugin.Id,
                    ["name"] = plugin.Name,
                    ["vendor"] = plugin.Vendor,
                    ["version"] = plugin.Version,
                    ["description"] = plugin.Description,
                    ["features"] = new JsonArray([.. plugin.Features.Select(feature => (JsonNode?)feature)]),
                })]);
                return new JsonObject { ["plugins"] = plugins };
            }

            case "load":
            {
                _library = ClapLibrary.Open(request["path"]!.GetValue<string>());
                _plugin = _library.Create(request["id"]!.GetValue<string>());
                return new JsonObject
                {
                    ["name"] = _plugin.Name,
                    ["latency"] = _plugin.Latency,
                    ["params"] = new JsonArray([.. _plugin.Parameters.Select(parameter => (JsonNode?)new JsonObject
                    {
                        ["id"] = parameter.Id,
                        ["name"] = parameter.Name,
                        ["module"] = parameter.Module,
                        ["min"] = parameter.Min,
                        ["max"] = parameter.Max,
                        ["default"] = parameter.Default,
                        ["flags"] = parameter.Flags,
                    })]),
                };
            }

            case "activate":
            {
                ClapInstance plugin = Loaded();
                plugin.SetOffline(request["offline"]?.GetValue<bool>() == true);
                plugin.Activate(request["rate"]!.GetValue<int>(), request["frames"]!.GetValue<int>());
                string shared = request["shared"]!.GetValue<string>();
                int channels = request["channels"]!.GetValue<int>();
                _audio = new Thread(() => AudioLoop(plugin, shared, channels)) { IsBackground = true, Name = "Plugin audio", Priority = ThreadPriority.Highest };
                _audio.Start();
                return new JsonObject { ["latency"] = plugin.Latency };
            }

            case "values":
            {
                ClapInstance plugin = Loaded();
                var values = new JsonObject();
                foreach (ClapParameter parameter in plugin.Parameters)
                {
                    values[parameter.Id.ToString(CultureInfo.InvariantCulture)] = plugin.Value(parameter.Id);
                }

                return new JsonObject { ["values"] = values };
            }

            case "set":
            {
                (uint, double)[] changes = [.. (request["values"]?.AsArray() ?? []).Select(node => (node!["id"]!.GetValue<uint>(), node["value"]!.GetValue<double>()))];
                Loaded().Set(changes);
                return [];
            }

            case "save":
                return new JsonObject { ["state"] = Convert.ToBase64String(Loaded().SaveState()) };

            case "load-state":
                return new JsonObject { ["loaded"] = Loaded().LoadState(Convert.FromBase64String(request["state"]?.GetValue<string>() ?? string.Empty)) };

            default:
                throw new InvalidDataException($"No such request: '{op}'.");
        }
    }

    private static ClapInstance Loaded() => _plugin ?? throw new InvalidOperationException("No plugin is loaded.");

    /// <summary>Waits for a block, runs the plugin on the shared planes, says it is done; until quitting.</summary>
    private static void AudioLoop(ClapInstance plugin, string shared, int channels)
    {
        using var memory = MemoryMappedFile.OpenExisting(shared);
        using MemoryMappedViewAccessor view = memory.CreateViewAccessor();
        byte* bytes = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref bytes);
        using var go = EventWaitHandle.OpenExisting($@"Local\{shared}.go");
        using var done = EventWaitHandle.OpenExisting($@"Local\{shared}.done");
        float** inputs = stackalloc float*[PluginProcess.MaxChannels];
        float** outputs = stackalloc float*[PluginProcess.MaxChannels];
        byte* planes = bytes + PluginProcess.HeaderBytes + (PluginProcess.MaxEvents * PluginProcess.EventBytes);
        for (int channel = 0; channel < PluginProcess.MaxChannels; channel++)
        {
            inputs[channel] = (float*)(planes + ((long)channel * PluginProcess.MaxFrames * sizeof(float)));
            outputs[channel] = (float*)(planes + ((long)(PluginProcess.MaxChannels + channel) * PluginProcess.MaxFrames * sizeof(float)));
        }

        var changes = new (uint Id, double Value, int Offset)[PluginProcess.MaxEvents];
        try
        {
            while (!_quitting)
            {
                if (!go.WaitOne(200))
                {
                    continue;
                }

                int* header = (int*)bytes;
                int frames = header[0];
                int count = Math.Min(header[2], PluginProcess.MaxEvents);
                byte* events = bytes + PluginProcess.HeaderBytes;
                for (int index = 0; index < count; index++)
                {
                    changes[index] = (*(uint*)(events + (index * PluginProcess.EventBytes)), *(double*)(events + (index * PluginProcess.EventBytes) + 8), *(int*)(events + (index * PluginProcess.EventBytes) + 4));
                }

                header[3] = plugin.Process(inputs, outputs, Math.Min(channels, header[1]), frames, changes.AsSpan(0, count)) ? 0 : 1;
                done.Set();
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }
}
