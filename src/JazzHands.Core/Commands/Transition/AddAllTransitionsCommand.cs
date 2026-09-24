using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a transition on every cut of a track that has none.</summary>
/// <remarks>
/// One undo step. The type and duration default to the project's. A cut whose clips have too
/// little source is handled as <paramref name="Handles"/> says; refusing refuses the whole command
/// and names every cut that is short, so nothing is half done.
/// </remarks>
/// <param name="TrackId">The track.</param>
/// <param name="Type">Which transition.</param>
/// <param name="Duration">How long each runs.</param>
/// <param name="Alignment">Where each sits on its cut.</param>
/// <param name="Handles">What to do when a clip has too little source past a cut.</param>
/// <param name="Audio">Also crossfade the linked sound at the same cuts.</param>
[Command("transition.add-all", Description = "Put a transition on every cut of a track")]
public sealed record AddAllTransitionsCommand(
    [property: Option("track", "The track")] string TrackId,
    [property: Option("type", "Which transition, for example transition.crossfade")] string? Type = null,
    [property: Option("dur", "How long each runs")] Flicks? Duration = null,
    [property: Option("alignment", "centered, end-of-left or start-of-right")] TransitionAlignment Alignment = TransitionAlignment.Centered,
    [property: Option("handles", "When a clip has too little source past a cut: refuse, trim or hold")] TransitionHandles Handles = TransitionHandles.Hold,
    [property: Option("audio", "Also crossfade the linked sound at the same cuts")] bool Audio = true) : ICommand;
