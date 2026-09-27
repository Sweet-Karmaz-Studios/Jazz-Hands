using System.Buffers.Binary;
using FFmpeg.AutoGen;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Analysis;

/// <summary>Where one shot ends and the next begins.</summary>
/// <param name="Time">The source time of the new shot's first frame, or of a dissolve's middle frame.</param>
/// <param name="Score">How much changed, 0 to 100: the percentage of the picture's range.</param>
/// <param name="Kind">A cut or a dissolve.</param>
public sealed record SceneCut(Flicks Time, double Score, SceneCutKind Kind);

/// <summary>
/// What <see cref="SceneDetector"/> measured at every frame of a video stream, from which the cuts
/// at any threshold are found again without reading the file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Change"/> is FFmpeg's <c>scdet</c> score: the mean absolute difference from the
/// previous frame as a percentage of the range (mafd), and the score is the smaller of that and
/// how far it jumped from the previous frame's. A cut changes everything at once, so both are
/// high; a pan or a flicker changes a lot every frame, so the jump is small.
/// </para>
/// <para>
/// A dissolve changes a little every frame and scores nothing there, so <see cref="Blend"/> looks
/// across <see cref="HalfWindow"/> frames either side instead: how different the two ends are
/// (<c>s</c>), less how far the middle frame is from their average (<c>2m</c>). In a dissolve the
/// middle frame is the mix of its neighbours, so the whole difference counts, peaking at the
/// dissolve's middle; across a cut the middle frame is one end or the other, and motion puts it
/// nowhere in particular, so the two cancel.
/// </para>
/// </remarks>
public sealed class SceneMeasurements
{
    /// <summary>The cache kind, which names the format and the way it was measured.</summary>
    public const string CacheKind = "scenes1";

    private const int Magic = 0x314E4353; // "SCN1"

    /// <summary>Makes a set of measurements.</summary>
    public SceneMeasurements(Flicks[] times, float[] change, float[] blend, int halfWindow)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(blend);
        if (change.Length != times.Length || blend.Length != times.Length)
        {
            throw new ArgumentException("There must be one change and one blend per frame.", nameof(change));
        }

        Times = times;
        Change = change;
        Blend = blend;
        HalfWindow = halfWindow;
    }

    /// <summary>Each frame's source time, in order.</summary>
    public IReadOnlyList<Flicks> Times { get; }

    /// <summary>Each frame's cut score (scdet's), 0 to 100; zero for the first.</summary>
    public IReadOnlyList<float> Change { get; }

    /// <summary>Each frame's dissolve score, 0 to 100; zero within <see cref="HalfWindow"/> of either end.</summary>
    public IReadOnlyList<float> Blend { get; }

    /// <summary>How many frames either side the dissolve score looks.</summary>
    public int HalfWindow { get; }

    /// <summary>How many frames were measured.</summary>
    public int Count => Times.Count;

    /// <summary>
    /// The cuts and dissolves scoring over a threshold, in time order, with none closer to the one
    /// before than <paramref name="minShot"/> (the higher score wins).
    /// </summary>
    /// <param name="threshold">The score a change needs, 0 to 100; scdet's default is 10.</param>
    /// <param name="minShot">The shortest shot there can be.</param>
    public IReadOnlyList<SceneCut> Cuts(double threshold, Flicks minShot)
    {
        var found = new List<SceneCut>();

        for (int index = 1; index < Count; index++)
        {
            if (Change[index] > threshold)
            {
                found.Add(new SceneCut(Times[index], Change[index], SceneCutKind.Cut));
            }
        }

        // A dissolve is a run of frames over the threshold; it is placed at the run's weighted
        // middle, which is the peak of the triangle a short one makes and the middle of the
        // plateau a long one does.
        for (int index = 0; index < Count; index++)
        {
            if (Blend[index] <= threshold)
            {
                continue;
            }

            int start = index;
            double weight = 0;
            double moment = 0;
            float peak = 0;
            while (index < Count && Blend[index] > threshold)
            {
                double over = Blend[index] - threshold;
                weight += over;
                moment += over * index;
                peak = Math.Max(peak, Blend[index]);
                index++;
            }

            int middle = Math.Clamp((int)Math.Round(moment / weight), start, index - 1);
            found.Add(new SceneCut(Times[middle], peak, SceneCutKind.Dissolve));
        }

        found.Sort((a, b) => a.Time.CompareTo(b.Time));

        var kept = new List<SceneCut>(found.Count);
        foreach (SceneCut cut in found)
        {
            Flicks previous = kept.Count > 0 ? kept[^1].Time : Times.Count > 0 ? Times[0] : Flicks.Zero;
            if (cut.Time - previous >= minShot)
            {
                kept.Add(cut);
            }
            else if (kept.Count > 0 && cut.Score > kept[^1].Score)
            {
                kept[^1] = cut;
            }
        }

        return kept;
    }

    /// <summary>The measurements as the bytes the cache keeps.</summary>
    public byte[] ToBytes()
    {
        const int Header = 12;
        byte[] bytes = new byte[Header + (Count * 16)];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], HalfWindow);

        for (int index = 0; index < Count; index++)
        {
            Span<byte> frame = span.Slice(Header + (index * 16), 16);
            BinaryPrimitives.WriteInt64LittleEndian(frame, Times[index].Value);
            BinaryPrimitives.WriteSingleLittleEndian(frame[8..], Change[index]);
            BinaryPrimitives.WriteSingleLittleEndian(frame[12..], Blend[index]);
        }

        return bytes;
    }

    /// <summary>Reads what <see cref="ToBytes"/> wrote; null when it is not that.</summary>
    public static SceneMeasurements? FromBytes(ReadOnlySpan<byte> bytes)
    {
        const int Header = 12;
        if (bytes.Length < Header || BinaryPrimitives.ReadInt32LittleEndian(bytes) != Magic)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int halfWindow = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (count < 0 || bytes.Length != Header + ((long)count * 16))
        {
            return null;
        }

        var times = new Flicks[count];
        var change = new float[count];
        var blend = new float[count];
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> frame = bytes.Slice(Header + (index * 16), 16);
            times[index] = new Flicks(BinaryPrimitives.ReadInt64LittleEndian(frame));
            change[index] = BinaryPrimitives.ReadSingleLittleEndian(frame[8..]);
            blend[index] = BinaryPrimitives.ReadSingleLittleEndian(frame[12..]);
        }

        return new SceneMeasurements(times, change, blend, halfWindow);
    }
}

