using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace JazzHands.Control;

/// <summary>A running editor or <c>jazz serve</c>, as <c>instances.json</c> lists it.</summary>
/// <param name="Pid">Its process.</param>
/// <param name="Kind"><c>gui</c> or <c>serve</c>.</param>
/// <param name="Name">What it calls itself.</param>
/// <param name="Pipe">Its pipe name, or null.</param>
/// <param name="Tcp">Where TCP listens, <c>127.0.0.1:47800</c>, or null.</param>
/// <param name="Token">The token TCP wants, or null. The file is the user's own, as the pipe is.</param>
/// <param name="Project">The project it has open, or empty.</param>
/// <param name="Started">When it started listening.</param>
public sealed record InstanceInfo(int Pid, string Kind, string Name, string? Pipe, string? Tcp, string? Token, string Project, DateTimeOffset Started);

/// <summary>
/// <c>%LOCALAPPDATA%\JazzHands\instances.json</c>: which editors are running and how to reach
/// them, so <c>jazz --attach</c> finds one without being told a pipe name.
/// </summary>
/// <remarks>
/// Every read drops the entries whose process has gone, so a crashed editor does not linger. A
/// named mutex keeps two processes from writing it at once.
/// </remarks>
public static class InstanceRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The per-user list, or the file <c>JAZZ_INSTANCES</c> names, which keeps test instances off the real one.</summary>
    public static string DefaultPath { get; } = Environment.GetEnvironmentVariable("JAZZ_INSTANCES") is { Length: > 0 } chosen
        ? Path.GetFullPath(chosen)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JazzHands", "instances.json");

    /// <summary>Adds or replaces an instance.</summary>
    public static void Register(InstanceInfo instance, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Update(path, list => [.. list.Where(other => !Same(other, instance.Pid, instance.Pipe)), instance]);
    }

    /// <summary>Takes an instance off the list.</summary>
    public static void Unregister(int pid, string? pipe, string? path = null) =>
        Update(path, list => [.. list.Where(other => !Same(other, pid, pipe))]);

    /// <summary>The instances whose process is still running, newest first.</summary>
    public static IReadOnlyList<InstanceInfo> List(string? path = null)
    {
        IReadOnlyList<InstanceInfo> live = [];
        Update(path, list =>
        {
            live = [.. list.Where(Alive).OrderByDescending(instance => instance.Started)];
            return live.Count == list.Count ? null : live;
        });
        return live;
    }

    private static bool Same(InstanceInfo instance, int pid, string? pipe) =>
        instance.Pid == pid && string.Equals(instance.Pipe, pipe, StringComparison.OrdinalIgnoreCase);

    private static bool Alive(InstanceInfo instance)
    {
        try
        {
            using Process process = Process.GetProcessById(instance.Pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the list, changes it and writes it back, under the machine-wide mutex; a null change writes nothing.</summary>
    private static void Update(string? path, Func<IReadOnlyList<InstanceInfo>, IReadOnlyList<InstanceInfo>?> change)
    {
        string file = path ?? DefaultPath;
        using var mutex = new Mutex(false, @"Local\JazzHands.Instances");
        bool held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            IReadOnlyList<InstanceInfo> list = Read(file);
            if (change(list) is { } changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(changed, Json));
                File.Move(temporary, file, overwrite: true);
            }
        }
        catch (IOException error)
        {
            Log.ForContext(typeof(InstanceRegistry)).Warning(error, "Could not update {File}", file);
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static IReadOnlyList<InstanceInfo> Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<InstanceInfo[]>(File.ReadAllText(file), Json) ?? [];
        }
        catch (JsonException)
        {
            // A broken list is started again, not trusted.
            return [];
        }
    }
}
