using FFmpeg.AutoGen;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Filters;

/// <summary>Building libavfilter graphs filter by filter, with options set directly.</summary>
/// <remarks>
/// Setting each option on its filter, rather than parsing a graph description, means a path needs
/// no escaping. Shared by the analyses that read a whole file through a <c>movie</c> source
/// (<see cref="MotionAnalyzer"/>, <see cref="Analysis.SceneDetector"/>).
/// </remarks>
internal static unsafe class FilterGraphs
{
    /// <summary>Makes one filter in the graph with its options set.</summary>
    public static AVFilterContext* Filter(AVFilterGraph* graph, string name, params (string Key, string Value)[] options)
    {
        AVFilter* type = ffmpeg.avfilter_get_by_name(name);
        if (type is null)
        {
            throw new FfmpegException($"This FFmpeg build has no {name} filter.");
        }

        AVFilterContext* context = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc_filter(graph, type, name), $"avfilter_graph_alloc_filter ({name})");
        foreach ((string key, string value) in options)
        {
            Av.Check(ffmpeg.av_opt_set(context, key, value, ffmpeg.AV_OPT_SEARCH_CHILDREN), $"av_opt_set ({name} {key})", value);
        }

        Av.Check(ffmpeg.avfilter_init_str(context, null), $"avfilter_init_str ({name})");
        return context;
    }
}
