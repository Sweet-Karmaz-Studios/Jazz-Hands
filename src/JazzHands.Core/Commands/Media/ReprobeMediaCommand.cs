namespace JazzHands.Core.Commands;

/// <summary>Reads a media item's file again and updates what the project remembers about it.</summary>
/// <remarks>
/// For a file that has been replaced in place. The cache entry for the old content is dropped,
/// because everything computed from it, thumbnails included, is now about a different file.
/// </remarks>
/// <param name="MediaId">Which media item. Every one when left out.</param>
[Command("media.reprobe", Description = "Read a media file again and update what the project knows")]
public sealed record ReprobeMediaCommand(
    [property: Arg(0, "The media id, or every one when left out")] string? MediaId = null) : ICommand;
