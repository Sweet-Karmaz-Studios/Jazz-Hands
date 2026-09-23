using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Serilog;

namespace JazzHands.Render.Frames;

/// <summary>
/// Copies a hardware decoded frame out of the decoder's texture array into textures the cache can
/// keep.
/// </summary>
/// <remarks>
/// A decoder allocates a fixed number of surfaces and hands out slices of them. Holding one is
/// holding the decoder's, so a cache that kept eight would stall the decoder that filled them.
/// This is a GPU to GPU copy: the pixels never leave video memory, which is the whole point.
///
/// It is still a copy, and it is the price of a frame outliving the decode that produced it. A
/// frame going straight to the compositor should be sampled from the decoder's array instead; see
/// the hw-decode skill.
/// </remarks>
public sealed class DecoderFrameCopier(RenderDevice device)
{
    private readonly ILogger _log = Log.ForContext<DecoderFrameCopier>();

    /// <summary>Frames copied out of a decoder, for the diagnostics.</summary>
    public long Copied { get; private set; }

    /// <summary>
    /// Copies one slice of a decoder's texture array into a frame texture.
    /// </summary>
    /// <param name="decoderTexture">The decoder's <c>ID3D11Texture2D</c>, as the decoded frame reports it.</param>
    /// <param name="arraySlice">Which slice of it the frame occupies.</param>
    /// <param name="target">A texture rented with <see cref="FrameTextureUsage.Copy"/>.</param>
    public void Copy(IntPtr decoderTexture, int arraySlice, FrameTexture target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (decoderTexture == IntPtr.Zero)
        {
            throw new ArgumentException("The decoder gave no texture to copy from.", nameof(decoderTexture));
        }

        // The pointer belongs to the decoder's frame. Wrapping it adds a reference this method
        // gives back, so the decoder's own release still frees it at the right time.
        using var source = new ID3D11Texture2D(decoderTexture);
        source.AddRef();

        Texture2DDescription description = source.Description;
        ID3D11Texture2D destination = target.Plane(0);
        Format expected = destination.Description.Format;

        // Direct3D 11 copies a video surface only into a texture of its own format, and a copy
        // that breaks the rule does nothing and says nothing. Better to fail loudly here than to
        // show a black frame.
        if (description.Format != expected)
        {
            throw new InvalidOperationException(
                $"The decoder wrote {description.Format} and the frame texture is {expected}. "
                + "A hardware frame's layout comes from the stream's bit depth; check what import recorded.");
        }

        // The box is the picture, not the surface: a decoder pads its surfaces to whole
        // macroblocks, so 1080 lines arrive in a 1088 line surface, and copying the whole
        // subresource into a 1080 line texture would be invalid. For a video format the box is in
        // luma texels and the chroma plane comes with it.
        var box = new Box(0, 0, 0, target.Width, target.Height, 1);

        device.ImmediateContext.CopySubresourceRegion(
            destination,
            0,
            0,
            0,
            0,
            source,
            (uint)CalculateSubresource(0, arraySlice, description.MipLevels),
            box);

        Copied++;
    }

    private static int CalculateSubresource(int mipSlice, int arraySlice, uint mipLevels) =>
        mipSlice + (arraySlice * (int)mipLevels);

    /// <summary>Logs how many frames have been copied out of decoders.</summary>
    public void LogTotals() => _log.Debug("Copied {Frames} frames out of decoder surfaces", Copied);
}
