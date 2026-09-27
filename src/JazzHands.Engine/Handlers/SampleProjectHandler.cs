using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// Builds the sample project: a new project, then the ordinary commands that make the trailer,
/// each through its own handler.
/// </summary>
/// <remarks>
/// Like <see cref="NewProjectHandler"/> it returns the project rather than loading it; the session
/// decides that it replaces what was open. Built from commands rather than written out as JSON, so
/// it is always a project the editor can make, and a test that builds it checks every step.
/// </remarks>
public sealed class SampleProjectHandler : ICommandHandler<SampleProjectCommand>
{
    /// <summary>What the sample project is called.</summary>
    public const string Name = "Sample";

    /// <inheritdoc />
    public Project Handle(Project project, SampleProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IServiceProvider services = context.Services
            ?? throw new CommandException("no-services", "The sample is built from commands, and needs the engine's handlers to run them.");

        Project built = new NewProjectHandler().Handle(project, new NewProjectCommand(Name, Rational.Fps30, new FrameSize(1920, 1080)), context);
        foreach (ICommand step in Steps(built))
        {
            built = CommandDispatcher.Apply(services, built, step, context);
        }

        return built;
    }

    /// <summary>The commands that make the trailer, on a new project with one video and one audio track.</summary>
    public static IReadOnlyList<ICommand> Steps(Project fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        Sequence sequence = fresh.ActiveSequence!;
        string pictures = sequence.Tracks.First(track => track.Kind == TrackKind.Video).Id;
        string sound = sequence.Tracks.First(track => track.Kind == TrackKind.Audio).Id;
        string particles = Id.New();
        string wind = Id.New();

        // Four shots of five seconds: a gradient each, and particles over it.
        (string Name, string From, string To, string Particles)[] shots =
        [
            ("Dusk", "#1B2A6B", "#E86F3A", "gen.particles.dust"),
            ("Ember", "#2A0B0B", "#E8552A", "gen.particles.embers"),
            ("Frost", "#0D2A3A", "#9AD1E8", "gen.particles.snow"),
            ("Night", "#0D1117", "#3B2A6B", "gen.particles.magic"),
        ];

        string[] shotIds = [.. shots.Select(_ => Id.New())];
        var steps = new List<ICommand>
        {
            new RenameTrackCommand(pictures, "Shots"),
            new AddTrackCommand(TrackKind.Video, "Particles", TrackId: particles),
        };

        for (int index = 0; index < shots.Length; index++)
        {
            (string name, string from, string to, string particle) = shots[index];
            Flicks at = Seconds(index * 5);
            steps.Add(new AddClipCommand(pictures, at, GeneratorId: "gen.gradient", Duration: Seconds(5), Name: name, ClipId: shotIds[index]));
            steps.Add(new SetParamCommand(shotIds[index], "start-colour", from));
            steps.Add(new SetParamCommand(shotIds[index], "end-colour", to));
            steps.Add(new AddClipCommand(particles, at, GeneratorId: particle, Duration: Seconds(5), Name: name + " particles"));
            steps.Add(new AddMarkerCommand(at, name));
        }

        steps.Add(new AddTransitionCommand(shotIds[0], shotIds[1], "transition.crossfade", Seconds(1), Handles: TransitionHandles.Hold, Audio: false));
        steps.Add(new AddTransitionCommand(shotIds[1], shotIds[2], "transition.blur-dissolve", Seconds(1), Handles: TransitionHandles.Hold, Audio: false));

        // The titles: two from presets, two from the motion templates.
        steps.Add(new AddTitleCommand(Seconds(0.5), "Jazz Hands", "title-card", Seconds(4)));
        steps.Add(new AddTitleCommand(Seconds(10.5), "Frost\nThe third shot", "lower-third", Seconds(4)));
        steps.Add(new ApplyTemplateCommand("feature-callout", Seconds(6), Values("label=Press C for the razor", "target=0, 0", "inset=160, -220")));
        steps.Add(new ApplyTemplateCommand("end-card", Seconds(15), Values("headline=Your turn", "date=Made with Jazz Hands", "store-one=File, New project", "store-two=Help, User guide", "duration=4.5s")));

        // A quiet bed of wind under it all, fading in and out.
        steps.Add(new RenameTrackCommand(sound, "Wind"));
        steps.Add(new AddClipCommand(sound, Flicks.Zero, GeneratorId: "audio.gen.pink-noise", Duration: Seconds(20), Name: "Wind", ClipId: wind));
        steps.Add(new SetAudioGainCommand(wind, -18));
        steps.Add(new SetAudioFadeInCommand(wind, Seconds(2), Interp.EaseInOut));
        steps.Add(new SetAudioFadeOutCommand(wind, Seconds(3), Interp.EaseInOut));

        return steps;
    }

    private static Flicks Seconds(double seconds) => Flicks.FromSeconds(seconds);

    private static EquatableArray<string> Values(params string[] values) => new(ImmutableArray.Create(values));
}
