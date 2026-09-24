namespace JazzHands.Audio;

/// <summary>
/// EBU R128 loudness: momentary (400 ms), short-term (3 s) and gated integrated, in LUFS.
/// </summary>
/// <remarks>
/// <para>
/// Each channel is K weighted (<see cref="Biquad.KWeighting"/>) and its mean square taken over
/// 100 ms steps; a channel's weight is 1, or 1.41 for the surrounds of 5.1, and the low
/// frequency channel does not count, as ITU-R BS.1770 has it. Momentary is the last four steps,
/// short-term the last thirty (fewer while there are fewer, once there are four).
/// </para>
/// <para>
/// Integrated gates each 400 ms block, stepped by 100 ms: blocks under -70 LUFS are dropped, then
/// blocks more than 10 LU under the loudness of those left. The blocks are kept as a histogram
/// of tenths of a LU, energy and count per bin, so a meter that runs for an hour holds a fixed
/// thousand bins rather than thirty six thousand blocks, and allocates nothing after it is made.
/// The relative gate reads bins by their centre, which moves the answer by well under 0.05 LU.
/// </para>
/// </remarks>
public sealed class Loudness
{
    /// <summary>The level a reading has when there is nothing to measure yet.</summary>
    public const float Silent = float.NegativeInfinity;

    private const int ShortTermSteps = 30;
    private const int Bins = 1000;
    private const double Floor = -70.0;

    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly double[] _weights;
    private readonly int _stepLength;
    private readonly double[] _steps = new double[ShortTermSteps];
    private readonly double[] _binEnergy = new double[Bins];
    private readonly long[] _binCount = new long[Bins];
    private int _stepsSeen;
    private int _stepAt;
    private int _inStep;
    private double _stepSum;

    /// <summary>Creates a meter.</summary>
    /// <param name="sampleRate">The rate of what it measures.</param>
    /// <param name="channels">How many channels, in FFmpeg's order (L R C LFE Ls Rs for six).</param>
    public Loudness(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        Channels = Math.Min(channels, Dsp.MaxChannels);
        _stepLength = sampleRate / 10;
        _shelf = new Biquad[Channels];
        _highPass = new Biquad[Channels];
        _weights = new double[Channels];

        (Biquad shelf, Biquad highPass) = Biquad.KWeighting(sampleRate);
        for (int channel = 0; channel < Channels; channel++)
        {
            _shelf[channel] = shelf;
            _highPass[channel] = highPass;
            _weights[channel] = Weight(channel, Channels);
        }
    }

    /// <summary>How many channels it measures.</summary>
    public int Channels { get; }

    /// <summary>Loudness over the last 400 ms, LUFS.</summary>
    public float Momentary { get; private set; } = Silent;

    /// <summary>Loudness over the last 3 s, LUFS.</summary>
    public float ShortTerm { get; private set; } = Silent;

    /// <summary>Gated loudness since the meter was made or <see cref="ResetIntegrated"/> was called, LUFS.</summary>
    public float Integrated { get; private set; } = Silent;

    /// <summary>The weight BS.1770 gives a channel: 1, 1.41 for a 5.1 surround, 0 for low frequency.</summary>
    public static double Weight(int channel, int channels) => channels == 6
        ? channel switch
        {
            3 => 0.0,
            4 or 5 => 1.41,
            _ => 1.0,
        }
        : 1.0;

    /// <summary>Loudness of a mean square, LUFS.</summary>
    public static float ToLufs(double meanSquare) => meanSquare <= 0.0 ? Silent : (float)(-0.691 + (10.0 * Math.Log10(meanSquare)));

    /// <summary>Measures a range of a buffer.</summary>
    public void Process(AudioBuffer buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int channels = Math.Min(Channels, buffer.Channels);
        int done = 0;
        while (done < frames)
        {
            int count = Math.Min(frames - done, _stepLength - _inStep);
            for (int channel = 0; channel < channels; channel++)
            {
                double weight = _weights[channel];
                if (weight == 0.0)
                {
                    continue;
                }

                ref Biquad shelf = ref _shelf[channel];
                ref Biquad highPass = ref _highPass[channel];
                ReadOnlySpan<float> samples = buffer.Plane(channel, offset + done, count);
                double sum = 0.0;
                foreach (float sample in samples)
                {
                    float weighted = highPass.Process(shelf.Process(sample));
                    sum += (double)weighted * weighted;
                }

                _stepSum += weight * sum;
            }

            _inStep += count;
            done += count;

            if (_inStep == _stepLength)
            {
                EndStep();
            }
        }
    }

    /// <summary>Forgets the recent past (a seek): momentary and short-term start again. Integrated keeps going.</summary>
    public void Reset()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            _shelf[channel].Reset();
            _highPass[channel].Reset();
        }

        Array.Clear(_steps);
        _stepsSeen = 0;
        _stepAt = 0;
        _inStep = 0;
        _stepSum = 0.0;
        Momentary = Silent;
        ShortTerm = Silent;
    }

    /// <summary>Starts the integrated measurement again.</summary>
    public void ResetIntegrated()
    {
        Array.Clear(_binEnergy);
        Array.Clear(_binCount);
        Integrated = Silent;
    }

    private void EndStep()
    {
        _steps[_stepAt] = _stepSum / _stepLength;
        _stepAt = (_stepAt + 1) % ShortTermSteps;
        _stepsSeen++;
        _stepSum = 0.0;
        _inStep = 0;

        if (_stepsSeen < 4)
        {
            return;
        }

        double momentary = Mean(4);
        Momentary = ToLufs(momentary);
        ShortTerm = ToLufs(Mean(Math.Min(_stepsSeen, ShortTermSteps)));

        // The 400 ms block that just ended is one gating block.
        if (Momentary > Floor)
        {
            int bin = Math.Clamp((int)((Momentary - Floor) * 10.0), 0, Bins - 1);
            _binEnergy[bin] += momentary;
            _binCount[bin]++;
            Integrated = Gate();
        }
    }

    private double Mean(int steps)
    {
        double sum = 0.0;
        for (int back = 1; back <= steps; back++)
        {
            sum += _steps[((_stepAt - back) % ShortTermSteps + ShortTermSteps) % ShortTermSteps];
        }

        return sum / steps;
    }

    private float Gate()
    {
        double energy = 0.0;
        long count = 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            energy += _binEnergy[bin];
            count += _binCount[bin];
        }

        if (count == 0)
        {
            return Silent;
        }

        double relative = ToLufs(energy / count) - 10.0;
        energy = 0.0;
        count = 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            double centre = Floor + ((bin + 0.5) / 10.0);
            if (centre > relative)
            {
                energy += _binEnergy[bin];
                count += _binCount[bin];
            }
        }

        return count == 0 ? Silent : ToLufs(energy / count);
    }
}
