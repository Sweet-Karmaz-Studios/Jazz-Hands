using System.Runtime.CompilerServices;
using FFmpeg.AutoGen;

namespace JazzHands.Media.Interop;

/// <summary>
/// Turns FFmpeg return codes into exceptions, and FFmpeg's log stream into Serilog.
/// </summary>
/// <remarks>
/// FFmpeg reports everything as a negative int. Two of those are control flow rather than
/// failure: <see cref="Again"/> means "feed me more" and <see cref="EndOfFile"/> means "drained".
/// Handle both before calling <see cref="Check"/>.
/// </remarks>
public static unsafe class Av
{
    /// <summary>AVERROR(EAGAIN). The codec wants more input, or has no output yet.</summary>
    public static readonly int Again = -11;

    /// <summary>AVERROR_EOF. The stream is drained.</summary>
    public static readonly int EndOfFile = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));

    /// <summary>AVERROR(ENOMEM).</summary>
    public static readonly int OutOfMemory = -12;

    /// <summary>AVERROR(EINVAL).</summary>
    public static readonly int InvalidArgument = -22;

    /// <summary>AVERROR_DECODER_NOT_FOUND.</summary>
    public static readonly int DecoderNotFound = FfmpegTag(0xF8, 'D', 'E', 'C');

    /// <summary>Throws <see cref="FfmpegException"/> when <paramref name="code"/> is negative.</summary>
    /// <param name="code">The FFmpeg return code.</param>
    /// <param name="operation">The call that produced it, for the message.</param>
    /// <param name="context">What was being worked on, usually a file path.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Check(int code, string operation, string? context = null) =>
        code < 0 ? throw new FfmpegException(code, operation, context) : code;

    /// <summary>Allocates and throws when FFmpeg hands back null.</summary>
    public static T* CheckAlloc<T>(T* pointer, string operation)
        where T : unmanaged =>
        pointer is null
            ? throw new FfmpegException(OutOfMemory, operation, context: null)
            : pointer;

    /// <summary>Renders an FFmpeg error code as text, the way av_strerror does.</summary>
    public static string DescribeError(int code)
    {
        const int bufferSize = 1024;
        byte* buffer = stackalloc byte[bufferSize];
        return ffmpeg.av_strerror(code, buffer, bufferSize) == 0
            ? new string((sbyte*)buffer)
            : $"unknown FFmpeg error {code}";
    }

    /// <summary>Reads a nullable FFmpeg string, which is what most metadata accessors return.</summary>
    public static string? ReadString(byte* value) => value is null ? null : new string((sbyte*)value);

    /// <summary>Copies an AVDictionary into a managed dictionary, lower-cased keys.</summary>
    public static IReadOnlyDictionary<string, string> ReadDictionary(AVDictionary* dictionary)
    {
        if (dictionary is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dictionary, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
        {
            string? key = ReadString(entry->key);
            string? value = ReadString(entry->value);
            if (key is not null && value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static int FfmpegTag(int a, char b, char c, char d) =>
        -(a | (b << 8) | (c << 16) | (d << 24));
}

/// <summary>An FFmpeg call that failed, with the code, the call and what it was working on.</summary>
public sealed class FfmpegException : Exception
{
    /// <summary>Creates the exception from an FFmpeg return code.</summary>
    public FfmpegException(int code, string operation, string? context)
        : base(BuildMessage(code, operation, context))
    {
        Code = code;
        Operation = operation;
        Context = context;
    }

    /// <summary>Creates the exception with a message of your own.</summary>
    public FfmpegException(string message)
        : base(message)
    {
        Operation = string.Empty;
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public FfmpegException(string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = string.Empty;
    }

    /// <summary>The negative FFmpeg error code.</summary>
    public int Code { get; }

    /// <summary>The FFmpeg function that failed.</summary>
    public string Operation { get; }

    /// <summary>The file or stream being worked on, when there was one.</summary>
    public string? Context { get; }

    private static string BuildMessage(int code, string operation, string? context)
    {
        string where = context is null ? string.Empty : $" for '{context}'";
        return $"{operation}{where} failed: {Av.DescribeError(code)} ({code}).";
    }
}
