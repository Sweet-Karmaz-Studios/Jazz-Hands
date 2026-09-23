namespace JazzHands.Core.Commands;

/// <summary>Asks about one media item, with its streams.</summary>
/// <param name="MediaId">Which media item.</param>
[Query("media.get", Description = "Describe one media item and its streams")]
public sealed record GetMediaQuery(
    [property: Arg(0, "The media id")] string MediaId) : IQuery<MediaItemInfo>;
