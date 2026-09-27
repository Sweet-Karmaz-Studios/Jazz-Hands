namespace JazzHands.Audio;

/// <summary>
/// One audio stream of one media item, as the graph asks for it.
/// </summary>
/// <param name="MediaId">The media item.</param>
/// <param name="StreamIndex">The stream within the file, as the container numbers it.</param>
/// <param name="Channels">How many channels the stream has, which is how many planes a read fills.</param>
/// <param name="Stretch">
/// For a clip that keeps its pitch at a speed (Phase 36), how its sound is stretched: then positions
/// are the clip's own samples, read straight, and the engine renders them; null for the file's samples.
/// </param>
public readonly record struct AudioSourceRef(string MediaId, int StreamIndex, int Channels, StretchPlan? Stretch = null);

/// <summary>
/// A stretch of source samples the graph is about to need, for decoding ahead.
/// </summary>
/// <param name="Source">Which stream.</param>
/// <param name="StartSample">The first sample, at the mix rate.</param>
/// <param name="Frames">How many.</param>
public readonly record struct SourceDemand(AudioSourceRef Source, long StartSample, long Frames);

/// <summary>
/// Where the graph gets decoded samples from.
/// </summary>
/// <remarks>
/// The graph cannot see the media layer; the dependency rule puts Audio beside Media rather than
/// above it. So Audio says what it needs and the engine, which sees both, provides it over the
/// decoder and the block cache. It is the same shape as the video side, where the compositor is
/// handed frames rather than going to fetch them.
///
/// Samples arrive at the mix rate, in the stream's own channel count. Getting from the source's
/// layout to the mix's is a matrix, and the matrix depends on the pan, so it belongs to the graph.
/// </remarks>
public interface IAudioSampleSource
{
    /// <summary>
    /// Copies samples of a source into a buffer.
    /// </summary>
    /// <remarks>
    /// Called on the audio thread during playback, so it must not block, lock, log or allocate.
    /// What is not ready is written as silence and the call returns false, which the graph
    /// counts; a stutter is better than a stall. Positions before the source's start and after
    /// its end are silence too, and return true, because there is nothing to wait for.
    /// </remarks>
    /// <param name="source">Which stream.</param>
    /// <param name="startSample">The first sample wanted, at the mix rate. May be negative.</param>
    /// <param name="destination">Where to write, with at least <see cref="AudioSourceRef.Channels"/> planes.</param>
    /// <param name="offset">Where in each plane to start writing.</param>
    /// <param name="frames">How many samples each plane gets.</param>
    /// <returns>False when some of the range was not decoded in time.</returns>
    bool Read(AudioSourceRef source, long startSample, AudioBuffer destination, int offset, int frames);
}
