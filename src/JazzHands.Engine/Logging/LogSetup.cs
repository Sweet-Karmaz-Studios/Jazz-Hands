using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace JazzHands.Engine.Logging;

/// <summary>How a front end wants logging configured.</summary>
public sealed record LogOptions
{
    /// <summary>The lowest level written anywhere.</summary>
    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;

    /// <summary>Write a human-readable line to stderr. The CLI and MCP stdio hosts want this; the GUI does not.</summary>
    public bool Console { get; init; }

    /// <summary>Write rolling files under <see cref="LogDirectory"/>.</summary>
    public bool File { get; init; } = true;

    /// <summary>Keep recent entries in memory for the Log panel and the control API.</summary>
    public bool RingBuffer { get; init; } = true;

    /// <summary>Where rolling files go. Defaults to %LOCALAPPDATA%\JazzHands\logs.</summary>
    public string? LogDirectory { get; init; }

    /// <summary>The file name stem inside <see cref="LogDirectory"/>, so jazz.exe and the GUI do not fight.</summary>
    public string FileStem { get; init; } = "jazz";

    /// <summary>How many daily files to keep.</summary>
    public int RetainedFileCount { get; init; } = 14;

    /// <summary>Also write to the debugger output window. Debug builds only by default.</summary>
    public bool Debugger { get; init; }
}

/// <summary>
/// Configures Serilog for every Jazz Hands front end. Logging is local only: nothing is ever
/// sent off the machine, which is why there is no network sink here and never will be.
/// </summary>
public static class LogSetup
{
    private static readonly Lock Gate = new();
    private static LogRingBufferSink? _ringBuffer;

    /// <summary>The in-memory sink, once <see cref="Configure"/> has run with it enabled.</summary>
    public static LogRingBufferSink? RingBuffer => _ringBuffer;

    /// <summary>The directory rolling log files are written to.</summary>
    public static string DefaultLogDirectory => Path.Combine(
        JazzHands.Core.JazzFolders.Local,
        "logs");

    /// <summary>
    /// Builds the global logger and assigns it to <see cref="Log.Logger"/>. Call once per process,
    /// as early as possible; calling again replaces and disposes the previous logger.
    /// </summary>
    public static ILogger Configure(LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            LoggerConfiguration configuration = new LoggerConfiguration()
                .MinimumLevel.Is(options.MinimumLevel)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Pid", Environment.ProcessId);

            if (options.Console)
            {
                // stderr, so "jazz describe --json | jq" stays clean.
                configuration.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    standardErrorFromLevel: LogEventLevel.Verbose);
            }

            if (options.File)
            {
                string directory = options.LogDirectory ?? DefaultLogDirectory;
                Directory.CreateDirectory(directory);
                configuration.WriteTo.File(
                    new CompactJsonFormatter(),
                    Path.Combine(directory, $"{options.FileStem}-.jsonl"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCount,
                    fileSizeLimitBytes: 64L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true);
            }

            LogRingBufferSink? ring = null;
            if (options.RingBuffer)
            {
                ring = new LogRingBufferSink();
                configuration.WriteTo.Sink(ring);
            }

            if (options.Debugger)
            {
                configuration.WriteTo.Debug();
            }

            Serilog.Core.Logger logger = configuration.CreateLogger();
            Log.CloseAndFlush();
            Log.Logger = logger;
            _ringBuffer = ring;
            return logger;
        }
    }

    /// <summary>Console plus files, for jazz.exe.</summary>
    public static ILogger ConfigureForCli(LogEventLevel minimumLevel = LogEventLevel.Warning) =>
        Configure(new LogOptions
        {
            MinimumLevel = minimumLevel,
            Console = true,
            FileStem = "jazz-cli",
        });

    /// <summary>Files plus the in-memory buffer the Log panel reads, for the GUI.</summary>
    public static ILogger ConfigureForApp(LogEventLevel minimumLevel = LogEventLevel.Information) =>
        Configure(new LogOptions
        {
            MinimumLevel = minimumLevel,
            Console = false,
            FileStem = "jazz-app",
            Debugger = System.Diagnostics.Debugger.IsAttached,
        });

    /// <summary>
    /// Files only, for the MCP server: stdout carries the protocol, so nothing else may touch it.
    /// </summary>
    public static ILogger ConfigureForMcp(LogEventLevel minimumLevel = LogEventLevel.Information) =>
        Configure(new LogOptions
        {
            MinimumLevel = minimumLevel,
            Console = false,
            FileStem = "jazz-mcp",
        });

    /// <summary>Flushes and closes the global logger. Call at process exit.</summary>
    public static void Shutdown()
    {
        Log.CloseAndFlush();
        _ringBuffer = null;
    }
}
