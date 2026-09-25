namespace JazzHands.Core.Commands;

/// <summary>
/// Gathers a project and every file it uses into one folder: a copy that opens anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The project is written to the folder as <c>name.jazz</c>, its media under <c>media\</c>, with
/// the paths rewritten. The open project is not changed. Moved files leave their old places, so the
/// open project then finds them missing: open the gathered copy, or relink with
/// <c>media.relink --auto --search</c> the folder, which finds them by hash.
/// </para>
/// <para>
/// With <c>--trim</c> only the parts clips use are kept, with handles either side: each stretch is
/// cut out by smart cut (exact, and encoding only the frames around the cuts), and the copy's clips
/// point at the pieces. A file that cannot be smart cut, and anything that is not a moving
/// picture, is copied whole. Copying keeps each file's date, so its thumbnails and proxies follow.
/// </para>
/// </remarks>
/// <param name="To">The folder to gather into; made if it is not there.</param>
/// <param name="Trim">Keep only the used parts of each recording.</param>
/// <param name="Handles">How much to keep either side of each used part.</param>
/// <param name="Move">Move the files instead of copying them (not with <paramref name="Trim"/>).</param>
/// <param name="Overwrite">Write into a folder that already has a project in it.</param>
[Command("project.consolidate", Description = "Gather the project and its media into one folder",
    Undoable = false,
    NotUndoableReason = "It writes files outside the project; delete the folder to take it back.")]
public sealed record ConsolidateProjectCommand(
    [property: Arg(0, "The folder to gather into")] string To,
    [property: Option("trim", "Keep only the parts clips use, with handles")] bool Trim = false,
    [property: Option("handles", "How much to keep either side of each used part (default 1s)")] Time.Flicks? Handles = null,
    [property: Option("move", "Move the files instead of copying them")] bool Move = false,
    [property: Option("overwrite", "Write into a folder that already has a project in it")] bool Overwrite = false) : ICommand;
