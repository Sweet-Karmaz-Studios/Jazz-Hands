using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>
/// Motion blur on what the compositor animates: a clip's transform, crop and masks, and a
/// generator's own parameters (a title flying in).
/// </summary>
/// <remarks>
/// A frame of an animated layer is the average of the layer drawn at <see cref="Samples"/> moments
/// spread across the shutter, which is open for <see cref="ShutterAngle"/> of 360 degrees of a
/// frame, centred on the frame's time, as a film camera's is at 180 degrees. The source's own
/// motion is not blurred here; only how the layer is placed. A layer that does not move costs
/// nothing and draws exactly as without blur.
///
/// Set on a sequence it is every layer's; a track's replaces the sequence's for its clips; a
/// clip's replaces both. <see cref="Enabled"/> false on a clip or track turns it off there.
/// </remarks>
/// <param name="ShutterAngle">How long the shutter is open, in degrees of a frame: 180 is half a frame, 360 a whole one.</param>
/// <param name="Samples">How many moments across the shutter are drawn and averaged.</param>
/// <param name="Enabled">False turns motion blur off here even where a track or the sequence has it on.</param>
public sealed record MotionBlur(double ShutterAngle = MotionBlur.DefaultAngle, int Samples = MotionBlur.DefaultSamples, bool Enabled = true) : IEquatable<MotionBlur>
{
    /// <summary>The shutter angle a new setting starts at.</summary>
    public const double DefaultAngle = 180.0;

    /// <summary>The sample count a new setting starts at.</summary>
    public const int DefaultSamples = 32;

    /// <summary>The most samples a frame may take.</summary>
    public const int MaxSamples = 64;

    /// <summary>The widest shutter.</summary>
    public const double MaxAngle = 720.0;

    /// <summary>What applies to a clip: its own, else its track's, else its sequence's; null when off.</summary>
    public static MotionBlur? For(Clip clip, Track track, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(sequence);

        MotionBlur? blur = clip.MotionBlur ?? track.MotionBlur ?? sequence.MotionBlur;
        return blur is { Enabled: true, Samples: > 1, ShutterAngle: > 0 } ? blur : null;
    }

    /// <summary>
    /// The moments a frame at <paramref name="time"/> averages, spread evenly across the open
    /// shutter and centred on it, at most <paramref name="maxSamples"/> of them.
    /// </summary>
    public IReadOnlyList<Flicks> Moments(Flicks time, Rational frameRate, int maxSamples = MaxSamples)
    {
        int count = Math.Clamp(Math.Min(Samples, maxSamples), 1, MaxSamples);
        if (count == 1 || frameRate.IsZero)
        {
            return [time];
        }

        double exposure = Flicks.FromFrames(1, frameRate).Value * Math.Clamp(ShutterAngle, 0, MaxAngle) / 360.0;
        var moments = new Flicks[count];
        for (int index = 0; index < count; index++)
        {
            moments[index] = time + new Flicks((long)Math.Round(exposure * ((index / (double)(count - 1)) - 0.5)));
        }

        return moments;
    }
}
