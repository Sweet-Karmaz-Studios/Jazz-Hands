using JazzHands.Core.Time;

namespace JazzHands.Engine.Export;

/// <summary>How far an export has got, reported about four times a second.</summary>
/// <param name="Fraction">Between zero and one.</param>
/// <param name="Frame">Frames written, for an encode; zero for a copy.</param>
/// <param name="TotalFrames">Frames to write, for an encode.</param>
/// <param name="Fps">Frames written per second of wall clock.</param>
/// <param name="Bytes">Bytes written so far.</param>
/// <param name="Encoder">The encoder at work, once it has opened.</param>
public readonly record struct ExportProgress(double Fraction, long Frame, long TotalFrames, double Fps, long Bytes, string? Encoder);

/// <summary>What an export wrote.</summary>
/// <param name="Path">The file.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Duration">How long it plays.</param>
/// <param name="Encoder">The video encoder that did the work, or copy.</param>
/// <param name="Frames">Frames encoded; zero for a copy.</param>
/// <param name="Elapsed">Wall clock.</param>
/// <param name="Notes">Things worth saying: an encoder that would not open, a fallback taken.</param>
public sealed record ExportResult(
    string Path,
    long Bytes,
    Flicks Duration,
    string Encoder,
    long Frames,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes)
{
    /// <summary>How many times faster than real time it ran.</summary>
    public double Speed => Elapsed.TotalSeconds <= 0 ? 0 : Duration.ToSeconds() / Elapsed.TotalSeconds;
}

/// <summary>Where an export renders: which device, whether to decode on it, where keyframe indexes are kept.</summary>
/// <param name="ForceWarp">Render on WARP. Tests, and machines without a GPU.</param>
/// <param name="HardwareDecode">Decode on the GPU when it can.</param>
/// <param name="Keyframes">Keyframe indexes, shared with planning.</param>
/// <param name="EncodeTextures">Hand NVENC the rendered textures rather than reading frames back (spike S3); off to measure the difference or to rule it out.</param>
public sealed record ExportEnvironment(bool ForceWarp = false, bool HardwareDecode = true, KeyframeLookup? Keyframes = null, bool EncodeTextures = true)
{
    /// <summary>The GPU if there is one, decoding on it.</summary>
    public static ExportEnvironment Default { get; } = new();
}

/// <summary>An export that ran and could not deliver what its plan asked for, such as a size it could not get under.</summary>
public sealed class ExportException : Exception
{
    /// <summary>Creates one with a message.</summary>
    public ExportException()
    {
    }

    /// <summary>Creates one with a message.</summary>
    public ExportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates one with a message and a cause.</summary>
    public ExportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
