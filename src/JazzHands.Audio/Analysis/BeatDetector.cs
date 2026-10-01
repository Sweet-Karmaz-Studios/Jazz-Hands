namespace JazzHands.Audio.Analysis;

/// <summary>One beat found in a piece of music.</summary>
/// <param name="Seconds">When, from the start of what was analysed.</param>
/// <param name="Downbeat">True for the first beat of a bar.</param>
/// <param name="Bar">Which bar, from 1; 0 for the beats before the first downbeat.</param>
/// <param name="BeatInBar">Which beat of it, from 1.</param>
/// <param name="OnOnset">True when an attack was heard there; false when the beat was filled in from the tempo.</param>
public readonly record struct Beat(double Seconds, bool Downbeat, int Bar, int BeatInBar, bool OnOnset);

/// <summary>What beat detection found.</summary>
/// <param name="Bpm">The tempo, in beats a minute.</param>
/// <param name="Confidence">How sure it is, 0 to 1: how many beats fell on an attack, and how clearly the tempo stood out.</param>
/// <param name="Beats">Every beat, in time order.</param>
/// <param name="Onsets">Every attack heard, in seconds, beats or not.</param>
public sealed record BeatAnalysis(double Bpm, double Confidence, IReadOnlyList<Beat> Beats, IReadOnlyList<double> Onsets)
{
    /// <summary>Nothing found: silence, or too short to say.</summary>
    public static BeatAnalysis None { get; } = new(0, 0, [], []);
}

/// <summary>
/// Finds the beats of music: its attacks, its tempo, where the beats fall and which of them start
/// a bar. Music at a steady tempo in four; a tempo that drifts is followed only as far as the
/// attacks lead it.
/// </summary>
/// <remarks>
/// <para>
/// Attacks come from the rise of the signal's energy in a 10 ms window stepped a millisecond at a
/// time, in decibels, so a quiet hat and a loud kick are both heard. Each attack is then placed to
/// the sample: the first after the rise where the signal reaches a fifth of its peak.
/// </para>
/// <para>
/// The tempo is the strongest period of the attack signal between 60 and 200 beats a minute, nudged
/// towards 120 as a drummer would count it; the phase is where that grid of beats lands on the most
/// attack. Beats near an attack take the attack's time; the period and phase are then fitted to
/// those, and beats with no attack keep the grid's. The downbeat is whichever of the four beats of a
/// bar is loudest on average.
/// </para>
/// </remarks>
public static class BeatDetector
{
    private const double Hop = 0.001;
    private const double Window = 0.010;

    /// <summary>Finds the beats in mono samples.</summary>
    public static BeatAnalysis Detect(ReadOnlySpan<float> samples, int rate)
    {
        if (rate <= 0 || samples.Length < rate * 2)
        {
            return BeatAnalysis.None;
        }

        int hop = Math.Max(1, (int)(rate * Hop));
        int window = Math.Max(hop, (int)(rate * Window));
        int frames = (samples.Length - window) / hop;
        if (frames < 10)
        {
            return BeatAnalysis.None;
        }

        // Energy in decibels, and its rise over three milliseconds: the attack signal.
        double[] level = new double[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double energy = 0;
            ReadOnlySpan<float> part = samples.Slice(frame * hop, window);
            foreach (float sample in part)
            {
                energy += sample * sample;
            }

            level[frame] = 10 * Math.Log10((energy / window) + 1e-10);
        }

        double[] rise = new double[frames];
        for (int frame = 3; frame < frames; frame++)
        {
            rise[frame] = Math.Max(0, level[frame] - level[frame - 3]);
        }

        List<double> onsets = Onsets(samples, rate, hop, window, rise);
        if (onsets.Count < 4)
        {
            return new BeatAnalysis(0, 0, [], onsets);
        }

        (double period, double clarity) = Tempo(rise, hop / (double)rate);
        double phase = Phase(rise, period, hop / (double)rate);
        (List<(double Time, bool Heard)> grid, period) = Grid(onsets, period, phase, samples.Length / (double)rate);

        int downbeat = Downbeat(samples, rate, grid);
        var beats = new List<Beat>(grid.Count);
        for (int index = 0; index < grid.Count; index++)
        {
            int offset = index - downbeat;
            int inBar = ((offset % 4) + 4) % 4;
            int bar = (int)Math.Floor(offset / 4.0) + 1;
            beats.Add(new Beat(grid[index].Time, inBar == 0, bar, inBar + 1, grid[index].Heard));
        }

        double heard = beats.Count == 0 ? 0 : beats.Count(beat => beat.OnOnset) / (double)beats.Count;
        return new BeatAnalysis(60 / period, Math.Clamp(heard * clarity, 0, 1), beats, onsets);
    }