/// <summary>
/// Finds where the shots change in an already edited video: reads every frame once, small, and
/// measures each against the one before and against its neighbours.
/// </summary>
/// <remarks>
/// <para>
/// One libavfilter graph, as <see cref="Filters.MotionAnalyzer"/> does it: <c>movie</c> reading
/// the file with the decoder's own threads, <c>scale</c> down to <see cref="Width"/> by
/// <see cref="Height"/> with area averaging (which also averages away grain and compression
/// noise, and keeps a mix a mix), and <c>format</c> to eight bit 4:2:0, pulled through a sink. The
/// scoring is FFmpeg's <c>scdet</c> formula, worked out here on the frames already in hand rather
/// than read back out of each frame's metadata as text, because the dissolve score needs the
/// frames anyway (see <see cref="SceneMeasurements"/>).
/// </para>
/// <para>
/// Decoding is in software. Hardware decode is faster, but the frames would have to come back
/// from the GPU to be looked at, and on the reference machine that costs more than it saves
/// (10 minutes of 1080p60 H.264: 23 s in software against 57 s through D3D11VA and a download).
/// </para>
/// </remarks>
public static unsafe class SceneDetector
{
    /// <summary>How wide the frames are measured.</summary>
    public const int Width = 160;

    /// <summary>How tall the frames are measured.</summary>
    public const int Height = 90;

    /// <summary>How far either side the dissolve score looks, in seconds.</summary>
    public const double HalfWindowSeconds = 0.5;

    private static readonly ILogger Logger = Log.ForContext(typeof(SceneDetector));

