using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace JazzHands.App.Shell;

/// <summary>
/// How long starting took, from the process being created to each step: the runtime and WPF up,
/// the project and the services, the window built, laid out, and its first frame on screen.
/// </summary>
/// <remarks>
/// Every launch logs these once its first frame is up, so a slow start shows in the log with
/// where the time went (Phase 32; the bar is 1.5 s to an empty project). <c>--measure-startup</c>
/// runs the same start with the window built and laid out off screen and never shown, writes the
/// times as JSON and quits: the part of starting that can be measured without putting a window in
/// front of anyone.
/// </remarks>
public sealed class StartupTimes
{
    /// <summary>The switch that measures a start without showing anything.</summary>
    public const string MeasureSwitch = "--measure-startup";

    private readonly DateTime _processStarted;
    private readonly List<(string Step, double Milliseconds)> _steps = [];

    /// <summary>Starts counting from when the process was created.</summary>
    public StartupTimes()
    {
        using var process = Process.GetCurrentProcess();
        _processStarted = process.StartTime.ToUniversalTime();
    }

    /// <summary>The steps so far, each the time since the process was created.</summary>
    public IReadOnlyList<(string Step, double Milliseconds)> Steps => _steps;

    /// <summary>Notes that a step has finished now.</summary>
    public void Mark(string step) =>
        _steps.Add((step, Math.Round((DateTime.UtcNow - _processStarted).TotalMilliseconds, 1)));

    /// <summary>The steps as one line: "runtime 180 ms, services 420 ms, ...".</summary>
    public string Describe() =>
        string.Join(", ", _steps.Select(step => string.Create(CultureInfo.InvariantCulture, $"{step.Step} {step.Milliseconds:F0} ms")));

    /// <summary>Writes the steps as a JSON object of step names to milliseconds.</summary>
    public void Write(string path)
    {
        var steps = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string step, double milliseconds) in _steps)
        {
            steps[step] = milliseconds;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Where <see cref="MeasureSwitch"/> asks the times to go, or null when it was not given.</summary>
    public static string? MeasurePath(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], MeasureSwitch, StringComparison.Ordinal))
            {
                return index + 1 < args.Count ? Path.GetFullPath(args[index + 1]) : Path.Combine(Path.GetTempPath(), "jazz-startup.json");
            }
        }

        return null;
    }
}
