using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Places a combination of effects timed to one moment: a hit, a heavy hit, a boss entrance.</summary>
/// <remarks>
/// The moment is <c>--at</c> or a marker's time (<c>--marker</c>, its name or id). With no
/// <c>--to</c> the effects go on a new adjustment clip around the moment, on the topmost
/// adjustment track with room (one is added above everything when none has), so they hit every
/// layer below. <c>--to</c> puts them on one clip or track instead. Every effect's trigger is the
/// moment, so moving the clip moves the hit; each is an ordinary effect afterwards. One undo.
/// <c>vfx.presets</c> lists the presets.
/// </remarks>
/// <param name="Preset">The preset, for example impact.heavy.</param>
/// <param name="At">The moment of the hit, on the sequence.</param>
/// <param name="Marker">A marker whose time is the moment, by name or id.</param>
/// <param name="To">A clip or track to put the effects on, rather than a new adjustment clip.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
[Command("vfx.apply-preset", Description = "Place a timed combination of effects (a hit, a heavy hit, a boss intro) at a moment or a marker")]
public sealed record ApplyVfxPresetCommand(
    [property: Arg(0, "The preset, for example impact.heavy; vfx.presets lists them")] string Preset,
    [property: Option("at", "The moment of the hit, on the sequence")] Flicks? At = null,
    [property: Option("marker", "A marker whose time is the moment, by name or id")] string? Marker = null,
    [property: Option("to", "A clip or track to put the effects on, instead of a new adjustment clip")] string? To = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;

/// <summary>The effect presets <c>vfx.apply-preset</c> places.</summary>
[Query("vfx.presets", Description = "The effect presets vfx.apply-preset places, with what each is made of")]
public sealed record ListVfxPresetsQuery : IQuery<VfxPresetInfo[]>;

/// <summary>One preset.</summary>
/// <param name="Name">What to pass to <c>vfx.apply-preset</c>.</param>
/// <param name="Description">What it looks like.</param>
/// <param name="Effects">The effect types it places, in order.</param>
/// <param name="Before">How long before the moment its adjustment clip starts.</param>
/// <param name="After">How long after the moment it lasts.</param>
public sealed record VfxPresetInfo(string Name, string Description, string[] Effects, Flicks Before, Flicks After);
