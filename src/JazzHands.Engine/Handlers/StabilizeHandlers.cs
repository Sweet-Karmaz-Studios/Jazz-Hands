using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Stabilization;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Render.Effects.Stabilize;
using Serilog;

namespace JazzHands.Engine.Handlers;

/// <summary>What the stabilization handlers share: finding the video stream and analysing it.</summary>
internal static class StabilizeHelp
{
    private static readonly ILogger Logger = Log.ForContext(typeof(StabilizeHelp));

    /// <summary>The video stream of a media item a clip or command means.</summary>
    internal static MediaStream VideoStream(MediaItem item, int? index)
    {
        MediaStream? stream = index is { } wanted
            ? item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == wanted && candidate.Kind == MediaStreamKind.Video)
            : item.Info?.VideoStreams.FirstOrDefault();

        return stream ?? throw new CommandException("no-video", $"'{item.Name}' has no video stream{(index is { } at ? FormattableString.Invariant($" {at}") : string.Empty)} to analyse.");
    }

    /// <summary>The analysis of a stream: the one kept, or a new one when there is none or one is asked for.</summary>
    internal static CameraMotion Analysis(MediaItem item, int streamIndex, HandlerContext context, bool again)
    {
        MotionStore store = MotionStore.For(context.ProjectPath);
        if (!again && store.Load(item.Hash, streamIndex) is { } known)
        {
            return known;
        }

        string path = HandlerHelp.Resolve(context, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}, so its motion cannot be analysed. Relink it first.");
        }

        var progress = new Progress<double>(done => Logger.Debug("Analysing the motion of {Media}: {Done:P0}", item.Name, done));
        try
        {
            return store.Analyze(item, path, streamIndex, progress, context.Cancellation);
        }
        catch (Media.Interop.FfmpegException exception)
        {
            throw new CommandException("analysis-failed", $"The motion of '{item.Name}' could not be analysed: {exception.Message}");
        }
    }
}

/// <summary>Steadies a clip: its analysis if needed, then its Stabilize effect.</summary>
/// <remarks>The analysis, minutes for a long recording, is done before the command is queued.</remarks>
public sealed class StabilizeClipHandler : ICommandHandler<StabilizeClipCommand>, IPreparingHandler<StabilizeClipCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, StabilizeClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Anything the handler refuses it refuses itself, in its turn; here only the analysis.
        if (command.Off || project.FindClip(command.ClipId)?.Clip is not { MediaId: { } mediaId } clip
            || project.MediaItem(mediaId) is not { Kind: MediaKind.Movie } item)
        {
            return null;
        }

        MediaStream stream = StabilizeHelp.VideoStream(item, clip.SourceStreamIndex);
        _ = PreparedWork.Reuse(context, (item.Hash, stream.Index, command.Reanalyze), () => StabilizeHelp.Analysis(item, stream.Index, context, command.Reanalyze) is not null);
        return new PreparedWork((item.Hash, stream.Index, command.Reanalyze), true);
    }

    /// <inheritdoc />
    public Project Handle(Project project, StabilizeClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        int existing = clip.Effects.IndexOf(effect => string.Equals(effect.TypeId, StabilizeEffect.TypeId, StringComparison.Ordinal));

        if (command.Off)
        {
            if (existing < 0)
            {
                return project;
            }

            context.Changed(clip.Id);
            return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = clip.Effects.RemoveAt(existing) }));
        }

        if (command.Smoothing is < 0 or > 120)
        {
            throw new CommandException("invalid-value", "Smoothing is 0 to 120 frames either side.", "smoothing");
        }

        if (!double.IsFinite(command.Zoom) || command.Zoom is < 0 or > 100)
        {
            throw new CommandException("invalid-value", "The extra zoom is 0 to 100 percent.", "zoom");
        }

        if (clip.MediaId is not { } mediaId || MediaServices.Require(project, mediaId) is not { Kind: MediaKind.Movie } item)
        {
            throw new CommandException("not-media", "Only a clip of a video file has a camera to steady.");
        }

        MediaStream stream = StabilizeHelp.VideoStream(item, clip.SourceStreamIndex);
        StabilizeHelp.Analysis(item, stream.Index, context, command.Reanalyze);

        Effect effect = (existing >= 0 ? clip.Effects[existing] : Effect.Create(StabilizeEffect.TypeId) with { Enabled = true })
            .WithParameter(StabilizeEffect.Smoothing, AnimatedValue.Constant(new ParamValue.Int(command.Smoothing)))
            .WithParameter(StabilizeEffect.Zoom, AnimatedValue.Constant((float)command.Zoom))
            .WithParameter(StabilizeEffect.AutoZoom, AnimatedValue.Constant(new ParamValue.Bool(!command.NoAutoZoom)));

        EquatableArray<Effect> effects = existing >= 0 ? clip.Effects.SetItem(existing, effect) : clip.Effects.Insert(0, effect);
        context.Changed(clip.Id);
        context.Changed(effect.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = effects }));
    }
}

/// <summary>Analyses a video's camera motion ahead of stabilizing it.</summary>
/// <remarks>The analysis is done before the command is queued, so edits go on meanwhile.</remarks>
public sealed class AnalyzeMotionHandler : ICommandHandler<AnalyzeMotionCommand>, IPreparingHandler<AnalyzeMotionCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, AnalyzeMotionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        MediaStream stream = StabilizeHelp.VideoStream(item, command.Stream);
        StabilizeHelp.Analysis(item, stream.Index, context, again: true);
        return new PreparedWork((item.Hash, stream.Index), true);
    }

    /// <inheritdoc />
    public Project Handle(Project project, AnalyzeMotionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        MediaStream stream = StabilizeHelp.VideoStream(item, command.Stream);
        _ = PreparedWork.Reuse(context, (item.Hash, stream.Index), () => StabilizeHelp.Analysis(item, stream.Index, context, again: true) is not null);
        context.Changed(item.Id);
        return project;
    }
}
