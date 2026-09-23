using JazzHands.Audio.Output;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Playback;

/// <summary>
/// Where the playhead is, as heard.
/// </summary>
/// <remarks>
/// While audio plays, the master clock is the sound card: the frames it has actually played,
/// not the frames the mixer has rendered, which run a buffer ahead. Video presents the frame at
/// or before this time, so picture follows sound rather than the other way round, and a sound
/// card whose crystal is a little fast or slow drags the picture with it instead of drifting away.
///
/// The link between the two is an anchor: "timeline sample S is heard when the device has played
/// F frames". The audio thread sets one whenever playback starts, seeks or changes rate, at the
/// moment it hands the device the first frame of the new position. Everything else reads it.
/// The anchor is three numbers written by one thread and read by many, so it sits under a
/// sequence number rather than a lock, and the audio thread never waits for a reader.
///
/// Before the device reaches the anchor (the moment after pressing play, while the frames ahead
/// of it drain) the clock holds at the anchor rather than running backwards.
/// </remarks>
public sealed class PlaybackClock
{
    private readonly IAudioOutput _output;
    private long _sequence;
    private long _anchorSample;
    private long _anchorFrame;
    private double _rate;
    private bool _running;

    /// <summary>Creates a clock over an output's played frames.</summary>
    public PlaybackClock(IAudioOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        SampleRate = output.SampleRate;
    }

    /// <summary>The rate positions are counted in.</summary>
    public int SampleRate { get; }

    /// <summary>The timeline sample being heard now.</summary>
    public long Sample
    {
        get
        {
            long sample;
            long frame;
            double rate;
            bool running;
            long sequence;

            do
            {
                sequence = Volatile.Read(ref _sequence);
                sample = Volatile.Read(ref _anchorSample);
                frame = Volatile.Read(ref _anchorFrame);
                rate = Volatile.Read(ref _rate);
                running = Volatile.Read(ref _running);
            }
            while ((sequence & 1) != 0 || sequence != Volatile.Read(ref _sequence));

            if (!running)
            {
                return sample;
            }

            long heard = Math.Max(0, _output.FramesPlayed - frame);
            return Math.Max(0, sample + (long)Math.Round(heard * rate));
        }
    }

    /// <summary>The timeline position being heard now.</summary>
    public Flicks Now => Flicks.FromSamples(Sample, SampleRate);

    /// <summary>
    /// Pins the clock: timeline sample <paramref name="sample"/> is heard when the device has
    /// played <paramref name="frame"/> frames, and it moves at <paramref name="rate"/> from there.
    /// </summary>
    /// <remarks>Called from the audio thread. Allocates nothing.</remarks>
    internal void Anchor(long sample, long frame, double rate, bool running)
    {
        Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _anchorSample, sample);
        Volatile.Write(ref _anchorFrame, frame);
        Volatile.Write(ref _rate, rate);
        Volatile.Write(ref _running, running);
        Interlocked.Increment(ref _sequence);
    }
}
