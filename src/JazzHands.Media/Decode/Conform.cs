using JazzHands.Core.Model;
using JazzHands.Core.Time;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>
/// Builds the chain of stages that turns what a file actually contains into what the timeline
/// expects, from the conform settings on a <see cref="MediaItem"/>.
/// </summary>
/// <remarks>
/// Import decides what a file needs and records it on the media item; this decides how to honour
/// it. Keeping the two apart means a user who disagrees with the import can change the setting on
/// the item and the next decode does the other thing, with no reimport and no cache to clear.
/// </remarks>
public static class Conform
{
    /// <summary>
    /// Whether decoding this media has to happen in software.
    /// </summary>
    /// <remarks>
    /// Deinterlacing runs through libavfilter, which cannot see a Direct3D texture. Everything
    /// else in the chain works on frames wherever they are, so this is the only reason to give up
    /// hardware decode.
    /// </remarks>
    public static bool NeedsSoftwareDecode(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ShouldDeinterlace;
    }

    /// <summary>
    /// Wraps a decoder in whatever conform stages the media needs.
    /// </summary>
    /// <param name="source">The decoder, or an already wrapped source. Passed to the result to own.</param>
    /// <param name="item">The media item, which carries the conform settings.</param>
    /// <param name="projectRate">The timeline frame rate a variable source is conformed to.</param>
    /// <param name="sourceRate">
    /// The file's nominal frame rate, which the deinterlacer needs. Defaults to the project rate
    /// when the caller does not know it.
    /// </param>
    /// <returns>
    /// The source itself when nothing needs conforming, so the common case costs nothing.
    /// </returns>
    public static IVideoSource Apply(
        IVideoSource source,
        MediaItem item,
        Rational projectRate,
        Rational? sourceRate = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(item);

        IVideoSource result = source;

        if (item.ShouldDeinterlace)
        {
            result = new Deinterlacer(result, sourceRate ?? projectRate);
        }

        if (item.ShouldConformFrameRate)
        {
            result = new FrameRateConformer(result, projectRate);
        }

        if (!ReferenceEquals(result, source))
        {
            Log.ForContext(typeof(Conform)).Debug(
                "Conforming {Media}: deinterlace {Deinterlace}, frame rate {Conform}",
                item.Name,
                item.ShouldDeinterlace,
                item.ShouldConformFrameRate);
        }

        return result;
    }
}
