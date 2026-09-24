namespace JazzHands.Core.Commands;

/// <summary>Turns a sequence's master limiter on or off, or sets its ceiling.</summary>
/// <remarks>
/// The limiter is the last thing on the master: it looks 5 ms ahead and keeps the true peak, the
/// level between samples a converter reconstructs, at or under the ceiling. It is on at -1 dBTP
/// until told otherwise, which is what most delivery specifications ask for; -2 is safer for a
/// file that will be encoded to AAC or Opus again. Off, the master can clip.
/// </remarks>
/// <param name="On">True to turn it on, false to turn it off. Left as it is when not given.</param>
/// <param name="Ceiling">The ceiling in dBTP, from -24 to 0. Left as it is when not given.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("audio.set-limiter", Description = "Turn the master limiter on or off, or set its ceiling in dBTP")]
public sealed record SetMasterLimiterCommand(
    [property: Option("on", "true to turn it on, false to turn it off")] bool? On = null,
    [property: Option("ceiling", "The ceiling in dBTP, -24 to 0")] double? Ceiling = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
