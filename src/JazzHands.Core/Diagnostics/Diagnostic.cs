namespace JazzHands.Core.Diagnostics;

/// <summary>How much something a session noticed matters.</summary>
public enum DiagnosticLevel
{
    /// <summary>Worth knowing. A fallback that still plays correctly.</summary>
    Information,

    /// <summary>Something will look or behave worse than it should.</summary>
    Warning,

    /// <summary>Something will not work at all.</summary>
    Error,
}

/// <summary>
/// Something a session noticed about one media item.
/// </summary>
/// <remarks>
/// In Core because every surface reports it: the editor shows it as a notice, <c>jazz
/// diagnostics list</c> prints it, and the control server sends it. The thing that collects them
/// lives in the engine; this is only what one of them is.
/// </remarks>
/// <param name="MediaId">Which media item, or empty when it is about the session itself.</param>
/// <param name="Name">The item's display name, so a message reads without a lookup.</param>
/// <param name="Code">A stable machine readable code, the same across every surface.</param>
/// <param name="Message">What it means, in a sentence somebody can act on.</param>
/// <param name="Level">How much it matters.</param>
/// <param name="At">When it was first noticed.</param>
/// <param name="Occurrences">How many times it has happened since.</param>
public sealed record Diagnostic(
    string MediaId,
    string Name,
    string Code,
    string Message,
    DiagnosticLevel Level,
    DateTimeOffset At,
    long Occurrences = 1);

/// <summary>The codes the engine reports, so every surface spells them the same way.</summary>
public static class DiagnosticCodes
{
    /// <summary>A file that should have decoded on the GPU did not.</summary>
    public const string DecoderFellBack = "decoder-fell-back";

    /// <summary>A media item's file is not where the project says it is.</summary>
    public const string MediaMissing = "media-missing";

    /// <summary>A file could not be decoded at all.</summary>
    public const string DecodeFailed = "decode-failed";

    /// <summary>The frame cache is full of pinned frames and cannot honour its budget.</summary>
    public const string FrameCacheOverBudget = "frame-cache-over-budget";

    /// <summary>A plugin effect in the project names a plugin this computer does not have; it is bypassed.</summary>
    public const string PluginMissing = "plugin-missing";

    /// <summary>A sound effect is on a clip when it goes on a track, or the other way round: a hand edit put it there.</summary>
    public const string EffectMisplaced = "effect-misplaced";
}
