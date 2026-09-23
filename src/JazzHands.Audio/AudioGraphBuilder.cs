using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Audio;

/// <summary>
/// Turns a sequence into a <see cref="MixSnapshot"/> the audio thread can read.
/// </summary>
/// <remarks>
/// Runs on whatever thread noticed the project changed, never on the audio thread: it walks the
/// model, converts every time to samples and allocates freely, so that the audio thread does
/// none of that. Cheap enough to run on every change; a thirty clip project is a few hundred
/// small objects.
///
/// Only audio tracks are mixed. A movie's sound is on audio clips of its own, linked to the
/// picture, so that muting the microphone is a track operation rather than a checkbox hidden
/// inside a video clip.
/// </remarks>
public static class AudioGraphBuilder
{
    /// <summary>Builds the mix for a sequence.</summary>
    /// <param name="project">The project, for its media and settings.</param>
    /// <param name="sequence">The sequence, or null for the active one.</param>
    public static MixSnapshot Build(Project project, Sequence? sequence = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        sequence ??= project.ActiveSequence;
        ProjectSettings settings = sequence is null ? project.Settings : project.SettingsFor(sequence);
        int rate = settings.SampleRate;
        int channels = Math.Clamp(settings.ChannelCount, 1, Dsp.MaxChannels);

        if (sequence is null)
        {
            return MixSnapshot.Silent(rate, channels);
        }

        var tracks = new List<TrackMix>();

        foreach (Track track in sequence.Tracks.OrderBy(track => track.Order))
        {
            if (track.Kind != TrackKind.Audio)
            {
                continue;
            }

            var clips = new List<ClipMix>();
            foreach (Clip clip in track.Clips)
            {
                if (BuildClip(project, clip, rate) is { } built)
                {
                    clips.Add(built);
                }
            }

            tracks.Add(new TrackMix(
                track.Id,
                track.Name,
                track.Muted,
                track.Solo,
                ScalarCurve.From(track.Volume, 0.0f, rate),
                ScalarCurve.From(track.Pan, 0.0f, rate),
                clips));
        }

        return new MixSnapshot(rate, channels, tracks);
    }

    /// <summary>
    /// The mix form of one clip, or null when it has nothing to play.
    /// </summary>
    /// <remarks>
    /// A disabled clip, a generator, a compound clip and a clip whose media or stream cannot be
    /// found are all silent. The last is not an error here: a project whose drive is unplugged
    /// still opens, and validation is where a missing file is reported.
    /// </remarks>
    internal static ClipMix? BuildClip(Project project, Clip clip, int rate)
    {
        if (!clip.Enabled || clip.MediaId is not { } mediaId || clip.Duration <= Flicks.Zero)
        {
            return null;
        }

        MediaItem? item = project.MediaItem(mediaId);
        MediaStream? stream = item?.Info?.Streams.FirstOrDefault(
            candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio);

        if (stream is null)
        {
            return null;
        }

        int channels = Math.Clamp(stream.Channels <= 0 ? 2 : stream.Channels, 1, Dsp.MaxChannels);
        Rational speed = clip.EffectiveSpeed;
        long start = clip.Start.ToSamples(rate, RoundingMode.Nearest);
        long end = clip.End.ToSamples(rate, RoundingMode.Nearest);
        Fade fadeIn = clip.FadeIn ?? Fade.None;
        Fade fadeOut = clip.FadeOut ?? Fade.None;

        return new ClipMix(
            clip.Id,
            new AudioSourceRef(mediaId, stream.Index, channels),
            start,
            end,
            clip.SourceIn.ToSamples(rate, RoundingMode.Nearest),
            clip.SourceOut.ToSamples(rate, RoundingMode.Nearest),
            speed.Num,
            speed.Den,
            clip.Reverse,
            fadeIn.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeIn.Curve,
            fadeOut.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeOut.Curve,
            ScalarCurve.From(clip.Volume, 0.0f, rate),
            ScalarCurve.From(clip.Pan, 0.0f, rate));
    }
}
