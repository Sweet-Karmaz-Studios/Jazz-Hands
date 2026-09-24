namespace JazzHands.Core.Commands;

/// <summary>Asks which media has a proxy, which is being made, and which would be worth one.</summary>
[Query("proxy.list", Description = "List each movie's proxy and whether one is suggested")]
public sealed record ListProxiesQuery : IQuery<ProxyInfo[]>;

/// <summary>Where a media item's proxy is.</summary>
public enum ProxyState
{
    /// <summary>There is none.</summary>
    None,

    /// <summary>Waiting on the export queue.</summary>
    Queued,

    /// <summary>Being made.</summary>
    Running,

    /// <summary>There, and played when proxies are on.</summary>
    Ready,

    /// <summary>The last attempt failed.</summary>
    Failed,
}

/// <summary>One media item's proxy.</summary>
/// <param name="MediaId">The media item.</param>
/// <param name="Name">Its name.</param>
/// <param name="State">Where its proxy is.</param>
/// <param name="Suggested">True when the source is heavy enough to want one.</param>
/// <param name="Path">The proxy file, when there is one.</param>
/// <param name="Width">The proxy's width, when there is one.</param>
/// <param name="Height">Its height.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="Progress">0 to 1 while it is being made.</param>
/// <param name="JobId">The export job making it, while there is one.</param>
/// <param name="Error">Why the last attempt failed.</param>
public sealed record ProxyInfo(
    string MediaId,
    string Name,
    ProxyState State,
    bool Suggested,
    string? Path = null,
    int Width = 0,
    int Height = 0,
    long Bytes = 0,
    double Progress = 0,
    string? JobId = null,
    string? Error = null);
