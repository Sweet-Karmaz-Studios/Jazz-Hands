namespace JazzHands.Core.Export;

/// <summary>Where an export job has got to.</summary>
public enum ExportJobState
{
    /// <summary>Waiting for the worker.</summary>
    Queued,

    /// <summary>Being written now.</summary>
    Running,

    /// <summary>Written and closed.</summary>
    Done,

    /// <summary>Stopped by an error; <see cref="ExportJobInfo.Error"/> says what.</summary>
    Failed,

    /// <summary>Stopped on request. The partial file was deleted.</summary>
    Cancelled,
}

/// <summary>One job in the export queue, as the queue panel, <c>export.list</c> and MCP see it.</summary>
/// <param name="Id">The job id.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="OutputPath">The file it writes.</param>
/// <param name="Preset">The preset it was planned from.</param>
/// <param name="Mode">copy or encode.</param>
/// <param name="Progress">Between zero and one.</param>
/// <param name="Frame">Frames written, for an encode.</param>
/// <param name="TotalFrames">Frames to write, for an encode.</param>
/// <param name="Fps">Frames written per second of wall clock, for an encode.</param>
/// <param name="EtaSeconds">Seconds left at the current rate, or null when it cannot say.</param>
/// <param name="Bytes">Bytes written so far.</param>
/// <param name="Encoder">The encoder that did the work, once one has opened: h264_nvenc, libx264, or copy.</param>
/// <param name="Error">What went wrong, for a failed job.</param>
/// <param name="Created">When it was queued.</param>
/// <param name="Finished">When it stopped, for any end state.</param>
/// <param name="Note">Anything worth knowing that is not an error: a fallback encoder, keyframe snaps.</param>
public sealed record ExportJobInfo(
    string Id,
    ExportJobState State,
    string OutputPath,
    string Preset,
    ExportMode Mode,
    double Progress,
    long Frame,
    long TotalFrames,
    double Fps,
    double? EtaSeconds,
    long Bytes,
    string? Encoder,
    string? Error,
    DateTimeOffset Created,
    DateTimeOffset? Finished,
    string? Note = null)
{
    /// <summary>True for a job that will not change again.</summary>
    public bool IsFinished => State is ExportJobState.Done or ExportJobState.Failed or ExportJobState.Cancelled;
}
