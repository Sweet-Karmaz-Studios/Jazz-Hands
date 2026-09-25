using System.Globalization;
using JazzHands.Core.Export;
using JazzHands.Media.Interop;

namespace JazzHands.Engine.Export;

/// <summary>
/// Room on the disk an export writes to: checked before it starts, and a disk that fills while it
/// runs said in words rather than as an FFmpeg error number (Phase 33).
/// </summary>
public static class DiskSpace
{
    private const long Margin = 64L * 1024 * 1024;

    private static readonly AsyncLocal<Func<string, long?>?> Pretended = new();

    /// <summary>Free bytes in a folder's volume, or null when it cannot be told (a network share it cannot ask).</summary>
    public static long? FreeBytes(string folder) => (Pretended.Value ?? Probe)(folder);

    /// <summary>For tests: what <see cref="FreeBytes"/> answers on this flow of execution until disposed.</summary>
    public static IDisposable Pretend(Func<string, long?> free)
    {
        ArgumentNullException.ThrowIfNull(free);
        Func<string, long?>? before = Pretended.Value;
        Pretended.Value = free;
        return new Restore(() => Pretended.Value = before);
    }

    /// <summary>
    /// Refuses an export that will not fit: the plan's own estimate, a tenth more for the estimate
    /// being an estimate, and 64 MB to spare. A plan without an estimate goes ahead.
    /// </summary>
    /// <exception cref="ExportException">There is not room.</exception>
    public static void Check(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string folder = Path.GetDirectoryName(Path.GetFullPath(plan.OutputPath)) ?? ".";
        if (plan.Estimate is not { Bytes: > 0 } estimate || FreeBytes(folder) is not { } free)
        {
            return;
        }

        long needed = (long)(estimate.Bytes * 1.1) + Margin;
        if (free < needed)
        {
            throw new ExportException(string.Create(
                CultureInfo.InvariantCulture,
                $"There is not room for this export on {Volume(folder)}. It needs about {ExportPresets.FormatBytes(needed)} and {ExportPresets.FormatBytes(free)} is free. Free some space, or export somewhere else."));
        }
    }

    /// <summary>True when an error is the disk being full, however it arrived: Windows' own or FFmpeg's ENOSPC, at any depth.</summary>
    public static bool IsFull(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult == unchecked((int)0x80070070) || io.HResult == unchecked((int)0x80070027)))
            {
                return true;
            }

            // AVERROR(ENOSPC).
            if (current is FfmpegException { Code: -28 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The error a full disk becomes, naming where and what was left.</summary>
    public static ExportException Full(string outputPath, Exception cause)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        return new ExportException(
            $"{Volume(folder)} filled up while exporting. The part-written file was removed and nothing else was touched; free some space, or export somewhere else.",
            cause);
    }

    private static string Volume(string folder) => Path.GetPathRoot(folder) is { Length: > 0 } root ? root.TrimEnd(Path.DirectorySeparatorChar) : folder;

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static long? Probe(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(folder);
            return root is null || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
