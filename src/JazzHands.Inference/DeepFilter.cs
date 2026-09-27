using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace JazzHands.Inference;

/// <summary>
/// Speech enhancement with DeepFilterNet 3 (Phase 43): noise taken out of speech at 48 kHz, whole
/// recordings at a time, on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The published model is three networks (an encoder, an ERB gain decoder and a deep filter
/// decoder) that take the whole sequence of features and keep their recurrent state inside, so
/// the signal processing around them is ours, as libDF does it: a 960 sample Vorbis window every
/// 480 samples, the spectrum scaled by 1/960; 32 ERB band powers in dB, and the first 96 bins
/// complex, each normalised by an exponential mean (alpha 0.99); features moved two frames earlier
/// (the convolutions' lookahead); gains per ERB band over the whole spectrum, and over the first
/// 96 bins a five tap complex filter across frames t-2 to t+2; overlap-add, and the 480 sample
/// delay taken off.
/// </para>
/// <para>
/// Long recordings go through in pieces of <see cref="PieceFrames"/> frames with a second of the
/// frames before each heard first and thrown away, so the recurrent state has settled where the
/// piece begins; the normalisations run over the whole recording as they would in one pass.
/// </para>
/// </remarks>
public sealed class DeepFilter : IDisposable
{
    /// <summary>The rate it works at.</summary>
    public const int SampleRate = 48000;

    private const int FftSize = 960;
    private const int Hop = 480;
    private const int Bins = (FftSize / 2) + 1;
    private const int ErbBands = 32;
    private const int DfBins = 96;
    private const int DfOrder = 5;
    private const int DfLookahead = 2;
    private const int ConvLookahead = 2;
    private const float Alpha = 0.99f;

    /// <summary>Frames heard in one go: thirty seconds.</summary>
    internal const int PieceFrames = 3000;

    /// <summary>Frames heard before a piece and thrown away: a second.</summary>
    internal const int ContextFrames = 100;

    private readonly NeuralModel _encoder;
    private readonly NeuralModel _erbDecoder;
    private readonly NeuralModel _dfDecoder;
    private readonly int[] _erbWidths = ErbWidths();
    private readonly float[] _window = VorbisWindow();

    private DeepFilter(NeuralModel encoder, NeuralModel erbDecoder, NeuralModel dfDecoder)
    {
        _encoder = encoder;
        _erbDecoder = erbDecoder;
        _dfDecoder = dfDecoder;
    }

    /// <summary>For tests: leave the networks out, so the signal path alone must give the input back.</summary>
    internal bool PassThrough { get; set; }

    /// <summary>Where the networks run.</summary>
    public ExecutionProvider Provider => _encoder.Provider;

