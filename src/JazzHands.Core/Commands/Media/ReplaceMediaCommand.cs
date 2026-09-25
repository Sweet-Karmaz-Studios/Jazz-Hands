namespace JazzHands.Core.Commands;

/// <summary>Swaps a media item's file for another, keeping every clip made from it.</summary>
/// <remarks>
/// For a better take, a re-render or a graded version: the clips stay where they are and play the
/// new file from the same source times. Refused when a clip would run past the end of the new
/// file, unless forced (such clips then run out of picture).
/// </remarks>
/// <param name="MediaId">Which media item.</param>
/// <param name="Path">The new file.</param>
/// <param name="Force">Accept a file too short for some clips.</param>
[Command("media.replace", Description = "Swap a media item's file for another, keeping its clips")]
public sealed record ReplaceMediaCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Arg(1, "The new file")] string Path,
    [property: Option("force", "Accept a file too short for some clips")] bool Force = false) : ICommand;
