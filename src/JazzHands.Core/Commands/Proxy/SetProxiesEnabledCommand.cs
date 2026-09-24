namespace JazzHands.Core.Commands;

/// <summary>Plays proxies in place of their sources, where there are any, or stops.</summary>
/// <remarks>
/// Playback, scrubbing and the preview only. Export always reads the sources, whatever this says.
/// </remarks>
/// <param name="Enabled">True to play proxies.</param>
[Command("proxy.set-enabled",
    Description = "Play proxies instead of their sources, or stop",
    Undoable = false,
    NotUndoableReason = "Whether to play proxies is a setting of the editor, not of the project.")]
public sealed record SetProxiesEnabledCommand(
    [property: Arg(0, "true to play proxies, false for the sources")] bool Enabled) : ICommand;
