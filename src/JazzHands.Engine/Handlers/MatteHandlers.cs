using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Models;
using JazzHands.Render.Effects.Keying;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Making sure a clip's file has its person matte, for the commands that add the effect.</summary>
internal static class MatteHelp
{
    /// <summary>Makes the matte of a clip's file when it has none yet; refuses a clip that is not of a video file.</summary>
    internal static void Ensure(Project project, Clip clip, HandlerContext context)
    {
        if (clip.MediaId is not { } mediaId || MediaServices.Require(project, mediaId) is not { Kind: MediaKind.Movie } item)
        {
            throw new CommandException("not-media", "Only a clip of a video file has a picture to cut a person out of.");
        }

        MediaStream stream = StabilizeHelp.VideoStream(item, SceneCutHelp.VideoStreamOf(item, clip));
        MatteService service = context.Services?.GetService<MatteService>() ?? MatteService.For(context.Services?.GetService<Media.Import.CacheManager>());
        if (service.Cached(item.Hash, stream.Index) is not null)
        {
            return;
        }

        string path = HandlerHelp.Resolve(context.ProjectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{item.Name}' is not at {path}, so its picture cannot be read. Relink it first.");
        }

        try
        {
            service.Analyze(item, path, stream, cancellationToken: context.Cancellation);
        }
        catch (FileNotFoundException) when (!ModelStore.IsPresent(MatteService.Model))
        {
            throw new CommandException("model-missing", string.Create(CultureInfo.InvariantCulture, $"The background removal model is not downloaded. `model.download {MatteService.Model.Name}` fetches it ({MatteService.Model.Bytes / 1e6:0} MB); ask first."), MatteService.Model.Name);
        }
        catch (Media.Interop.FfmpegException exception)
        {
            throw new CommandException("analysis-failed", $"The picture of '{item.Name}' could not be read: {exception.Message}");
        }
    }
}

/// <summary>Cuts a person out of a clip: makes its file's matte, then puts the effect first.</summary>
/// <remarks>The matte is made before the command is queued, so edits go on meanwhile.</remarks>
public sealed class RemoveBackgroundHandler : ICommandHandler<RemoveBackgroundCommand>, IPreparingHandler<RemoveBackgroundCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, RemoveBackgroundCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!command.Off && project.FindClip(command.ClipId) is { Clip.IsMedia: true } found)
        {
            MatteHelp.Ensure(project, found.Clip, context);
        }

        return null;
    }

    /// <inheritdoc />
    public Project Handle(Project project, RemoveBackgroundCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        int existing = clip.Effects.IndexOf(effect => string.Equals(effect.TypeId, PersonMatteEffect.TypeId, StringComparison.Ordinal));

        if (command.Off)
        {
            if (existing < 0)
            {
                return project;
            }

            context.Changed(clip.Id);
            return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = clip.Effects.RemoveAt(existing) }));
        }

        MatteHelp.Ensure(project, clip, context);
        if (existing >= 0)
        {
            return project;
        }

        Effect effect = Effect.Create(PersonMatteEffect.TypeId) with { Enabled = true };
        context.Changed(clip.Id);
        context.Changed(effect.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = clip.Effects.Insert(0, effect) }));
    }
}
