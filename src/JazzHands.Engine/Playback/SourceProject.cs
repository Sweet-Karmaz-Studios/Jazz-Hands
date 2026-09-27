using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Playback;

/// <summary>
/// What the source monitor's player plays: the project with one sequence in it, the whole of one
/// media item laid out at its own source times, so the player's timeline time is the file's time.
/// </summary>
/// <remarks>
/// Built for the viewer and never put in the session's project: a <see cref="PlaybackEngine"/>
/// plays a project's active sequence, and this is the smallest project that plays one file through
/// the same decode, cache, compositor and mixer as the program preview. The picture goes on V1 and
/// each sound stream on a track of its own, all at unity, at the file's frame rate and size; the
/// sound format stays the project's, which is what the player's transport opens with.
/// </remarks>
public static class SourceProject
{
    /// <summary>The id of the one sequence.</summary>
    public const string SequenceId = "source-monitor";

    /// <summary>The project playing just this item.</summary>
    public static Project For(Project project, MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        MediaStream? video = item.Info?.VideoStreams.FirstOrDefault();
        ProjectSettings settings = project.Settings with
        {
            FrameRate = video?.FrameRate is { IsZero: false } rate ? rate : project.Settings.FrameRate,
            Width = video is { Width: > 0 } ? video.Width : project.Settings.Width,
            Height = video is { Height: > 0 } ? video.Height : project.Settings.Height,
        };

        var range = new TimeRange(Flicks.Zero, item.Duration);
        var tracks = new List<Track>();
        if (video is not null || item.IsImages)
        {
            tracks.Add(new Track("source-v1", TrackKind.Video, "V1", 0)
                .AddClip(new Clip("source-picture", range, Flicks.Zero, MediaId: item.Id, SourceStreamIndex: video?.Index ?? 0)));
        }

        int order = 1;
        foreach (MediaStream stream in item.Info?.AudioStreams ?? [])
        {
            tracks.Add(new Track($"source-a{order}", TrackKind.Audio, $"A{order}", order)
                .AddClip(new Clip($"source-sound-{order}", range, Flicks.Zero, MediaId: item.Id, SourceStreamIndex: stream.Index)));
            order++;
        }

        var sequence = new Sequence(SequenceId, item.Name, [.. tracks], Settings: settings);
        return project with { Sequences = [sequence], ActiveSequenceId = SequenceId };
    }
}
