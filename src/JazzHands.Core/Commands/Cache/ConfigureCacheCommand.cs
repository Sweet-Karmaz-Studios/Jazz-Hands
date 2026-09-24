namespace JazzHands.Core.Commands;

/// <summary>Sets where the cache lives and how big it may get.</summary>
/// <remarks>
/// Saved in the editor's settings. A new size limit applies at once, evicting if the cache is
/// over it. A new location applies the next time the editor or <c>jazz</c> starts, and nothing is
/// moved: the old folder can be deleted, or moved there by hand before then.
/// </remarks>
/// <param name="Location">The cache folder.</param>
/// <param name="CapGb">The most thumbnails and waveforms may take, in gigabytes; 0 for no limit.</param>
[Command("cache.configure",
    Description = "Set the cache's folder and size limit",
    Undoable = false,
    NotUndoableReason = "The cache's settings belong to the editor, not the project.")]
public sealed record ConfigureCacheCommand(
    [property: Option("location", "The cache folder, used from the next start")] string? Location = null,
    [property: Option("cap-gb", "The size limit in gigabytes; 0 for none")] double? CapGb = null) : ICommand;
