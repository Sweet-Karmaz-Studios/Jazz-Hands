using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how an ACES project brings a clip's picture into ACES (Phase 44).</summary>
/// <remarks>
/// srgb for screen captures, stills and graphics; rec709 for a Rec.709 camera; linear-rec709 for a
/// linear render; slog3, logc3 and vlog for those cameras; sdr-display or rec2100-pq for a picture
/// already rendered for a display, which then shows as it went in; auto (the default) picks rec2100-pq
/// for HDR PQ and srgb for anything else. A display-referred project ignores it.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="Transform">The input transform.</param>
[Command("clip.set-input-transform", Description = "Set how an ACES project brings a clip's picture into ACES")]
public sealed record SetClipInputTransformCommand(
    [property: Arg(0, "The clip")] string ClipId,
    [property: Arg(1, "auto, srgb, rec709, linear-rec709, slog3, logc3, vlog, sdr-display or rec2100-pq")] InputTransform Transform) : ICommand;
