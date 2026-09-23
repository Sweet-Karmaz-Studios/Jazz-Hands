using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using Serilog;
using Serilog.Events;

namespace JazzHands.Media.Interop;

/// <summary>
/// Sends FFmpeg's own log messages to Serilog instead of the process's stderr.
/// </summary>
/// <remarks>
/// Without this, every warning a demuxer or a decoder has is written straight to stderr by
/// FFmpeg's default callback: into the terminal of whoever ran <c>jazz</c>, past the log files, and
/// never with a timestamp. Matroska alone says "keyframes not correctly marked" on every audio
/// seek.
///
/// The callback runs on whichever thread FFmpeg logs from, decode threads included, and several at
/// once. So it filters by level before doing anything else (a dropped message allocates nothing),
/// formats into a stack buffer, never throws back into native code, and holds a lock only long
/// enough to count a repeat.
///
/// A message is keyed by the component that said it (<c>matroska,webm</c>, <c>aac</c>) and its
/// text, without the per-instance address FFmpeg's own prefix carries, so the same warning from
/// two demuxers is the same warning. The first of a kind is logged at once; repeats inside
/// <see cref="RepeatWindow"/> are counted rather than logged, and the count rides along the next
/// time the message is logged. A seek loop that trips a warning every second is one line every
/// ten seconds, not a flood.
///
/// Levels: panic, fatal and error are Error; warning is Warning; info is Debug; verbose and below
/// are dropped before they are formatted.
/// </remarks>
public static unsafe class FfmpegLog
{
    /// <summary>How long an identical message stays quiet after it was last logged.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many distinct messages are remembered for counting repeats. Past this the memory is
    /// cleared rather than grown, so a stream of unique messages cannot use unbounded memory.
    /// </summary>
    internal const int MaxTracked = 512;

    private const int LineBytes = 1024;

    /// <summary>
    /// av_log_format_line2, called through its export rather than FFmpeg.AutoGen's wrapper, which
    /// takes the format as a managed string and so would allocate for every message.
    /// </summary>
    private static delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, byte*, int, int*, int> _formatLine;

    private static readonly ConcurrentDictionary<string, Repeat> Seen = new(StringComparer.Ordinal);
    private static readonly Lock InstallGate = new();
    private static bool _installed;

    /// <summary>Where messages go. Null for the global Serilog logger, read afresh per message.</summary>
    internal static ILogger? Target { get; set; }

    /// <summary>The clock repeats are timed against. Replaced by tests.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>True once the callback is in place.</summary>
    public static bool IsInstalled => Volatile.Read(ref _installed);

    /// <summary>
    /// Replaces FFmpeg's default log callback with this one. Called by
    /// <see cref="FfmpegLoader.Initialize"/>; safe to call again.
    /// </summary>
    /// <remarks>
    /// The callback is a static method marked <see cref="UnmanagedCallersOnly"/> and handed over as
    /// a function pointer. That matters twice. FFmpeg.AutoGen's delegate type marshals the format
    /// into a managed string before any code of ours runs, which allocated on every debug message a
    /// decoder writes, hundreds of bytes a frame, and broke the zero-allocation decode tests. And a
    /// static method's address is fixed for the life of the process, so unlike the decoder's
    /// get_format delegate there is nothing for the collector to take away from under FFmpeg.
    /// </remarks>
    /// <param name="binaryDirectory">Where avutil was loaded from.</param>
    public static void Install(string binaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryDirectory);

