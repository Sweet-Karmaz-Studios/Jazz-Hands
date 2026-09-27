using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how the project handles colour (Phase 44): display referred, or ACES.</summary>
/// <remarks>
/// Only what is given changes. In ACES, every picture comes in through its clip's input transform
/// (<c>clip.set-input-transform</c>), blends in ACEScg, grades in ACEScct and shows through the ACES
/// 2.0 output transform for the chosen display. Display referred, the default, is how every
/// project looked before, and stores nothing.
/// </remarks>
/// <param name="Pipeline">display-referred or aces.</param>
/// <param name="Output">What an ACES project is rendered for: rec709 (SDR) or hdr10.</param>
[Command("project.set-color-management", Description = "Set the project's colour management: display referred or ACES")]
public sealed record SetColorManagementCommand(
    [property: Option("pipeline", "display-referred or aces")] ColorPipeline? Pipeline = null,
    [property: Option("output", "What an ACES project is rendered for: rec709 or hdr10")] AcesOutput? Output = null) : ICommand;
