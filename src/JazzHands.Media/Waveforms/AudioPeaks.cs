using JazzHands.Core.Time;

namespace JazzHands.Media.Waveforms;

/// <summary>
/// A stream's sound as the lowest and highest sample in every millisecond, all channels together.
/// </summary>
/// <remarks>
/// <para>
/// One millisecond is finer than any timeline zoom draws and coarse enough that ten minutes is
/// 1.2 MB: two signed bytes a window, -127 to 127 for -1 to 1. Coarser views are asked for with
/// <see cref="Range"/>, which reads a pyramid of blocks of 16, 256 and 4096 windows so that a
/// column covering a minute costs a few dozen reads rather than sixty thousand.
/// </para>
/// <para>
/// Immutable, so a snapshot of a waveform still being read can be handed to the UI thread while
/// the worker carries on. <see cref="IsComplete"/> says whether there is more to come.
/// </para>
/// </remarks>
public sealed class AudioPeaks
{
    /// <summary>Windows per second of sound.</summary>
    public const int WindowsPerSecond = 1000;

    private const int Fan = 16;
    private const int Levels = 3;

    private readonly sbyte[] _minimum;
    private readonly sbyte[] _maximum;
    private readonly Lock _gate = new();
    private sbyte[][]? _pyramidMinimum;
    private sbyte[][]? _pyramidMaximum;

    /// <summary>Wraps peak arrays. They are not copied and must not change afterwards.</summary>
    /// <param name="minimum">The lowest sample of each window.</param>
    /// <param name="maximum">The highest sample of each window.</param>
    /// <param name="windows">How many windows are filled in, from the start.</param>
    /// <param name="isComplete">True when the whole stream has been read.</param>
    public AudioPeaks(sbyte[] minimum, sbyte[] maximum, int windows, bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        ArgumentNullException.ThrowIfNull(maximum);
        ArgumentOutOfRangeException.ThrowIfNegative(windows);

        if (minimum.Length < windows || maximum.Length < windows)
        {
            throw new ArgumentException("The arrays are shorter than the windows they claim to hold.", nameof(windows));
        }

        _minimum = minimum;
        _maximum = maximum;
        Windows = windows;
        IsComplete = isComplete;
    }

    /// <summary>Nothing read yet.</summary>
    public static AudioPeaks Empty { get; } = new([], [], 0, isComplete: false);

    /// <summary>Filled windows, from the start of the stream.</summary>
    public int Windows { get; }

    /// <summary>True when the whole stream has been read; false for a snapshot taken on the way.</summary>
    public bool IsComplete { get; }

    /// <summary>How much of the stream the filled windows cover.</summary>
    public Flicks Covered => Flicks.FromSeconds((double)Windows / WindowsPerSecond);

    /// <summary>The window a source time falls in.</summary>
    public static long WindowAt(Flicks time) => time.Value * WindowsPerSecond / Flicks.PerSecond;

    /// <summary>The lowest and highest sample in a run of windows, -1 to 1, or false when none of it is filled.</summary>
    /// <param name="first">The first window.</param>
    /// <param name="end">One past the last.</param>
    /// <param name="minimum">The lowest sample.</param>
    /// <param name="maximum">The highest sample.</param>
    public bool Range(long first, long end, out float minimum, out float maximum)
    {
        first = Math.Max(first, 0);
        end = Math.Min(end, Windows);

        if (end <= first)
        {
            minimum = 0;
            maximum = 0;
            return false;
        }

        (sbyte[][] lows, sbyte[][] highs) = Pyramid();

        int low = sbyte.MaxValue;
        int high = sbyte.MinValue;
        long lo = first;
        long hi = end;
        int level = 0;
        long size = 1;

        // Climb while a whole block of the next level fits, reading the ragged ends at this one.
        // The end of the data need not be aligned: the last block of every level covers the
        // windows there are.
        while (level < Levels)
        {
            long next = size * Fan;
            long up = (lo + next - 1) / next * next;
            long down = hi / next * next;

            if (up >= down)
            {
                break;
            }

            Scan(level == 0 ? _minimum : lows[level - 1], level == 0 ? _maximum : highs[level - 1], lo / size, up / size, ref low, ref high);
            Scan(level == 0 ? _minimum : lows[level - 1], level == 0 ? _maximum : highs[level - 1], down / size, (hi + size - 1) / size, ref low, ref high);

            lo = up;
            hi = down;
            level++;
            size = next;
        }

        Scan(level == 0 ? _minimum : lows[level - 1], level == 0 ? _maximum : highs[level - 1], lo / size, (hi + size - 1) / size, ref low, ref high);

        minimum = low / 127.0f;
        maximum = high / 127.0f;
        return true;
    }

    /// <summary>The peaks as stored: a pair of signed bytes, lowest then highest, for each window.</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[Windows * 2];
        for (int window = 0; window < Windows; window++)
        {
            bytes[window * 2] = unchecked((byte)_minimum[window]);
            bytes[(window * 2) + 1] = unchecked((byte)_maximum[window]);
        }

        return bytes;
    }

    /// <summary>Reads what <see cref="ToBytes"/> wrote, as a complete waveform.</summary>
    public static AudioPeaks FromBytes(ReadOnlySpan<byte> bytes)
    {
        int windows = bytes.Length / 2;
        sbyte[] minimum = new sbyte[windows];
        sbyte[] maximum = new sbyte[windows];

        for (int window = 0; window < windows; window++)
        {
            minimum[window] = unchecked((sbyte)bytes[window * 2]);
            maximum[window] = unchecked((sbyte)bytes[(window * 2) + 1]);
        }

        return new AudioPeaks(minimum, maximum, windows, isComplete: true);
    }

    /// <summary>A sample, -1 to 1, as stored.</summary>
    public static sbyte Quantise(float sample) =>
        (sbyte)Math.Clamp((int)MathF.Round(sample * 127.0f), -127, 127);

    private static void Scan(sbyte[] lows, sbyte[] highs, long from, long to, ref int low, ref int high)
    {
        for (long index = from; index < to; index++)
        {
            if (lows[index] < low)
            {
                low = lows[index];
            }

            if (highs[index] > high)
            {
                high = highs[index];
            }
        }
    }

    private (sbyte[][] Lows, sbyte[][] Highs) Pyramid()
    {
        lock (_gate)
        {
            if (_pyramidMinimum is not null)
            {
                return (_pyramidMinimum, _pyramidMaximum!);
            }

            var lows = new sbyte[Levels][];
            var highs = new sbyte[Levels][];
            sbyte[] belowLow = _minimum;
            sbyte[] belowHigh = _maximum;
            long belowCount = Windows;

            for (int level = 0; level < Levels; level++)
            {
                long count = (belowCount + Fan - 1) / Fan;
                lows[level] = new sbyte[count];
                highs[level] = new sbyte[count];

                for (long block = 0; block < count; block++)
                {
                    int low = sbyte.MaxValue;
                    int high = sbyte.MinValue;
                    Scan(belowLow, belowHigh, block * Fan, Math.Min((block + 1) * Fan, belowCount), ref low, ref high);
                    lows[level][block] = (sbyte)low;
                    highs[level][block] = (sbyte)high;
                }

                belowLow = lows[level];
                belowHigh = highs[level];
                belowCount = count;
            }

            _pyramidMaximum = highs;
            _pyramidMinimum = lows;
            return (lows, highs);
        }
    }
}
