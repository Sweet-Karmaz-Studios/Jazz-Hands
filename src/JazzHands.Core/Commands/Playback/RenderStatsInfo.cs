namespace JazzHands.Core.Commands;

/// <summary>
/// What the preview's render pools have done since the engine started, for seeing whether
/// playback allocates: in steady state every count that says created stays flat.
/// </summary>
/// <param name="TargetsCreated">Compositor render targets ever created.</param>
/// <param name="TargetsRented">Render targets handed out, recycled or new.</param>
/// <param name="TargetsOutstanding">Render targets rented and not yet returned, the layer cache's included.</param>
/// <param name="FrameTexturesCreated">Decoded frame textures ever created.</param>
/// <param name="LayersCached">Placed layers kept for a parked playhead.</param>
/// <param name="LayersDrawn">Layers the compositor has drawn.</param>
public sealed record RenderStatsInfo(
    long TargetsCreated,
    long TargetsRented,
    int TargetsOutstanding,
    long FrameTexturesCreated,
    int LayersCached,
    long LayersDrawn)
{
    /// <summary>Nothing drawn yet.</summary>
    public static RenderStatsInfo None { get; } = new(0, 0, 0, 0, 0, 0);
}