    /// <summary>Attacks: peaks of the rise well above what is around them, each placed to the sample.</summary>
    private static List<double> Onsets(ReadOnlySpan<float> samples, int rate, int hop, int window, double[] rise)
    {
        var onsets = new List<double>();
        int neighbourhood = (int)(0.030 / Hop);
        int surround = (int)(0.5 / Hop);
        int gap = (int)(0.05 / Hop);
        int last = -gap;

        for (int frame = 1; frame < rise.Length - 1; frame++)
        {
            double value = rise[frame];
            if (value < 6 || frame - last < gap)
            {
                continue;
            }

            bool peak = true;
            for (int other = Math.Max(0, frame - neighbourhood); other <= Math.Min(rise.Length - 1, frame + neighbourhood) && peak; other++)
            {
                peak = rise[other] <= value || other == frame;
            }

            if (!peak)
            {
                continue;
            }

            double mean = 0;
            int from = Math.Max(0, frame - surround);
            int to = Math.Min(rise.Length - 1, frame + surround);
            for (int other = from; other <= to; other++)
            {
                mean += rise[other];
            }

            if (value < 2 * (mean / (to - from + 1)))
            {
                continue;
            }

            last = frame;
            onsets.Add(Refine(samples, rate, (frame * hop) + window - (3 * hop)));
        }

        return onsets;
    }

    /// <summary>Where an attack found near a sample starts: the first sample reaching a fifth of the peak that follows.</summary>
    private static double Refine(ReadOnlySpan<float> samples, int rate, int near)
    {
        int from = Math.Max(0, near - (int)(rate * 0.012));
        int to = Math.Min(samples.Length, near + (int)(rate * 0.020));
        float peak = 0;
        for (int index = from; index < to; index++)
        {
            peak = Math.Max(peak, Math.Abs(samples[index]));
        }

        // The quiet just before: where the signal was below a tenth of the peak for a millisecond.
        int quiet = from;
        int run = 0;
        int needed = Math.Max(1, rate / 1000);
        for (int index = from; index < to; index++)
        {
            if (Math.Abs(samples[index]) < peak * 0.1f)
            {
                if (++run >= needed)
                {
                    quiet = index;
                }
            }
            else
            {
                run = 0;
                if (Math.Abs(samples[index]) >= peak * 0.5f)
                {
                    break;
                }
            }
        }

        for (int index = quiet; index < to; index++)
        {
            if (Math.Abs(samples[index]) >= peak * 0.2f)
            {
                return index / (double)rate;
            }
        }

        return near / (double)rate;
    }

    /// <summary>
    /// The beat period in seconds, and how clearly it stood out (0 to 1). The attack signal is summed
    /// into 10 ms frames with its mean taken away, so loose or swung playing still lines up with
    /// itself; each candidate period is scored with the periods of two beats and of a bar, so the
    /// pulse a bar of four is built on wins over a subdivision of it (brushed or swung jazz otherwise
    /// reads about a quarter fast); and a gentle lean towards 120 only settles halves and doubles.
    /// </summary>
    private static (double Period, double Clarity) Tempo(double[] rise, double hop)
    {
        const double Coarse = 0.010;
        int per = Math.Max(1, (int)Math.Round(Coarse / hop));
        int count = rise.Length / per;
        int shortest = (int)Math.Floor(60.0 / 200 / Coarse);
        int longest = (int)Math.Ceiling(60.0 / 60 / Coarse);
        if (count < longest * 2)
        {
            return (0.5, 0);
        }

        double[] envelope = new double[count];
        double mean = 0;
        for (int index = 0; index < count; index++)
        {
            double sum = 0;
            for (int part = 0; part < per; part++)
            {
                sum += rise[(index * per) + part];
            }

            envelope[index] = sum;
            mean += sum;
        }

        mean /= count;
        for (int index = 0; index < count; index++)
        {
            envelope[index] -= mean;
        }

        int furthest = Math.Min(count / 2, longest * 4 + 1);
        double[] correlation = new double[furthest + 2];
        for (int lag = 1; lag <= furthest; lag++)
        {
            double sum = 0;
            for (int index = lag; index < count; index++)
            {
                sum += envelope[index] * envelope[index - lag];
            }

            correlation[lag] = sum / (count - lag);
        }

        double Near(int lag) => lag + 1 > furthest
            ? 0
            : Math.Max(correlation[lag], Math.Max(correlation[lag - 1], correlation[lag + 1]));

        double best = double.MinValue;
        int bestLag = shortest;
        double total = 0;
        int counted = 0;
        double[] scores = new double[longest + 2];
        for (int lag = shortest; lag <= longest; lag++)
        {
            double support = correlation[lag] + (0.5 * Near(2 * lag)) + (0.5 * Near(4 * lag));
            double octaves = Math.Log2(lag * Coarse / 0.5);
            double weighted = support * Math.Exp(-0.5 * octaves * octaves);
            scores[lag] = weighted;
            total += Math.Max(0, weighted);
            counted++;
            if (weighted > best)
            {
                best = weighted;
                bestLag = lag;
            }
        }

        // Between frames: a parabola through the best score and its neighbours.
        double refined = bestLag;
        if (bestLag > shortest && bestLag < longest)
        {
            double before = scores[bestLag - 1], after = scores[bestLag + 1];
            double bend = before - (2 * best) + after;
            if (bend < 0)
            {
                refined += 0.5 * (before - after) / bend;
            }
        }

        double average = counted == 0 ? 0 : total / counted;
        return (refined * Coarse, best <= 0 ? 0 : Math.Clamp(1 - (average / best), 0, 1));
    }

