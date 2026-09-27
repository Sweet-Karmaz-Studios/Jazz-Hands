namespace JazzHands.Core.Commands;

/// <summary>Fetches a machine learning model into the models folder.</summary>
/// <remarks>
/// From its project's own release, checked against its pinned SHA-256 before it is kept; a file
/// of the wrong hash is thrown away. <c>model.list</c> gives the names and sizes (the speech model
/// is 1.6 GB): ask the person before fetching one. Nothing is fetched by any other command.
/// </remarks>
/// <param name="Name">The model's name, from <c>model.list</c>.</param>
/// <param name="Force">Fetch it again even when it is here.</param>
[Command("model.download", Description = "Download a machine learning model, checked by its SHA-256",
    Undoable = false,
    NotUndoableReason = "It fetches a file into the models folder. The project does not change.")]
public sealed record DownloadModelCommand(
    [property: Arg(0, "The model's name, from model.list")] string Name,
    [property: Option("force", "Download it again even when present")] bool Force = false) : ICommand;
