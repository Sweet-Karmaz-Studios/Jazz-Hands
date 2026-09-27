using System.Globalization;
using JazzHands.Audio.Effects;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Models;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Making sure a clip's file has its enhanced speech, for the commands that add the effect.</summary>
internal static class EnhanceHelp
{
    /// <summary>Enhances the speech of a clip's sound stream when it has not been; refuses a clip without sound.</summary>
    internal static void Ensure(Project project, ClipLocation found, HandlerContext context)
    {
        if (SpeechHelp.SoundOf(project, found) is not { } sound)
        {
            throw new CommandException("no-sound", $"'{found.Clip.Name}' plays no sound from a file, so there is no speech to enhance.");
        }

        SpeechEnhanceService service = context.Services?.GetService<SpeechEnhanceService>() ?? SpeechEnhanceService.For(context.Services?.GetService<Media.Import.CacheManager>());
        if (service.IsMade(sound.Item.Hash, sound.Stream))
        {
            return;
        }

        MediaStream stream = sound.Item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == sound.Stream)
            ?? throw new CommandException("no-sound", $"'{sound.Item.Name}' has no sound stream {sound.Stream}.");
        string path = HandlerHelp.Resolve(context.ProjectPath, sound.Item.RelativePath);
        if (!File.Exists(path))
        {
            throw new CommandException("media-missing", $"'{sound.Item.Name}' is not at {path}, so its sound cannot be read. Relink it first.");
        }

        try
        {
            service.Enhance(sound.Item, path, stream, cancellationToken: context.Cancellation);
        }
        catch (FileNotFoundException) when (!ModelStore.IsPresent(SpeechEnhanceService.Model))
        {
            throw new CommandException("model-missing", string.Create(CultureInfo.InvariantCulture, $"The speech enhancement model is not downloaded. `model.download {SpeechEnhanceService.Model.Name}` fetches it ({SpeechEnhanceService.Model.Bytes / 1e6:0} MB); ask first."), SpeechEnhanceService.Model.Name);
        }
        catch (Media.Interop.FfmpegException exception)
        {
            throw new CommandException("analysis-failed", $"The sound of '{sound.Item.Name}' could not be read: {exception.Message}");
        }
    }
}

/// <summary>Enhances a clip's speech: makes the enhanced sound, then puts the effect on with its amount.</summary>
public sealed class EnhanceSpeechHandler : ICommandHandler<EnhanceSpeechCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, EnhanceSpeechCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        int existing = clip.Effects.IndexOf(effect => string.Equals(effect.TypeId, EnhanceSpeechEffect.TypeId, StringComparison.Ordinal));

        if (command.Off)
        {
            if (existing < 0)
            {
                return project;
            }

            context.Changed(clip.Id);
            return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = clip.Effects.RemoveAt(existing) }));
        }

        if (!double.IsFinite(command.Amount) || command.Amount is < 0 or > 100)
        {
            throw new CommandException("invalid-value", "The amount is 0 to 100 percent.", "amount");
        }

        EnhanceHelp.Ensure(project, found, context);
        Effect effect = (existing >= 0 ? clip.Effects[existing] : Effect.Create(EnhanceSpeechEffect.TypeId) with { Enabled = true })
            .WithParameter("amount", AnimatedValue.Constant((float)command.Amount));
        if (existing >= 0 && effect == clip.Effects[existing])
        {
            return project;
        }

        EquatableArray<Effect> effects = existing >= 0 ? clip.Effects.SetItem(existing, effect) : clip.Effects.Insert(0, effect);
        context.Changed(clip.Id);
        context.Changed(effect.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(clip with { Effects = effects }));
    }
}
