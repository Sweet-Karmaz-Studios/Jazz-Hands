namespace JazzHands.Core.Commands;

/// <summary>Asks about one clip.</summary>
/// <param name="ClipId">Which clip.</param>
[Query("clip.get", Description = "Describe one clip")]
public sealed record GetClipQuery(
    [property: Arg(0, "The clip id")] string ClipId) : IQuery<ClipInfo>;
