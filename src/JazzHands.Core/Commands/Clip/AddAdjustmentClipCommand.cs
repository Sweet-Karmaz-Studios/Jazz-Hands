using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts an adjustment layer over everything: effects added to it change every picture under it.</summary>
/// <remarks>
/// The clip goes on the topmost adjustment track with room for it, or on a new adjustment track
/// above everything when none has. It plays nothing of its own: its effects (a crop, a grade, a
/// blur) run over the stack of pictures below, for as long as it lasts. There was no way to make
/// one but a VFX preset (seen in QA, 2026-10-18). One undo.
/// </remarks>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="Duration">How long it lasts; five seconds when not given.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
/// <param name="ClipId">The identifier to give it. A fresh one when left out.</param>
[Command("clip.add-adjustment", Description = "Put an adjustment layer over everything, for effects that change every picture under it")]
public sealed record AddAdjustmentClipCommand(
    [property: Option("at", "Where it starts on the timeline")] Flicks At,
    [property: Option("dur", "How long it lasts; five seconds when left out")] Flicks? Duration = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null) : ICommand;