        lock (InstallGate)
        {
            if (_installed)
            {
                return;
            }

            string avutil = Directory.EnumerateFiles(binaryDirectory, "avutil-*.dll").First();
            IntPtr module = NativeLibrary.Load(avutil);
            _formatLine = (delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, byte*, int, int*, int>)
                NativeLibrary.GetExport(module, "av_log_format_line2");

            // Info and more severe reach the callback. av_vlog hands every message to the callback
            // whatever this says, so the callback filters too, but code inside FFmpeg that checks
            // the level before doing expensive diagnostic work sees it.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
            ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func
            {
                Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)&OnLog,
            });
            Volatile.Write(ref _installed, true);
        }
    }

    /// <summary>How many distinct messages are being counted. For tests.</summary>
    internal static int Tracked => Seen.Count;

    /// <summary>Forgets every repeat count. For tests.</summary>
    internal static void Reset() => Seen.Clear();

    /// <summary>The Serilog level for an FFmpeg level, or null when the message is dropped.</summary>
    internal static LogEventLevel? LevelFor(int level)
    {
        // The top bits can carry colour hints (AV_LOG_C); the level is the low byte.
        int bare = level & 0xFF;

        return bare switch
        {
            <= ffmpeg.AV_LOG_ERROR => LogEventLevel.Error,
            <= ffmpeg.AV_LOG_WARNING => LogEventLevel.Warning,
            <= ffmpeg.AV_LOG_INFO => LogEventLevel.Debug,
            _ => null,
        };
    }

    /// <summary>
    /// Logs one message, or counts it when the same one was logged within the window.
    /// </summary>
    /// <returns>True when it was logged rather than counted.</returns>
    internal static bool Write(LogEventLevel level, string component, string message)
    {
        if (Seen.Count >= MaxTracked)
        {
            Seen.Clear();
        }

        Repeat repeat = Seen.GetOrAdd(component + "\n" + message, static _ => new Repeat());
        long now = Clock.GetTimestamp();
        int suppressed;

        lock (repeat)
        {
            if (repeat.Logged && Clock.GetElapsedTime(repeat.LastLogged, now) < RepeatWindow)
            {
                repeat.Suppressed++;
                return false;
            }

            suppressed = repeat.Suppressed;
            repeat.Suppressed = 0;
            repeat.LastLogged = now;
            repeat.Logged = true;
        }

        ILogger log = (Target ?? Log.Logger).ForContext("SourceContext", "ffmpeg");

        if (suppressed == 0)
        {
            log.Write(level, "{Component}: {Message}", component, message);
        }
        else
        {
            log.Write(
                level,
                "{Component}: {Message} (and {Repeats} more like it in the last {Window} s)",
                component,
                message,
                suppressed,
                (int)RepeatWindow.TotalSeconds);
        }

        return true;
    }

    /// <summary>What FFmpeg calls instead of writing to stderr.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* context, int level, byte* format, byte* arguments)
    {
        // First, and before anything that could allocate: most of what FFmpeg says is debug
        // chatter from inside a decode, and it has to cost nothing to ignore.
        if (LevelFor(level) is not { } serilogLevel || format is null)
        {
            return;
        }

        try
        {

            // No prefix: the component is named separately, and FFmpeg's prefix carries the
            // context's address, which would make every instance's warning a different message.
            byte* line = stackalloc byte[LineBytes];
            int printPrefix = 0;
            int length = _formatLine(context, level, format, arguments, line, LineBytes, &printPrefix);

            if (length <= 0)
            {
                return;
            }

            string message = Encoding.UTF8.GetString(line, Math.Min(length, LineBytes - 1)).TrimEnd('\r', '\n', ' ');
            if (message.Length == 0)
            {
                return;
            }

            Write(serilogLevel, ComponentOf(context), message);
        }
        catch (Exception exception)
        {
            // Nothing may unwind into FFmpeg's stack. A logging failure is not worth a crash.
            try
            {
                (Target ?? Log.Logger).ForContext("SourceContext", "ffmpeg").Debug(exception, "An FFmpeg log message could not be forwarded");
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// The name of whatever logged: a format (<c>matroska,webm</c>), a codec (<c>aac</c>), or
    /// <c>ffmpeg</c> when the message has no context.
    /// </summary>
    /// <remarks>
    /// Every FFmpeg object that can log starts with a pointer to its <see cref="AVClass"/>, whose
    /// item_name function names that particular instance.
    /// </remarks>
    private static string ComponentOf(void* context)
    {
        if (context is null)
        {
            return "ffmpeg";
        }

        AVClass* type = *(AVClass**)context;
        if (type is null)
        {
            return "ffmpeg";
        }

        byte* name = null;
        if (type->item_name.Pointer != IntPtr.Zero)
        {
            name = ((delegate* unmanaged[Cdecl]<void*, byte*>)type->item_name.Pointer)(context);
        }

        if (name is null)
        {
            name = type->class_name;
        }

        return name is null ? "ffmpeg" : Marshal.PtrToStringUTF8((IntPtr)name) ?? "ffmpeg";
    }

    private sealed class Repeat
    {
        public long LastLogged;
        public int Suppressed;
        public bool Logged;
    }
}
