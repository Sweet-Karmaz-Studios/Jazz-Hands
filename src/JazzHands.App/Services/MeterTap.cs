using JazzHands.Audio;

namespace JazzHands.App.Services;

/// <summary>
/// Lets several panels read one meter ring, which only one reader may drain.
/// </summary>
/// <remarks>
/// <para>
/// The audio thread writes a reading every block into a ring with a single consumer. The Meters
/// panel and the Mixer both want the master's, so the tap is that one consumer: whichever reader
/// asks drains the ring, and every reader's share collects what it has not seen yet.
/// </para>
/// <para>
/// A reader gets everything since it last asked folded into one reading: the loudest peak, the
/// RMS over the whole stretch, the newest hold and loudness, and a clip if any block clipped.
/// Taking only the newest block would drop the peaks of the two or three blocks mixed between two
/// ticks at 30 Hz, which is where a transient usually is.
/// </para>
/// <para>
/// The ring comes from a function because a track's can change: a track removed and put back
/// gets a new strip in the graph.
/// </para>
/// </remarks>
/// <param name="ring">The ring to drain, or null while there is none.</param>
public sealed class MeterTap(Func<MeterRing?> ring)
{
    private readonly Lock _gate = new();
    private readonly List<Reader> _readers = [];

    /// <summary>A new reader, which sees readings from now on.</summary>
    public Reader Open()
    {
        lock (_gate)
        {
            var reader = new Reader(this);
            _readers.Add(reader);
            return reader;
        }
    }

    /// <summary>Folds a later reading into an earlier one, as one reading over both blocks.</summary>
    public static void Merge(ref MeterReading into, in MeterReading next)
    {
        int channels = Math.Max(into.Channels, next.Channels);
        int frames = into.Frames + next.Frames;

        for (int channel = 0; channel < channels; channel++)
        {
            into.Peak[channel] = Math.Max(into.Peak[channel], next.Peak[channel]);
            into.Hold[channel] = next.Hold[channel];

            // RMS over both, weighted by how long each was.
            double power = (double)into.Rms[channel] * into.Rms[channel] * into.Frames
                + ((double)next.Rms[channel] * next.Rms[channel] * next.Frames);
            into.Rms[channel] = frames == 0 ? next.Rms[channel] : (float)Math.Sqrt(power / frames);
        }

        into.Channels = channels;
        into.Frames = frames;
        into.Sample = next.Sample;
        into.Clipped |= next.Clipped;
        into.TruePeak = Math.Max(into.TruePeak, next.TruePeak);
        into.ReductionDb = Math.Min(into.ReductionDb, next.ReductionDb);
        into.Momentary = next.Momentary;
        into.ShortTerm = next.ShortTerm;
        into.Integrated = next.Integrated;
    }

    private void Drain()
    {
        if (ring() is not { } source)
        {
            return;
        }

        while (source.TryRead(out MeterReading reading))
        {
            foreach (Reader reader in _readers)
            {
                reader.Take(reading);
            }
        }
    }

    /// <summary>One panel's view of the ring.</summary>
    public sealed class Reader
    {
        private readonly MeterTap _tap;
        private MeterReading _pending;
        private bool _has;

        internal Reader(MeterTap tap) => _tap = tap;

        /// <summary>Everything since the last call as one reading, or false when nothing was mixed since.</summary>
        public bool TryRead(out MeterReading reading)
        {
            lock (_tap._gate)
            {
                _tap.Drain();
                reading = _pending;
                bool had = _has;
                _has = false;
                _pending = default;
                return had;
            }
        }

        internal void Take(in MeterReading next)
        {
            if (_has)
            {
                Merge(ref _pending, next);
            }
            else
            {
                _pending = next;
                _has = true;
            }
        }
    }
}
