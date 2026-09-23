namespace JazzHands.Core.Commands;

/// <summary>Points a media item at a file that has moved.</summary>
/// <remarks>
/// The new file is hashed and probed. A different hash is not refused, because relinking to a
/// re-encode or a proxy is a normal thing to do, but the duration is checked: a file of a
/// different length would silently change where every cut lands.
/// </remarks>
/// <param name="MediaId">Which media item.</param>
/// <param name="Path">Where the file is now.</param>
/// <param name="Force">Accept a file of a different duration.</param>
[Command("media.relink", Description = "Point a media item at a file that has moved")]
public sealed record RelinkMediaCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Arg(1, "Where the file is now")] string Path,
    [property: Option("force", "Accept a file of a different duration")] bool Force = false) : ICommand;
