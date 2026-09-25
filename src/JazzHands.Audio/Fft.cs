namespace JazzHands.Audio;

/// <summary>
/// A complex fast Fourier transform of one power-of-two size, in place, in double precision.
/// </summary>
/// <remarks>
/// Iterative radix 2 with the twiddles and the bit reversal worked out once, when it is made, so
/// running it allocates nothing and it can be kept by an effect on the audio thread. The forward
/// transform is unscaled; the inverse divides by the size, so a round trip gives back what went in.
/// </remarks>
public sealed class Fft
{
    private readonly double[] _cos;
    private readonly double[] _sin;
    private readonly int[] _reversed;

    /// <summary>Makes a transform of a size.</summary>
    /// <param name="size">A power of two, 2 or more.</param>
    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size is a power of two.");
        }

        Size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int index = 0; index < size / 2; index++)
        {
            double angle = -2.0 * Math.PI * index / size;
            _cos[index] = Math.Cos(angle);
            _sin[index] = Math.Sin(angle);
        }

        int bits = System.Numerics.BitOperations.Log2((uint)size);
        _reversed = new int[size];
        for (int index = 0; index < size; index++)
        {
            int reversed = 0;
            for (int bit = 0; bit < bits; bit++)
            {
                reversed |= ((index >> bit) & 1) << (bits - 1 - bit);
            }

            _reversed[index] = reversed;
        }
    }

    /// <summary>How many points it transforms.</summary>
    public int Size { get; }

    /// <summary>The smallest power of two at least as large as a count.</summary>
    public static int SizeFor(long count) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, count));

    /// <summary>Transforms real and imaginary parts in place, time to frequency.</summary>
    public void Forward(Span<double> real, Span<double> imaginary) => Run(real, imaginary, inverse: false);

    /// <summary>Transforms in place, frequency to time, divided by the size.</summary>
    public void Inverse(Span<double> real, Span<double> imaginary)
    {
        Run(real, imaginary, inverse: true);
        double scale = 1.0 / Size;
        for (int index = 0; index < Size; index++)
        {
            real[index] *= scale;
            imaginary[index] *= scale;
        }
    }

    private void Run(Span<double> real, Span<double> imaginary, bool inverse)
    {
        if (real.Length < Size || imaginary.Length < Size)
        {
            throw new ArgumentException($"Both parts hold at least {Size} points.");
        }

        for (int index = 0; index < Size; index++)
        {
            int other = _reversed[index];
            if (other > index)
            {
                (real[index], real[other]) = (real[other], real[index]);
                (imaginary[index], imaginary[other]) = (imaginary[other], imaginary[index]);
            }
        }

        double sign = inverse ? -1.0 : 1.0;
        for (int length = 2; length <= Size; length <<= 1)
        {
            int half = length / 2;
            int step = Size / length;
            for (int start = 0; start < Size; start += length)
            {
                for (int offset = 0; offset < half; offset++)
                {
                    double cos = _cos[offset * step];
                    double sin = sign * _sin[offset * step];
                    int a = start + offset;
                    int b = a + half;
                    double re = (real[b] * cos) - (imaginary[b] * sin);
                    double im = (real[b] * sin) + (imaginary[b] * cos);
                    real[b] = real[a] - re;
                    imaginary[b] = imaginary[a] - im;
                    real[a] += re;
                    imaginary[a] += im;
                }
            }
        }
    }
}
