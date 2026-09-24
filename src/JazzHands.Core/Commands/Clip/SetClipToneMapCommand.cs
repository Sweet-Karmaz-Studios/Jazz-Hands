using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how one clip's HDR picture is tone mapped, over the project's default.</summary>
/// <remarks>
/// Only what is given changes; the rest comes from the clip's current setting, or the project's
/// when it has none. <c>--reset</c> removes the clip's own setting so it follows the project again.
/// An SDR clip keeps the setting and ignores it.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on a video or adjustment track.</param>
/// <param name="Operator">bt2390, hable, mobius or clip.</param>
/// <param name="PeakNits">The source's peak in nits, 100 to 10000, overriding what the file says.</param>
/// <param name="Desaturate">How much compressed highlights lose their colour, 0 to 1.</param>
/// <param name="Reset">Follow the project's default again.</param>
[Command("clip.set-tone-map", Description = "Set how a clip's HDR picture is brought down to SDR")]
public sealed record SetClipToneMapCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("operator", "bt2390, hable, mobius or clip")] ToneMapOperator? Operator = null,
    [property: Option("peak", "The source's peak in nits, overriding the file's; 100 to 10000")] double? PeakNits = null,
    [property: Option("desaturate", "How much compressed highlights lose their colour, 0 to 1")] double? Desaturate = null,
    [property: Option("reset", "Follow the project's default again")] bool Reset = false) : ICommand;
