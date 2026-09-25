namespace JazzHands.Core.Commands;

/// <summary>Writes the project and every file it uses into one zip, to hand on or put away.</summary>
/// <remarks>The zip holds what <c>project.consolidate</c> would write to a folder.</remarks>
/// <param name="To">The zip file to write.</param>
/// <param name="Trim">Keep only the parts clips use, with handles.</param>
/// <param name="Handles">How much to keep either side of each used part.</param>
[Command("project.archive", Description = "Write the project and its media into one zip",
    Undoable = false,
    NotUndoableReason = "It writes a file outside the project; delete it to take it back.")]
public sealed record ArchiveProjectCommand(
    [property: Arg(0, "The zip file to write")] string To,
    [property: Option("trim", "Keep only the parts clips use, with handles")] bool Trim = false,
    [property: Option("handles", "How much to keep either side of each used part (default 1s)")] Time.Flicks? Handles = null) : ICommand;
