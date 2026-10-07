using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Audio.Effects;
using JazzHands.Core;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Plugins;
using JazzHands.Plugins.Clap;
using Serilog;

namespace JazzHands.Engine.Plugins;

/// <summary>A plugin found on this computer.</summary>
/// <param name="Id">Its CLAP id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Vendor">Who makes it.</param>
/// <param name="Version">Its version.</param>
/// <param name="Library">The .clap file it is in.</param>
/// <param name="Features">Its feature tags.</param>
public sealed record InstalledPlugin(string Id, string Name, string Vendor, string Version, string Library, IReadOnlyList<string> Features)
{
    /// <summary>True for an effect (something a clip or track's sound goes through), not an instrument.</summary>
    public bool IsEffect => Features.Contains("audio-effect") || !Features.Contains("instrument");
}

/// <summary>
/// The CLAP plugins on this computer (Phase 46): the standard folders and any given, scanned in
/// the background, each file read in a process of its own, what was found kept between runs, and a
/// file that crashed on scanning remembered and left alone until asked to try again.
/// </summary>
/// <remarks>
/// Kept in <c>%LOCALAPPDATA%\JazzHands\plugins.json</c> (<see cref="JazzFolders.Local"/>), keyed by
/// file, with its size and time so a file that has changed is read again. Wires the audio side's
/// <see cref="PluginEffect.Start"/> to <see cref="PluginProcess"/>.
/// </remarks>
public sealed class PluginCatalog
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PluginCatalog>();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Lock _gate = new();
    private readonly Lock _scanning = new();
    private readonly Dictionary<string, ScannedFile> _files;

    /// <summary>Creates the catalog over what was kept.</summary>
    public PluginCatalog(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(JazzFolders.Local, "plugins.json");
        _files = Load(Path);
    }

    /// <summary>Where what was found is kept.</summary>
    public string Path { get; }

    /// <summary>The folders every scan looks in: CLAP's standard ones, and any added.</summary>
    public static IReadOnlyList<string> StandardFolders =>
    [
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "CLAP"),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "CLAP"),
    ];

    /// <summary>Every plugin found, by name.</summary>
    public IReadOnlyList<InstalledPlugin> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Values.Where(file => !file.Crashed).SelectMany(file => file.Plugins).OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>The files that crashed when read, which scans leave alone until asked to try again.</summary>
    public IReadOnlyList<string> Crashed
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Values.Where(file => file.Crashed).Select(file => file.Path)];
            }
        }
    }

    /// <summary>A plugin by id, or null.</summary>
    public InstalledPlugin? Find(string id) => Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Reads every .clap file in the standard folders and <paramref name="extra"/> that is new or
    /// changed (all of them with <paramref name="again"/>, the crashed ones too), each in a host of
    /// its own. Returns how many files were read.
    /// </summary>
    public int Scan(IEnumerable<string>? extra = null, bool again = false, CancellationToken cancellationToken = default)
    {
        // One scan at a time: the one at start-up and one asked for read the same files.
        int read;
        lock (_scanning)
        {
            read = ScanFolders(extra, again, cancellationToken);
        }

        if (read > 0)
        {
            Scanned?.Invoke(this, EventArgs.Empty);
        }

        return read;
    }

    /// <summary>Raised, on the scanning thread, after a scan that read a new or changed file.</summary>
    public event EventHandler? Scanned;

    private int ScanFolders(IEnumerable<string>? extra, bool again, CancellationToken cancellationToken)
    {
        string[] files = [.. StandardFolders.Concat(extra ?? [])
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.clap", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        int read = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            lock (_gate)
            {
                if (!again && _files.TryGetValue(file, out ScannedFile? known) && known.Size == info.Length && known.Written == info.LastWriteTimeUtc)
                {
                    continue;
                }
            }

            ScannedFile scanned;
            try
            {
                IReadOnlyList<ClapDescriptor> found = PluginProcess.Scan(file);
                scanned = new ScannedFile(file, info.Length, info.LastWriteTimeUtc, false, [.. found.Select(plugin => new InstalledPlugin(plugin.Id, plugin.Name, plugin.Vendor, plugin.Version, file, plugin.Features))]);
            }
            catch (PluginCrashedException exception)
            {
                Log.Warning("{File} crashed when read, and is left alone until scanned again: {Message}", file, exception.Message);
                scanned = new ScannedFile(file, info.Length, info.LastWriteTimeUtc, true, []);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException)
            {
                Log.Warning("{File} is not a plugin this host can use: {Message}", file, exception.Message);
                scanned = new ScannedFile(file, info.Length, info.LastWriteTimeUtc, false, []);
            }

            lock (_gate)
            {
                _files[file] = scanned;
            }

            read++;
        }

        lock (_gate)
        {
            // Files that have gone are forgotten.
            foreach (string gone in _files.Keys.Where(file => !File.Exists(file)).ToList())
            {
                _files.Remove(gone);
            }

            Save();
        }

        return read;
    }

    /// <summary>
    /// The plugin effects in a project whose plugin file is not on this computer: the effect's id,
    /// the plugin's id and where its file was. They are bypassed, never lost.
    /// </summary>
    public static IReadOnlyList<(string EffectId, string PluginId, string Library)> Missing(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        static string Text(Effect effect, string name) =>
            effect.Parameter(name) is StaticValue { Value: ParamValue.Text text } ? text.Value : string.Empty;

        return [.. project.Sequences
            .SelectMany(sequence => sequence.Tracks)
            .SelectMany(track => track.Effects.Concat(track.Clips.SelectMany(clip => clip.Effects)))
            .Where(effect => effect.TypeId == PluginEffect.TypeId && !File.Exists(Text(effect, "library")))
            .Select(effect => (effect.Id, Text(effect, "plugin"), Text(effect, "library")))];
    }

    /// <summary>Starts a plugin for the mixer: a host process, the state given back, activated.</summary>
    public static IPluginRunner Start(PluginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.Library))
        {
            throw new FileNotFoundException($"The plugin's file is not at {request.Library}; it is not installed here.", request.Library);
        }

        PluginProcess process = PluginProcess.Start(request.Library, request.PluginId);
        try
        {
            if (request.State.Length > 0 && !process.LoadState(request.State))
            {
                Log.Warning("{Plugin} did not take its saved state", request.PluginId);
            }

            process.Activate(request.SampleRate, request.Channels, request.Offline);
            return new Runner(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static Dictionary<string, ScannedFile> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<ScannedFile>>(File.ReadAllText(path), Json) ?? []).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ScannedFile>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "The plugin list at {Path} could not be read; scanning again", path);
            return new Dictionary<string, ScannedFile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string part = Path + ".part";
            File.WriteAllText(part, JsonSerializer.Serialize(_files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList(), Json));
            File.Move(part, Path, overwrite: true);
        }
        catch (IOException exception)
        {
            Log.Warning(exception, "The plugin list could not be kept at {Path}", Path);
        }
    }

    /// <summary>One file as the last scan found it.</summary>
    private sealed record ScannedFile(string Path, long Size, DateTime Written, bool Crashed, List<InstalledPlugin> Plugins);

    /// <summary>A plugin process as the mixer's runner.</summary>
    private sealed class Runner(PluginProcess process) : IPluginRunner
    {
        public int Latency => process.Latency;

        public bool Failed => process.Failed;

        public Span<float> Input(int channel, int frames) => process.Input(channel, frames);

        public ReadOnlySpan<float> Output(int channel, int frames) => process.Output(channel, frames);

        public bool Run(int frames, ReadOnlySpan<(uint Id, double Value, int Offset)> changes, int timeoutMilliseconds) => process.Run(frames, changes, timeoutMilliseconds);

        public byte[] SaveState() => process.SaveState();

        public void Dispose() => process.Dispose();
    }
}