    /// <summary>Loads the three networks from a folder holding enc.onnx, erb_dec.onnx and df_dec.onnx.</summary>
    public static DeepFilter Load(string folder, bool gpu = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string Find(string name) => Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"There is no {name} in {folder}.", Path.Combine(folder, name));
        return new DeepFilter(NeuralModel.Load(Find("enc.onnx"), gpu), NeuralModel.Load(Find("erb_dec.onnx"), gpu), NeuralModel.Load(Find("df_dec.onnx"), gpu));
    }

    /// <summary>
    /// Loads the networks from the published archive (DeepFilterNet3_onnx.tar.gz), unpacking it
    /// beside itself the first time.
    /// </summary>
    public static DeepFilter LoadArchive(string archive, bool gpu = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        string folder = Path.Combine(Path.GetDirectoryName(archive)!, Path.GetFileName(archive).Split('.')[0]);
        if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "enc.onnx", SearchOption.AllDirectories).Any())
        {
            string part = folder + ".part";
            if (Directory.Exists(part))
            {
                Directory.Delete(part, recursive: true);
            }

            Directory.CreateDirectory(part);
            using (var file = File.OpenRead(archive))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            {
                System.Formats.Tar.TarFile.ExtractToDirectory(gzip, part, overwriteFiles: true);
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(part, folder);
        }

        return Load(folder, gpu);
    }

    /// <summary>A recording, 48 kHz mono, with the noise taken out of its speech; the same length.</summary>
    public float[] Enhance(ReadOnlySpan<float> samples, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        int length = samples.Length;
        int frames = (length + FftSize + Hop - 1) / Hop;
        var padded = new float[frames * Hop];
        samples.CopyTo(padded);

        // Analysis: every frame's spectrum, scaled as libDF scales it.
        var spectrum = new Complex[frames][];
        var fft = new MixedRadixFft(FftSize);
        var buffer = new Complex[FftSize];
        var memory = new float[FftSize - Hop];
        const float Norm = 2f * Hop / (FftSize * (float)FftSize);
        for (int t = 0; t < frames; t++)
        {
            for (int i = 0; i < FftSize - Hop; i++)
            {
                buffer[i] = memory[i] * _window[i];
            }

            for (int i = 0; i < Hop; i++)
            {
                float sample = padded[(t * Hop) + i];
                buffer[FftSize - Hop + i] = sample * _window[FftSize - Hop + i];
                memory[i] = sample;
            }

            fft.Forward(buffer);
            var bins = new Complex[Bins];
            for (int k = 0; k < Bins; k++)
            {
                bins[k] = buffer[k] * Norm;
            }

            spectrum[t] = bins;
        }

        // Features, normalised by their running means, then moved two frames earlier.
        float[] erbFeatures = new float[frames * ErbBands];
        float[] specFeatures = new float[frames * DfBins * 2];
        float[] erbState = [.. Enumerable.Range(0, ErbBands).Select(b => -60f + (-30f * b / (ErbBands - 1)))];
        float[] specState = [.. Enumerable.Range(0, DfBins).Select(f => 0.001f + (-0.0009f * f / (DfBins - 1)))];
        for (int t = 0; t < frames; t++)
        {
            int target = t - ConvLookahead;
            int bin = 0;
            for (int b = 0; b < ErbBands; b++)
            {
                double power = 0;
                for (int i = 0; i < _erbWidths[b]; i++, bin++)
                {
                    power += Norm2(spectrum[t][bin]);
                }

                float db = (float)(10 * Math.Log10((power / _erbWidths[b]) + 1e-10));
                erbState[b] = (db * (1 - Alpha)) + (erbState[b] * Alpha);
                if (target >= 0)
                {
                    erbFeatures[(target * ErbBands) + b] = (db - erbState[b]) / 40f;
                }
            }

            for (int f = 0; f < DfBins; f++)
            {
                Complex x = spectrum[t][f];
                specState[f] = ((float)x.Magnitude * (1 - Alpha)) + (specState[f] * Alpha);
                if (target >= 0)
                {
                    float scale = 1f / MathF.Sqrt(specState[f]);
                    specFeatures[(((0 * frames) + target) * DfBins) + f] = (float)x.Real * scale;
                    specFeatures[(((1 * frames) + target) * DfBins) + f] = (float)x.Imaginary * scale;
                }
            }
        }

        // The networks, a piece at a time: ERB gains and deep filter coefficients for every frame.
        float[] gains = new float[frames * ErbBands];
        float[] coefficients = new float[frames * DfBins * DfOrder * 2];
        if (PassThrough)
        {
            // For tests of the signal path: unit gains and a filter that passes frame t alone.
            Array.Fill(gains, 1f);
            for (int at = (DfOrder - 1 - DfLookahead) * 2; at < coefficients.Length; at += DfOrder * 2)
            {
                coefficients[at] = 1f;
            }
        }

        for (int start = 0; start < frames && !PassThrough; start += PieceFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int from = Math.Max(0, start - ContextFrames);
            int end = Math.Min(frames, start + PieceFrames);
            Run(erbFeatures, specFeatures, frames, from, end, start, gains, coefficients);
            progress?.Report(end / (double)frames);
        }

        // Gains over every bin, the deep filter over the first 96, and synthesis.
        var output = new float[frames * Hop];
        var synthesis = new float[FftSize - Hop];
        for (int t = 0; t < frames; t++)
        {
            int bin = 0;
            for (int b = 0; b < ErbBands; b++)
            {
                float gain = gains[(t * ErbBands) + b];
                for (int i = 0; i < _erbWidths[b]; i++, bin++)
                {
                    buffer[bin] = spectrum[t][bin] * gain;
                }
            }

            for (int f = 0; f < DfBins; f++)
            {
                Complex sum = Complex.Zero;
                for (int tap = 0; tap < DfOrder; tap++)
                {
                    int source = t - (DfOrder - 1 - DfLookahead) + tap;
                    if (source < 0 || source >= frames)
                    {
                        continue;
                    }

                    int at = (((t * DfBins) + f) * DfOrder * 2) + (tap * 2);
                    sum += spectrum[source][f] * new Complex(coefficients[at], coefficients[at + 1]);
                }

                buffer[f] = sum;
            }

            // The other half of the spectrum mirrors the first, so the inverse is real.
            for (int k = 1; k < FftSize - Bins + 1; k++)
            {
                buffer[FftSize - k] = Complex.Conjugate(buffer[k]);
            }

            fft.Inverse(buffer);
            for (int i = 0; i < Hop; i++)
            {
                output[(t * Hop) + i] = ((float)buffer[i].Real * _window[i]) + synthesis[i];
            }

            for (int i = 0; i < FftSize - Hop; i++)
            {
                synthesis[i] = (float)buffer[Hop + i].Real * _window[Hop + i];
            }
        }

        // The analysis and synthesis delay one hop's worth: the enhanced sound starts FftSize - Hop late.
        return output.AsSpan(FftSize - Hop, length).ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _encoder.Dispose();
        _erbDecoder.Dispose();
        _dfDecoder.Dispose();
    }

    /// <summary>The ERB bands' widths in bins, as libDF's erb_fb makes them for 48 kHz and a 960 FFT.</summary>
    internal static int[] ErbWidths()
    {
        static double FreqToErb(double f) => 9.265 * Math.Log(1 + (f / (24.7 * 9.265)));
        static double ErbToFreq(double e) => 24.7 * 9.265 * (Math.Exp(e / 9.265) - 1);
        double width = SampleRate / (double)FftSize;
        double low = FreqToErb(0);
        double step = (FreqToErb(SampleRate / 2.0) - low) / ErbBands;
        var widths = new int[ErbBands];
        int previous = 0;
        int over = 0;
        for (int i = 1; i <= ErbBands; i++)
        {
            int edge = (int)Math.Round(ErbToFreq(low + (i * step)) / width);
            int count = edge - previous - over;
            if (count < 2)
            {
                over = 2 - count;
                count = 2;
            }
            else
            {
                over = 0;
            }

            widths[i - 1] = count;
            previous = edge;
        }

        widths[^1] += 1;
        int tooMany = widths.Sum() - Bins;
        if (tooMany > 0)
        {
            widths[^1] -= tooMany;
        }

        return widths;
    }

    private static double Norm2(Complex value) => (value.Real * value.Real) + (value.Imaginary * value.Imaginary);

    private static float[] VorbisWindow() =>
        [.. Enumerable.Range(0, FftSize).Select(i =>
        {
            double s = Math.Sin(Math.PI * (i + 0.5) / FftSize);
            return (float)Math.Sin(Math.PI / 2 * s * s);
        })];

    /// <summary>
    /// Runs the networks over frames <paramref name="from"/> to <paramref name="end"/> and keeps what
    /// they said from <paramref name="keepFrom"/> on.
    /// </summary>
    private void Run(float[] erb, float[] spec, int frames, int from, int end, int keepFrom, float[] gains, float[] coefficients)
    {
        int count = end - from;
        var featErb = new DenseTensor<float>([1, 1, count, ErbBands]);
        erb.AsSpan(from * ErbBands, count * ErbBands).CopyTo(featErb.Buffer.Span);
        var featSpec = new DenseTensor<float>([1, 2, count, DfBins]);
        for (int channel = 0; channel < 2; channel++)
        {
            spec.AsSpan(((channel * frames) + from) * DfBins, count * DfBins).CopyTo(featSpec.Buffer.Span[(channel * count * DfBins)..]);
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encoded = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("feat_erb", featErb),
            NamedOnnxValue.CreateFromTensor("feat_spec", featSpec),
        ]);
        DenseTensor<float> Out(string name) => encoded.First(value => value.Name == name).AsTensor<float>().ToDenseTensor();
        DenseTensor<float> embedding = Out("emb");
        DenseTensor<float> c0 = Out("c0");

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> mask = _erbDecoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("emb", embedding),
            NamedOnnxValue.CreateFromTensor("e3", Out("e3")),
            NamedOnnxValue.CreateFromTensor("e2", Out("e2")),
            NamedOnnxValue.CreateFromTensor("e1", Out("e1")),
            NamedOnnxValue.CreateFromTensor("e0", Out("e0")),
        ]);
        float[] m = [.. mask.First(value => value.Name == "m").AsTensor<float>()];

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> filter = _dfDecoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("emb", embedding),
            NamedOnnxValue.CreateFromTensor("c0", c0),
        ]);
        float[] coefs = [.. filter.First(value => value.Name == "coefs").AsTensor<float>()];

        int skip = keepFrom - from;
        m.AsSpan(skip * ErbBands, (count - skip) * ErbBands).CopyTo(gains.AsSpan(keepFrom * ErbBands));
        int perFrame = DfBins * DfOrder * 2;
        coefs.AsSpan(skip * perFrame, (count - skip) * perFrame).CopyTo(coefficients.AsSpan(keepFrom * perFrame));
    }

    /// <summary>A complex FFT for any size that factors into small primes, by recursive Cooley-Tukey.</summary>
    private sealed class MixedRadixFft
    {
        private readonly int _size;
        private readonly Complex[] _twiddles;
        private readonly Complex[] _scratch;

        public MixedRadixFft(int size)
        {
            _size = size;
            _twiddles = [.. Enumerable.Range(0, size).Select(k => Complex.FromPolarCoordinates(1, -2 * Math.PI * k / size))];
            _scratch = new Complex[size];
        }

        /// <summary>The forward transform in place.</summary>
        public void Forward(Complex[] data)
        {
            Transform(data, 0, 1, _size, _scratch, 0);
            Array.Copy(_scratch, data, _size);
        }

        /// <summary>The inverse in place, not divided by the size.</summary>
        public void Inverse(Complex[] data)
        {
            for (int i = 0; i < _size; i++)
            {
                data[i] = Complex.Conjugate(data[i]);
            }

            Forward(data);
            for (int i = 0; i < _size; i++)
            {
                data[i] = Complex.Conjugate(data[i]);
            }
        }

        // data[offset + stride * n] for n below size, transformed into output[at..at + size].
        private void Transform(Complex[] data, int offset, int stride, int size, Complex[] output, int at)
        {
            if (size == 1)
            {
                output[at] = data[offset];
                return;
            }

            int p = Factor(size);
            int m = size / p;
            for (int r = 0; r < p; r++)
            {
                Transform(data, offset + (r * stride), stride * p, m, output, at + (r * m));
            }

            Span<Complex> sums = stackalloc Complex[5];
            Span<Complex> parts = stackalloc Complex[5];
            int step = _size / size;
            for (int k = 0; k < m; k++)
            {
                for (int r = 0; r < p; r++)
                {
                    parts[r] = output[at + (r * m) + k] * _twiddles[r * k * step % _size];
                }

                for (int q = 0; q < p; q++)
                {
                    Complex sum = Complex.Zero;
                    for (int r = 0; r < p; r++)
                    {
                        sum += parts[r] * _twiddles[r * q * m * step % _size];
                    }

                    sums[q] = sum;
                }

                for (int q = 0; q < p; q++)
                {
                    output[at + k + (q * m)] = sums[q];
                }
            }
        }

        private static int Factor(int size) => size % 2 == 0 ? 2 : size % 3 == 0 ? 3 : size % 5 == 0 ? 5 : throw new ArgumentException($"{size} has a factor above 5.", nameof(size));
    }
}
