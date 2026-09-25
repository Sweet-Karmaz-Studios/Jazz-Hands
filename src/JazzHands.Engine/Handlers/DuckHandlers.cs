using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Ducks one track under another, or takes the ducking off.</summary>
public sealed class DuckAudioHandler : ICommandHandler<DuckAudioCommand>
{
    /// <summary>The ducker's type.</summary>
    public const string Ducker = "audio.ducker";

    /// <inheritdoc />
    public Project Handle(Project project, DuckAudioCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track music) = HandlerHelp.Track(project, command.MusicTrackId);
        HandlerHelp.RequireUnlocked(music);
        if (!music.IsAudio)
        {
            throw new CommandException("no-sound", $"'{music.Name}' is not a sound track.", "music");
        }

        if (command.Off)
        {
            if (!music.Effects.Any(effect => effect.TypeId == Ducker))
            {
                throw new CommandException("not-ducked", $"'{music.Name}' is not ducked.", "music");
            }

            context.Changed(music.Id);
            return project.ReplaceTrack(music with { Effects = EquatableArray.Create([.. music.Effects.Where(effect => effect.TypeId != Ducker)]) });
        }

        Track voice = sequence.Tracks.FirstOrDefault(track => track.Id == command.VoiceTrackId)
            ?? throw new CommandException(
                command.VoiceTrackId is null ? "missing-argument" : "track-not-found",
                command.VoiceTrackId is null ? "Give --voice, the track that turns the music down." : $"There is no track '{command.VoiceTrackId}' in '{sequence.Name}'.",
                "voice");
        if (!voice.IsAudio || voice.Id == music.Id)
        {
            throw new CommandException("invalid-value", "The voice is another sound track than the music.", "voice");
        }

        double attack = Milliseconds(command.Attack, 15, "attack");
        double hold = Milliseconds(command.Hold, 300, "hold");
        double release = Milliseconds(command.Release, 400, "release");
        if (command.Depth is > 0 or < -60 || command.Threshold is > 0 or < -80 || attack is < 0.1 or > 1000 || hold > 5000 || release is < 1 or > 10000)
        {
            throw new CommandException("invalid-value", "The depth is from -60 to 0 dB, the threshold from -80 to 0 dBFS, the attack up to a second, the hold up to five and the release up to ten.");
        }

        Effect ducker = music.Effects.FirstOrDefault(effect => effect.TypeId == Ducker) ?? Effect.Create(Ducker);
        ducker = ducker
            .WithParameter("key", AnimatedValue.Constant(new ParamValue.Text(voice.Id)))
            .WithParameter("depth", AnimatedValue.Constant((float)command.Depth))
            .WithParameter("threshold", AnimatedValue.Constant((float)command.Threshold))
            .WithParameter("attack", AnimatedValue.Constant((float)attack))
            .WithParameter("hold", AnimatedValue.Constant((float)hold))
            .WithParameter("release", AnimatedValue.Constant((float)release));

        EquatableArray<Effect> effects = music.Effects.Any(effect => effect.Id == ducker.Id)
            ? EquatableArray.Create([.. music.Effects.Select(effect => effect.Id == ducker.Id ? ducker : effect)])
            : music.Effects.Add(ducker);

        context.Changed([music.Id, ducker.Id]);
        return project.ReplaceTrack(music with { Effects = effects });
    }

    private static double Milliseconds(Flicks? time, double fallback, string name) => time switch
    {
        null => fallback,
        { IsNegative: true } => throw new CommandException("invalid-value", string.Create(CultureInfo.InvariantCulture, $"The {name} is a time, not a negative one."), name),
        { } given => given.ToSeconds() * 1000.0,
    };
}