    /// <summary>Where the grid of beats at a period lands on the most attack.</summary>
    private static double Phase(double[] rise, double period, double hop)
    {
        int steps = Math.Max(1, (int)(period / hop));
        double best = double.MinValue;
        int bestOffset = 0;
        for (int offset = 0; offset < steps; offset++)
        {
            double sum = 0;
            for (double at = offset * hop; at / hop < rise.Length; at += period)
            {
                int frame = (int)Math.Round(at / hop);
                for (int near = Math.Max(0, frame - 5); near <= Math.Min(rise.Length - 1, frame + 5); near++)
                {
                    sum += rise[near];
                }
            }

            if (sum > best)
            {
                best = sum;
                bestOffset = offset;
            }
        }

        return bestOffset * hop;
    }

    /// <summary>The beats: the grid snapped to attacks near it, with the period and phase fitted to those.</summary>
    private static (List<(double Time, bool Heard)> Beats, double Period) Grid(List<double> onsets, double period, double phase, double length)
    {
        double tolerance = Math.Min(0.06, period * 0.12);

        // Fit period and phase to the attacks that fall on the grid: beat index against time.
        for (int pass = 0; pass < 2; pass++)
        {
            var matched = new List<(double Index, double Time)>();
            foreach (double onset in onsets)
            {
                double index = Math.Round((onset - phase) / period);
                if (Math.Abs(onset - (phase + (index * period))) <= tolerance)
                {
                    matched.Add((index, onset));
                }
            }

            if (matched.Count < 3)
            {
                break;
            }

            double meanIndex = matched.Average(match => match.Index);
            double meanTime = matched.Average(match => match.Time);
            double covariance = matched.Sum(match => (match.Index - meanIndex) * (match.Time - meanTime));
            double variance = matched.Sum(match => (match.Index - meanIndex) * (match.Index - meanIndex));
            if (variance > 0)
            {
                period = covariance / variance;
                phase = meanTime - (period * meanIndex);
            }
        }

        // From the first beat at or after the start to the end.
        double first = phase - (Math.Floor(phase / period) * period);
        var beats = new List<(double Time, bool Heard)>();
        int next = 0;
        for (double at = first; at < length; at += period)
        {
            while (next < onsets.Count && onsets[next] < at - tolerance)
            {
                next++;
            }

            if (next < onsets.Count && Math.Abs(onsets[next] - at) <= tolerance)
            {
                beats.Add((onsets[next], true));
            }
            else
            {
                beats.Add((at, false));
            }
        }

        return (beats, period);
    }

    /// <summary>Which of the first four beats starts a bar: the one whose beats are loudest on average.</summary>
    private static int Downbeat(ReadOnlySpan<float> samples, int rate, List<(double Time, bool Heard)> beats)
    {
        double[] loudness = new double[4];
        int[] counts = new int[4];
        for (int index = 0; index < beats.Count; index++)
        {
            int start = (int)(beats[index].Time * rate);
            int end = Math.Min(samples.Length, start + (int)(rate * 0.05));
            float peak = 0;
            for (int at = Math.Max(0, start); at < end; at++)
            {
                peak = Math.Max(peak, Math.Abs(samples[at]));
            }

            loudness[index % 4] += peak;
            counts[index % 4]++;
        }

        int best = 0;
        for (int phase = 1; phase < 4; phase++)
        {
            if (counts[phase] > 0 && loudness[phase] / counts[phase] > loudness[best] / Math.Max(1, counts[best]))
            {
                best = phase;
            }
        }

        return best;
    }
}
