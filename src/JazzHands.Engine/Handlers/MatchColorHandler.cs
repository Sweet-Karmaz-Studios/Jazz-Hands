using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Frames;
using JazzHands.Render.Color;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Grades a clip so its colour matches another's, on a Colour Wheels effect.</summary>
public sealed class MatchColorHandler : ICommandHandler<MatchColorCommand>
{
    /// <summary>The effect the match is written to.</summary>
    public const string Wheels = "color.wheels";

    /// <inheritdoc />
    public Project Handle(Project project, MatchColorCommand command, HandlerContext context)
    {
        ClipLocation target = Picture(project, command.ClipId);
        ClipLocation reference = Picture(project, command.ReferenceClipId);
        HandlerHelp.RequireUnlocked(target.Track);

        StillRenderer renderer = context.Services?.GetService<StillRenderer>()
            ?? throw new CommandException("no-renderer", "This session has no renderer to look at frames with.");

        Clip clip = target.Clip;
        int existing = clip.Effects.IndexOf(effect => effect.TypeId == Wheels);
        Effect wheels = existing >= 0 ? clip.Effects[existing] : Effect.Create(Wheels);
        var held = new WheelsGrade(
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            1,
            Float4(wheels, "offset"),
            Float(wheels, "contrast", 1),
            Float(wheels, "pivot", 0.435f));

        // The clip as it is before the wheels (with them and what follows off), and the reference
        // as it is seen, each on its own.
        StillFrame frame = Look(renderer, Alone(project, target, existing), target, command.At, "at", context);
        StillFrame wanted = Look(renderer, Alone(project, reference, -1), reference, command.ReferenceAt, "reference-at", context);
        context.Cancellation.ThrowIfCancellationRequested();

        if (Blank(frame.Linear) || Blank(wanted.Linear))
        {
            throw new CommandException("blank-frame", "One of the frames is black all over, so there is no colour to match; pick another frame with --at or --reference-at.");
        }

        // An ACES project's wheels work on ACEScct of ACEScg, which is what its frames read back as.
        GradingDomain domain = project.Settings.ColorManagement is { IsAces: true } ? GradingDomain.Acescct : GradingDomain.Srgb;
        WheelsGrade grade = ShotMatch.Solve(frame.Linear, wanted.Linear, held, domain);
        Effect graded = wheels
            .WithParameter("lift", AnimatedValue.Constant(new ParamValue.Float4(grade.Lift)))
            .WithParameter("gamma", AnimatedValue.Constant(new ParamValue.Float4(grade.Gamma)))
            .WithParameter("gain", AnimatedValue.Constant(new ParamValue.Float4(grade.Gain)))
            .WithParameter("saturation", AnimatedValue.Constant(grade.Saturation)) with { Enabled = true };

        EquatableArray<Effect> effects = existing >= 0 ? clip.Effects.SetItem(existing, graded) : clip.Effects.Add(graded);
        context.Changed(clip.Id);
        context.Changed(graded.Id);
        return project.ReplaceTrack(target.Track.ReplaceClip(clip with { Effects = effects }));
    }

    /// <summary>A clip with a picture, or a refusal.</summary>
    private static ClipLocation Picture(Project project, string clipId)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);
        return found.Track.Kind == TrackKind.Audio
            ? throw new CommandException("not-video", $"Clip '{clipId}' is sound; only a picture has a colour to match.")
            : found;
    }

    /// <summary>
    /// The project with only this clip showing in its sequence: every other clip with a picture
    /// off, its track shown and unmatted, and, from <paramref name="offFrom"/> on, its own effects
    /// off.
    /// </summary>
    private static Project Alone(Project project, ClipLocation found, int offFrom)
    {
        Sequence sequence = found.Sequence;
        Track[] tracks = [.. sequence.Tracks.Select(track =>
        {
            if (track.Kind == TrackKind.Audio)
            {
                return track;
            }

            Clip[] clips = [.. track.Clips.Select(clip => clip.Id != found.Clip.Id
                ? clip with { Enabled = false }
                : clip with
                {
                    Enabled = true,
                    Effects = offFrom < 0 ? clip.Effects : new EquatableArray<Effect>([.. clip.Effects.Select((effect, index) => index >= offFrom ? effect with { Enabled = false } : effect)]),
                })];
            return track.Id == found.Track.Id
                ? track with { Clips = new EquatableArray<Clip>(clips), Muted = false, Solo = false, Matte = null }
                : track with { Clips = new EquatableArray<Clip>(clips), Solo = false };
        })];

        return project.ReplaceSequence(sequence with { Tracks = new EquatableArray<Track>(tracks) });
    }

    private static StillFrame Look(StillRenderer renderer, Project alone, ClipLocation found, Flicks? at, string option, HandlerContext context)
    {
        Clip clip = found.Clip;
        Rational rate = alone.SettingsFor(found.Sequence).FrameRate;
        Flicks time = at ?? clip.Start + new Flicks(clip.Duration.Value / 2);
        if (time < clip.Start || time >= clip.End)
        {
            throw new CommandException("time-out-of-range", $"--{option} is outside clip '{clip.Id}', which runs from {Timecode.Format(clip.Start, rate)} to {Timecode.Format(clip.End, rate)}.");
        }

        return renderer.Render(alone, alone.Sequence(found.Sequence.Id)!, time, context.ProjectPath);
    }

    private static bool Blank(float[] linear)
    {
        for (int index = 0; index < linear.Length; index += 4)
        {
            if (linear[index] > 1e-4f || linear[index + 1] > 1e-4f || linear[index + 2] > 1e-4f)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector4 Float4(Effect effect, string name) =>
        effect.Parameter(name) is StaticValue { Value: ParamValue.Float4 value } ? value.Value : Vector4.Zero;

    private static float Float(Effect effect, string name, float fallback) =>
        effect.Parameter(name) is StaticValue { Value: ParamValue.Float value } ? value.Value : fallback;
}
