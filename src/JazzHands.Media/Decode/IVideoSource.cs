using JazzHands.Core.Time;

namespace JazzHands.Media.Decode;

/// <summary>
/// Somewhere frames come from, in order.
/// </summary>
/// <remarks>
/// A <see cref="VideoDecoder"/> is the root of every chain. Conform stages wrap one source and
/// present another, so the playback and export paths pull frames the same way whether the media
/// needed deinterlacing, frame rate conforming, both or neither. Every implementation is
/// thread-affine: drive one chain from one thread.
/// </remarks>
public interface IVideoSource : IDisposable
{
    /// <summary>
    /// The next frame, which the caller must dispose, or null at end of stream.
    /// </summary>
    VideoFrame? ReadFrame();

    /// <summary>
    /// Drops everything buffered. Call after seeking the demuxer, or the frames that come out
    /// next will be from before the seek.
    /// </summary>
    /// <param name="resumeAt">
    /// The source position reading will resume from. Stages that count output frames need it to
    /// know where they are; the decoder ignores it.
    /// </param>
    void Flush(Flicks resumeAt);
}
