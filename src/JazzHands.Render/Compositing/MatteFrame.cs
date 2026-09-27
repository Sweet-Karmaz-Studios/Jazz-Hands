namespace JazzHands.Render.Compositing;

/// <summary>
/// One frame of a matte made ahead for a clip's file (Phase 43): eight bit alpha, a byte a pixel,
/// rows packed, in the file's own picture stretched to this size.
/// </summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Alpha">Width times height bytes, top row first; 255 is kept.</param>
/// <param name="Identity">Which file and frame it is, for caches.</param>
public sealed record MatteFrame(int Width, int Height, byte[] Alpha, string Identity);
