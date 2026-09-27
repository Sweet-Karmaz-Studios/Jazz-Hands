using System.Globalization;
using FFmpeg.AutoGen;
using JazzHands.Core.Stabilization;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Filters;

/// <summary>
/// The first of stabilization's two passes: reads a video through vid.stab's detect filter and
/// returns how the camera moved at every frame.
/// </summary>
/// <remarks>
/// The whole pass is one libavfilter graph, <c>movie</c> reading the file, then <c>format</c>,
/// <c>scale</c> (down to at most <see cref="MaxWidth"/> wide, which is plenty to measure shake and
/// keeps a 4K analysis quick) and <c>vidstabdetect</c> writing its text transforms file, pulled
/// through a sink until the file ends. The filters are made one by one with their options set
/// directly, so a path needs no escaping. The transforms file is read by
/// <see cref="MotionFile"/> and deleted. The second pass is not vid.stab's: the renderer moves
/// each frame itself (see <see cref="CameraMotion"/>), which a filter that counts frames from the
/// start cannot do while scrubbing.
/// </remarks>
public static unsafe class MotionAnalyzer
{
    /// <summary>The widest picture the analysis looks at.</summary>
    public const int MaxWidth = 1920;

    private static readonly ILogger Logger = Log.ForContext(typeof(MotionAnalyzer));

    /// <summary>Analyses one video stream of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="streamIndex">The video stream, by its index in the file.</param>
    /// <param name="expectedFrames">About how many frames there are, for progress; zero when unknown.</param>
    /// <param name="shakiness">vid.stab's shakiness, 1 (little, fast) to 10 (very shaky).</param>
    /// <param name="progress">Told the fraction done, 0 to 1.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <param name="workFolder">Where vid.stab writes its transforms while it works, removed after; the temporary folder when not given.</param>
    public static CameraMotion Analyze(
        string path,
        int streamIndex,
        long expectedFrames = 0,
        int shakiness = 5,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        string? workFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        string transforms = Path.Combine(workFolder ?? Path.GetTempPath(), $"jazz-motion-{Guid.NewGuid():N}.trf");
        AVFilterGraph* graph = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc(), "avfilter_graph_alloc");
        AVFrame* frame = Av.CheckAlloc(ffmpeg.av_frame_alloc(), "av_frame_alloc");

        try
        {
            AVFilterContext* movie = FilterGraphs.Filter(graph, "movie", ("filename", path), ("si", streamIndex.ToString(CultureInfo.InvariantCulture)));
            AVFilterContext* format = FilterGraphs.Filter(graph, "format", ("pix_fmts", "yuv420p"));
            AVFilterContext* scale = FilterGraphs.Filter(graph, "scale", ("w", $"min({MaxWidth},iw)"), ("h", "-2"));
            AVFilterContext* detect = FilterGraphs.Filter(
                graph,
                "vidstabdetect",
                ("result", transforms),
                ("fileformat", "ascii"),
                ("shakiness", Math.Clamp(shakiness, 1, 10).ToString(CultureInfo.InvariantCulture)),
                ("accuracy", "15"));
            AVFilterContext* sink = FilterGraphs.Filter(graph, "buffersink");

            Av.Check(ffmpeg.avfilter_link(movie, 0, format, 0), "avfilter_link (movie)");
            Av.Check(ffmpeg.avfilter_link(format, 0, scale, 0), "avfilter_link (format)");
            Av.Check(ffmpeg.avfilter_link(scale, 0, detect, 0), "avfilter_link (scale)");
            Av.Check(ffmpeg.avfilter_link(detect, 0, sink, 0), "avfilter_link (vidstabdetect)");
            Av.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config", path);

            int width = ffmpeg.av_buffersink_get_w(sink);
            int height = ffmpeg.av_buffersink_get_h(sink);
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
                ffmpeg.av_frame_unref(frame);
                frames++;
                if (expectedFrames > 0 && frames % 10 == 0)
                {
                    progress?.Report(Math.Min(0.99, (double)frames / expectedFrames));
                }
            }

            // vid.stab writes the last of the file when the filter is freed, so the graph goes first.
            ffmpeg.avfilter_graph_free(&graph);
            using var reader = new StreamReader(transforms);
            CameraMotion motion = MotionFile.Read(reader, width, height);
            Logger.Information("Analysed the motion of {Frames} frames of {Path} at {Width}x{Height}", frames, path, width, height);
            progress?.Report(1.0);
            return motion;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            if (graph is not null)
            {
                ffmpeg.avfilter_graph_free(&graph);
            }

            try
            {
                File.Delete(transforms);
            }
            catch (IOException exception)
            {
                Logger.Warning(exception, "Could not delete the transforms file {Path}", transforms);
            }
        }
    }
}
