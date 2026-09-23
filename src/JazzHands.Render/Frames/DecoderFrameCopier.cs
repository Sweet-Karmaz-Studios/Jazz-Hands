using Vortice.Direct3D11;
using Vortice.DXGI;
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
        ID3D11DeviceContext context = device.ImmediateContext;

        // A semi-planar decoder surface is one texture holding both planes, and D3D copies it as
        // one subresource. The target's planes are separate textures, so the copy is per plane
        // through a view of the source's plane rectangle.
        if (target.PlaneCount == 2 && description.Format is Format.NV12 or Format.P010 or Format.P016)
        {
            CopySemiPlanar(context, source, arraySlice, description, target);
        }
        else
        {
            context.CopySubresourceRegion(
                target.Plane(0),
                0,
                0,
                0,
                0,
                source,
                (uint)CalculateSubresource(0, arraySlice, description.MipLevels));
        }

        Copied++;
    }

    /// <summary>
    /// Copies the luma and chroma halves of an NV12 or P010 surface into two textures.
    /// </summary>
    /// <remarks>
    /// Direct3D exposes the two planes of a video surface as separate subresource planes on the
    /// same texture, so the copy names a plane slice rather than a rectangle. Getting this wrong
    /// reads chroma as the bottom of luma, which looks like a green band across the lower third.
    /// </remarks>
    private static void CopySemiPlanar(
        ID3D11DeviceContext context,
        ID3D11Texture2D source,
        int arraySlice,
        Texture2DDescription description,
        FrameTexture target)
    {
        for (int plane = 0; plane < 2; plane++)
        {
            uint subresource = (uint)CalculateSubresource(0, arraySlice, description.MipLevels)
                + ((uint)plane * description.ArraySize * description.MipLevels);

            context.CopySubresourceRegion(target.Plane(plane), 0, 0, 0, 0, source, subresource);
        }
    }

    private static int CalculateSubresource(int mipSlice, int arraySlice, uint mipLevels) =>
        mipSlice + (arraySlice * (int)mipLevels);

    /// <summary>Logs how many frames have been copied out of decoders.</summary>
    public void LogTotals() => _log.Debug("Copied {Frames} frames out of decoder surfaces", Copied);
}
