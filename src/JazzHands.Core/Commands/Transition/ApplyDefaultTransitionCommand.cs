using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Which default transitions a shortcut adds.</summary>
public enum TransitionKinds
{
    /// <summary>Picture and sound: Shift+D.</summary>
    Both,

    /// <summary>The picture transition: Ctrl+D.</summary>
    Video,

    /// <summary>The sound crossfade: Ctrl+Shift+D.</summary>
    Audio,
}

/// <summary>Puts the project's default transition on the cut nearest a time: what Ctrl+D does at the playhead.</summary>
/// <remarks>
/// The cut is the one nearest <paramref name="AtCut"/>, within a second, on the given track, or
/// when none is given on the unlocked picture tracks (for video and both) or sound tracks (for
/// audio). Every track of those kinds with a cut at that same time gets one, so a picture and its
/// sound turn over together; a cut that already has a transition keeps it.
/// </remarks>
/// <param name="AtCut">A time at or near the cut, on the timeline.</param>
/// <param name="TrackId">Only this track.</param>
/// <param name="Kind">video, audio or both.</param>
/// <param name="Handles">What to do when a clip has too little source past the cut.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("transition.apply-default", Description = "Put the default transition on the cut nearest a time")]
public sealed record ApplyDefaultTransitionCommand(
    [property: Option("at-cut", "A time at or near the cut")] Flicks AtCut,
    [property: Option("track", "Only this track")] string? TrackId = null,
    [property: Option("kind", "video, audio or both")] TransitionKinds Kind = TransitionKinds.Both,
    [property: Option("handles", "When a clip has too little source past the cut: refuse, trim or hold")] TransitionHandles Handles = TransitionHandles.Hold,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
