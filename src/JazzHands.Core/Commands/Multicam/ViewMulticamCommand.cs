namespace JazzHands.Core.Commands;

/// <summary>Shows a multicam clip's angles as a grid in the program monitor, or the program again.</summary>
/// <remarks>In the grid, clicking an angle or pressing 1 to 9 while it plays cuts to it. Needs a running editor.</remarks>
/// <param name="ClipId">The multicam clip; the program again when left out.</param>
[Command("multicam.view",
    Description = "Show a multicam clip's angles in the program monitor",
    Undoable = false,
    NotUndoableReason = "The grid is how the monitor shows the clip. It does not change the project.")]
public sealed record ViewMulticamCommand(
    [property: Arg(0, "The multicam clip id; the program when left out")] string? ClipId = null) : ICommand;
