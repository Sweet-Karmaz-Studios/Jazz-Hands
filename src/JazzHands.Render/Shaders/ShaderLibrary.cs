using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace JazzHands.Render.Shaders;

/// <summary>
/// Every shader the renderer uses: its source, its includes, and its compiled bytecode.
/// </summary>
/// <remarks>
/// Shaders are embedded as source and compiled on first use rather than at build time. The
/// alternative the phase prompt sketched, a source generator running the D3D compiler, would load
/// a native DLL inside the C# compiler process, which is fragile on build machines and slow to
/// iterate on. What it would have bought is kept: compilation is strict (warnings are errors),
/// every shader is compiled by a test so a broken one fails the build, and compiled bytecode is
/// cached on disk keyed by the hash of its preprocessed source, so only the first start after a
/// change pays for compiling.
///
/// <c>#include "Name.hlsli"</c> is resolved here, textually, from the same embedded resources,
/// so a pass and its includes always come from one build.
///
/// Setting <c>JAZZ_SHADER_DIR</c> to the Shaders folder reads the source from disk instead and
/// watches it: saving a shader bumps <see cref="Generation"/>, and the compositor rebuilds its
/// passes before the next frame. That is the hot reload the hlsl-effects skill asks for.
/// </remarks>
public static partial class ShaderLibrary
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ShaderLibrary));
    private static readonly ConcurrentDictionary<string, byte[]> Compiled = new(StringComparer.Ordinal);
    private static readonly string? SourceDirectory = FindSourceDirectory();
    private static readonly FileSystemWatcher? Watcher = Watch(SourceDirectory);
    private static int _generation;

    /// <summary>Bumped whenever a watched shader file changes. Passes built at an older one are stale.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>Where compiled bytecode is kept between runs.</summary>
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JazzHands",
        "cache",
        "shaders");

    /// <summary>Every shader file this build embeds, for the test that compiles them all.</summary>
    public static IReadOnlyList<string> Files { get; } =
    [
        .. typeof(ShaderLibrary).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".hlsl", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..]),
    ];

    private const string ResourcePrefix = "JazzHands.Render.Shaders.";

    /// <summary>A file's source with its includes spliced in.</summary>
    public static string Source(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return Preprocess(file, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Compiled bytecode for one entry point of a file.</summary>
    /// <param name="file">The shader file, for example Composite.hlsl.</param>
    /// <param name="entryPoint">The function.</param>
    /// <param name="profile">vs_5_0, ps_5_0 or cs_5_0.</param>
    public static byte[] Bytecode(string file, string entryPoint, string profile)
    {
        string source = Source(file);
        string key = Hash($"{profile}\n{entryPoint}\n{source}");

        return Compiled.GetOrAdd(key, _ => LoadOrCompile(key, source, file, entryPoint, profile));
    }

    /// <summary>
    /// Compiled bytecode for shader source that is not one of the embedded files: an effect from
    /// elsewhere, or a test. Its includes resolve against the embedded ones, so it can use
    /// Common.hlsli and Color.hlsli.
    /// </summary>
    public static byte[] BytecodeFromSource(string source, string name, string entryPoint, string profile)
    {
        ArgumentNullException.ThrowIfNull(source);

        string expanded = IncludeLine().Replace(
            source,
            match => Preprocess(match.Groups["name"].Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) + "\n");
        string key = Hash($"{profile}\n{entryPoint}\n{expanded}");

        return Compiled.GetOrAdd(key, _ => LoadOrCompile(key, expanded, name, entryPoint, profile));
    }

    /// <summary>A vertex shader.</summary>
    public static ID3D11VertexShader VertexShader(RenderDevice device, string file, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Device.CreateVertexShader(Bytecode(file, entryPoint, "vs_5_0"));
    }

    /// <summary>A pixel shader.</summary>
    public static ID3D11PixelShader PixelShader(RenderDevice device, string file, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Device.CreatePixelShader(Bytecode(file, entryPoint, "ps_5_0"));
    }

    /// <summary>A compute shader.</summary>
    public static ID3D11ComputeShader ComputeShader(RenderDevice device, string file, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Device.CreateComputeShader(Bytecode(file, entryPoint, "cs_5_0"));
    }

    private static string Preprocess(string file, HashSet<string> seen)
    {
        if (!seen.Add(file))
        {
            // Include guards in the files make a second inclusion harmless; this just stops a
            // cycle from recursing forever.
            return string.Empty;
        }

        string text = ReadRaw(file);

        return IncludeLine().Replace(text, match => Preprocess(match.Groups["name"].Value, seen) + "\n");
    }

    private static string ReadRaw(string file)
    {
        if (SourceDirectory is not null)
        {
            string onDisk = Path.Combine(SourceDirectory, file);
            if (File.Exists(onDisk))
            {
                return File.ReadAllText(onDisk);
            }
        }

        Assembly assembly = typeof(ShaderLibrary).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
            ?? throw new RenderDeviceException(
                $"Shader '{file}' is not embedded. Is it in Shaders/ and marked as an EmbeddedResource?");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] LoadOrCompile(string key, string source, string file, string entryPoint, string profile)
    {
        string cached = Path.Combine(CacheDirectory, key + ".cso");

        try
        {
            if (File.Exists(cached))
            {
                return File.ReadAllBytes(cached);
            }
        }
        catch (IOException)
        {
            // A cache we cannot read is a cache miss.
        }

        byte[] bytecode = Compile(source, file, entryPoint, profile);

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string temporary = cached + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temporary, bytecode);
            File.Move(temporary, cached, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Two processes starting at once may race to write the same file; either copy is right.
            Log.Debug(exception, "Could not cache the bytecode for {File}:{Entry}", file, entryPoint);
        }

        return bytecode;
    }

    private static byte[] Compile(string source, string file, string entryPoint, string profile)
    {
        Blob? errors = null;
        try
        {
            Compiler.Compile(
                source,
                defines: [],
                include: null!,
                entryPoint,
                file,
                profile,
                ShaderFlags.OptimizationLevel3 | ShaderFlags.WarningsAreErrors,
                EffectFlags.None,
                out Blob? bytecode,
                out errors);

            if (bytecode is null)
            {
                throw new RenderDeviceException(
                    $"Compiling {file}:{entryPoint} ({profile}) failed: {errors?.AsString() ?? "no compiler output"}");
            }

            using (bytecode)
            {
                Log.Debug("Compiled {File}:{Entry} ({Profile})", file, entryPoint, profile);
                return bytecode.AsSpan().ToArray();
            }
        }
        finally
        {
            errors?.Dispose();
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private static string? FindSourceDirectory()
    {
        string? directory = Environment.GetEnvironmentVariable("JAZZ_SHADER_DIR");
        return directory is { Length: > 0 } && Directory.Exists(directory) ? directory : null;
    }

    private static FileSystemWatcher? Watch(string? directory)
    {
        if (directory is null)
        {
            return null;
        }

        var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        watcher.Filters.Add("*.hlsl");
        watcher.Filters.Add("*.hlsli");
        watcher.Changed += OnSourceChanged;
        watcher.Created += OnSourceChanged;
        watcher.Renamed += OnSourceChanged;

        Log.Information("Reading shaders from {Directory} and reloading them when they change", directory);
        return watcher;
    }

    /// <summary>
    /// Makes every renderer rebuild its shaders before its next frame: what a shader from outside
    /// the build does when its file changes. The compiled cache is keyed on the source, so an
    /// unchanged shader is not compiled again.
    /// </summary>
    public static void Invalidate() => Interlocked.Increment(ref _generation);

    private static void OnSourceChanged(object sender, FileSystemEventArgs e)
    {
        // The compiled cache is keyed on the source, so an edited file simply misses it.
        Interlocked.Increment(ref _generation);
        Log.Information("Shader {File} changed; passes rebuild before the next frame", e.Name);
    }

    [GeneratedRegex("^\\s*#include\\s+\"(?<name>[^\"]+)\"\\s*$", RegexOptions.Multiline)]
    private static partial Regex IncludeLine();

    /// <summary>Keeps the watcher reachable for the life of the process.</summary>
    internal static bool IsWatching => Watcher is not null;
}
