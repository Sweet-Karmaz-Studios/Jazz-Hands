using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Engine.Caching;
using Serilog;

namespace JazzHands.Control;

/// <summary>How the editor's control server listens, as the person using it set it.</summary>
/// <param name="Tcp">Listen on TCP as well as the pipe. Off by default (decision 272).</param>
/// <param name="Port">The TCP port.</param>
/// <param name="Address">The address TCP binds to; loopback unless <paramref name="AllowRemote"/>.</param>
/// <param name="AllowRemote">Let TCP bind to an address other machines can reach.</param>
public sealed record ControlSettings(
    bool Tcp = false,
    int Port = ControlServerOptions.DefaultTcpPort,
    string Address = "127.0.0.1",
    bool AllowRemote = false)
{
    /// <summary>The server options these settings make, for an instance of a kind.</summary>
    public ControlServerOptions ToOptions(string name) => new()
    {
        Tcp = Tcp,
        TcpPort = Port,
        TcpAddress = Address,
        AllowRemote = AllowRemote,
        Name = name,
    };
}

/// <summary>
/// Reads and writes the <c>control</c> section of the editor's settings file.
/// </summary>
/// <remarks>
/// The same file as <see cref="CacheSettingsStore"/>, <c>%APPDATA%\JazzHands\settings.json</c>, and
/// the same manners: writing this section leaves the others alone, and a file that cannot be read
/// is logged and treated as defaults. <c>JAZZ_CONTROL_TCP=1</c> turns TCP on for one run without
/// touching the file.
/// </remarks>
public sealed class ControlSettingsStore
{
    private const string Section = "control";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger _log = Log.ForContext<ControlSettingsStore>();
    private readonly Lock _gate = new();

    /// <summary>A store over a settings file.</summary>
    /// <param name="path">The file, or null for the per-user one.</param>
    public ControlSettingsStore(string? path = null)
    {
        Path = path ?? CacheSettingsStore.DefaultPath;
        Current = Read();
    }

    /// <summary>The file.</summary>
    public string Path { get; }

    /// <summary>The settings as last read or written, with the environment's override applied.</summary>
    public ControlSettings Current { get; private set; }

    /// <summary>Writes new settings, keeping the rest of the file. They apply from the next start.</summary>
    public void Save(ControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            JsonObject root = Load() ?? [];
            root[Section] = JsonSerializer.SerializeToNode(settings, Json);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);

            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Json));
            File.Move(temporary, Path, overwrite: true);

            Current = settings;
        }
    }

    private ControlSettings Read()
    {
        ControlSettings settings = new();
        try
        {
            if (Load()?[Section] is JsonNode node && node.Deserialize<ControlSettings>(Json) is { } read)
            {
                settings = read with { Port = read.Port is > 0 and < 65536 ? read.Port : ControlServerOptions.DefaultTcpPort };
            }
        }
        catch (JsonException error)
        {
            _log.Warning(error, "The control settings in {Path} could not be read; using the defaults", Path);
        }

        return Environment.GetEnvironmentVariable("JAZZ_CONTROL_TCP") is "1" or "true" ? settings with { Tcp = true } : settings;
    }

    private JsonObject? Load()
    {
        try
        {
            return File.Exists(Path) ? JsonNode.Parse(File.ReadAllText(Path)) as JsonObject : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "The settings file {Path} could not be read", Path);
            return null;
        }
    }
}

/// <summary>Registers the control server's settings with the settings commands.</summary>
public static class ControlSettingsServices
{
    /// <summary>Adds the <c>control</c> section to <c>settings.get</c> and <c>settings.set</c>.</summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddJazzHandsControlSettings(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(
            services,
            new Engine.Settings.SettingsSectionType("control", typeof(ControlSettings), "How jazz --attach and MCP reach the editor (from the next start)"));
        return services;
    }
}