    /// <summary>Measures one video stream of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="streamIndex">The video stream, by its index in the file.</param>
    /// <param name="expectedFrames">About how many frames there are, for progress; zero when unknown.</param>
    /// <param name="progress">Told the fraction done, 0 to 1.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public static SceneMeasurements Measure(
        string path,
        int streamIndex,
        long expectedFrames = 0,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        AVFilterGraph* graph = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc(), "avfilter_graph_alloc");
        AVFrame* frame = Av.CheckAlloc(ffmpeg.av_frame_alloc(), "av_frame_alloc");

        try
        {
            AVFilterContext* movie = Filters.FilterGraphs.Filter(graph, "movie", ("filename", path), ("si", streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            AVFilterContext* scale = Filters.FilterGraphs.Filter(graph, "scale", ("w", Width.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("h", Height.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("flags", "area"));
            AVFilterContext* format = Filters.FilterGraphs.Filter(graph, "format", ("pix_fmts", "yuv420p"));
            AVFilterContext* sink = Filters.FilterGraphs.Filter(graph, "buffersink");

            Av.Check(ffmpeg.avfilter_link(movie, 0, scale, 0), "avfilter_link (movie)");
            Av.Check(ffmpeg.avfilter_link(scale, 0, format, 0), "avfilter_link (scale)");
            Av.Check(ffmpeg.avfilter_link(format, 0, sink, 0), "avfilter_link (format)");
            Av.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config", path);

            AVRational timeBase = ffmpeg.av_buffersink_get_time_base(sink);
            AVRational rate = ffmpeg.av_buffersink_get_frame_rate(sink);
            double fps = rate.num > 0 && rate.den > 0 ? (double)rate.num / rate.den : 30;
            int halfWindow = Math.Max(1, (int)Math.Round(fps * HalfWindowSeconds));

            var measure = new Measurer(halfWindow, (int)Math.Clamp(expectedFrames + 16, 16, 1 << 24));
            long frames = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int result = ffmpeg.av_buffersink_get_frame(sink, frame);
                if (result == Av.EndOfFile || result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                Av.Check(result, "av_buffersink_get_frame", path);

                long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                Flicks time = pts == ffmpeg.AV_NOPTS_VALUE
                    ? Flicks.FromSeconds(frames / fps)
                    : Flicks.FromTimebase(pts, timeBase.num, timeBase.den);
                measure.Add(time, frame);
                ffmpeg.av_frame_unref(frame);

                frames++;
                if (expectedFrames > 0 && frames % 60 == 0)
                {
                    progress?.Report(Math.Min(0.99, (double)frames / expectedFrames));
                }
            }

            SceneMeasurements measured = measure.Finish();
            Logger.Information("Measured {Frames} frames of {Path} for scene cuts", frames, path);
            progress?.Report(1.0);
            return measured;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avfilter_graph_free(&graph);
        }
    }

    /// <summary>Works the two scores out frame by frame, keeping the last few frames in a ring.</summary>
    internal sealed class Measurer
    {
        private const int Pixels = (Width * Height) + (2 * (Width / 2) * (Height / 2));
        private readonly int _half;
        private readonly byte[][] _ring;
        private readonly List<Flicks> _times;
        private readonly List<float> _change;
        private readonly List<float> _blend;
        private double _previousMafd;

        public Measurer(int halfWindow, int capacity)
        {
            _half = halfWindow;
            _ring = new byte[(2 * halfWindow) + 1][];
            for (int slot = 0; slot < _ring.Length; slot++)
            {
                _ring[slot] = new byte[Pixels];
            }

            _times = new List<Flicks>(capacity);
            _change = new List<float>(capacity);
            _blend = new List<float>(capacity);
        }

        /// <summary>Adds the next frame, <see cref="Width"/> by <see cref="Height"/> in 4:2:0: all three planes count, as in scdet, so a cut between two pictures of the same brightness is still seen.</summary>
        public unsafe void Add(Flicks time, AVFrame* frame)
        {
            int index = _times.Count;
            byte[] current = _ring[index % _ring.Length];
            int at = 0;
            for (uint plane = 0; plane < 3; plane++)
            {
                int width = plane == 0 ? Width : Width / 2;
                int height = plane == 0 ? Height : Height / 2;
                int stride = frame->linesize[plane];
                var source = new ReadOnlySpan<byte>(frame->data[plane], stride * height);
                for (int row = 0; row < height; row++, at += width)
                {
                    source.Slice(row * stride, width).CopyTo(current.AsSpan(at));
                }
            }

            Add(time, current, index);
        }

        /// <summary>Adds the next frame, already in its ring slot as the three planes end to end.</summary>
        internal void Add(Flicks time, byte[] current, int index)
        {

            _times.Add(time);
            _blend.Add(0);

            if (index == 0)
            {
                _change.Add(0);
            }
            else
            {
                // scdet: mafd is the sum of absolute differences over the pixels, as a percentage
                // of an eight bit range, and the score the smaller of it and its jump.
                double mafd = Sad(current, _ring[(index - 1) % _ring.Length]) * 100.0 / Pixels / 256.0;
                double score = Math.Clamp(Math.Min(mafd, Math.Abs(mafd - _previousMafd)), 0, 100);
                _previousMafd = mafd;
                _change.Add((float)score);
            }

            if (index >= 2 * _half)
            {
                int middle = index - _half;
                byte[] first = _ring[(index - (2 * _half)) % _ring.Length];
                byte[] centre = _ring[middle % _ring.Length];
                double span = Sad(current, first);
                double offMix = OffMix(centre, first, current);
                _blend[middle] = (float)Math.Max(0, (span - offMix) * 100.0 / Pixels / 256.0);
            }
        }

        public SceneMeasurements Finish() => new([.. _times], [.. _change], [.. _blend], _half);

        /// <summary>The sum of absolute differences.</summary>
        private static long Sad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            for (int pixel = 0; pixel < a.Length; pixel++)
            {
                sum += Math.Abs(a[pixel] - b[pixel]);
            }

            return sum;
        }

        /// <summary>Twice the middle's distance from the ends' average, summed: |2m - a - b|.</summary>
        private static long OffMix(ReadOnlySpan<byte> middle, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            long sum = 0;
            for (int pixel = 0; pixel < middle.Length; pixel++)
            {
                sum += Math.Abs((2 * middle[pixel]) - a[pixel] - b[pixel]);
            }

            return sum;
        }
    }
}
