using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Puts a packaged speed ramp on a clip.</summary>
public sealed class SpeedPresetHandler : ICommandHandler<SpeedPresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SpeedPresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        Clip clip = found.Clip;
        if (!clip.IsMedia || found.Track.Kind != TrackKind.Video)
        {
            throw new CommandException("not-media", "Only a clip of a video file plays source at a speed.");
        }

        if (!Enum.IsDefined(command.Preset))
        {
            throw new CommandException("invalid-value", $"{(int)command.Preset} is not a speed preset.");
        }

        Flicks length = command.Duration ?? Flicks.FromSeconds(2);
        if (length <= Flicks.Zero)
        {
            throw new CommandException("bad-duration", "It takes some time.", "dur");
        }

        if (command.At < clip.Start || command.At > clip.End)
        {
            throw new CommandException("time-out-of-range", $"The moment is not inside '{clip.Name}'.", "at");
        }

        return command.Preset switch
        {
            SpeedPreset.Impact => Ramp(project, found, command.At, [(-0.5, 1), (-0.15, 0.2), (0.35, 0.2), (0.47, 1)], context),
            SpeedPreset.Traversal => Ramp(project, found, command.At, [(0, 1), (0.3, 3), (0.3 + length.ToSeconds(), 3), (0.6 + length.ToSeconds(), 1)], context),
            SpeedPreset.Beat => Ramp(project, found, command.At, ToTheBeat(found, command.At, length), context),
            _ => Repeat(project, found, command.Preset, command.At, length, context),
        };
    }

    /// <summary>Normal speed on each beat marker from a moment for a while, and three times between each pair (Phase 45).</summary>
    private static (double After, double Speed)[] ToTheBeat(ClipLocation found, Flicks at, Flicks length)
    {
        Flicks[] beats = [.. found.Sequence.Markers
            .Where(marker => marker.Kind == MarkerKind.Beat && marker.Time >= at && marker.Time <= at + length && marker.Time <= found.Clip.End)
            .Select(marker => marker.Time)
            .Order()];
        if (beats.Length < 2)
        {
            throw new CommandException("no-beats", "There are not two beat markers there to ramp between. Find the beats first: audio.beats on the music.");
        }

        var points = new List<(double, double)>();
        for (int index = 0; index < beats.Length; index++)
        {
            points.Add(((beats[index] - at).ToSeconds(), 1));
            if (index + 1 < beats.Length)
            {
                points.Add((((beats[index] + beats[index + 1]) / 2 - at).ToSeconds(), 3));
            }
        }

        return [.. points];
    }

    /// <summary>
    /// The clip's remap curve made to pass through speeds at times from the moment, holding its
    /// speed before and after, and blending frames.
    /// </summary>
    private static Project Ramp(Project project, ClipLocation found, Flicks at, (double After, double Speed)[] points, HandlerContext context)
    {
        Clip clip = found.Clip;
        Flicks local = at - clip.Start;
        Flicks first = local + Flicks.FromSeconds(points[0].After);
        Flicks last = local + Flicks.FromSeconds(points[^1].After);
        if (first < Flicks.Zero || last > clip.Duration)
        {
            throw new CommandException("time-out-of-range", $"The ramp runs past '{clip.Name}': it needs from {Timecode.FormatClock(clip.Start + first)} to {Timecode.FormatClock(clip.Start + last)}.", "at");
        }

        var speed = new ParamValue.Float((float)clip.EffectiveSpeed.ToDouble());
        Keyframe[] kept = clip.Remap switch
        {
            KeyframedValue keyed => [.. keyed.Keyframes.Where(keyframe => keyframe.Time < first || keyframe.Time > last)],
            _ when first > Flicks.Zero => [new Keyframe(Flicks.Zero, clip.Remap is StaticValue { Value: ParamValue.Float held } ? held : speed, Interp.Hold)],
            _ => [],
        };

        Keyframe[] ramp =
        [
            .. points.Select((point, index) => new Keyframe(
                local + Flicks.FromSeconds(point.After),
                new ParamValue.Float((float)(point.Speed * speed.Value)),
                index == points.Length - 1 ? Interp.Hold : Interp.EaseInOut)),
        ];

        Project updated = RemapHelp.Apply(project, found, new KeyframedValue(kept.Concat(ramp)), context);
        ClipLocation again = updated.FindClip(clip.Id)!;
        return updated.ReplaceTrack(again.Track.ReplaceClip(again.Clip with { Retime = RetimeMode.Blend }));
    }

    /// <summary>The seconds before the moment again, on a new track above: backwards and fast, or slow.</summary>
    private static Project Repeat(Project project, ClipLocation found, SpeedPreset preset, Flicks at, Flicks length, HandlerContext context)
    {
        Clip clip = found.Clip;
        if (clip.Reverse || clip.IsRemapped || clip.IsHold)
        {
            throw new CommandException("not-plain", "A rewind or a replay is taken from a clip playing forwards at one speed.");
        }

        Flicks sourceEnd = clip.SourceTimeAt(at);
        Flicks sourceStart = sourceEnd - Flicks.FromSeconds(length.ToSeconds() * clip.EffectiveSpeed.ToDouble());
        if (sourceStart < clip.SourceIn)
        {
            sourceStart = clip.SourceIn;
        }

        if (sourceEnd <= sourceStart)
        {
            throw new CommandException("time-out-of-range", "There is nothing of the clip before the moment to repeat.", "at");
        }

        bool rewind = preset == SpeedPreset.Rewind;
        var speed = new Rational(rewind ? 4 : 1, rewind ? 1 : 2);
        Flicks source = sourceEnd - sourceStart;
        Flicks shown = new((long)Math.Round(source.Value / speed.ToDouble()));
        var copy = new Clip(
            Id.New(),
            new TimeRange(at, shown),
            sourceStart,
            MediaId: clip.MediaId,
            SourceStreamIndex: clip.SourceStreamIndex,
            Speed: speed,
            Reverse: rewind,
            Name: rewind ? $"{clip.Name} (rewind)" : $"{clip.Name} (replay)")
        {
            Transform = clip.Transform,
            Crop = clip.Crop,
            Retime = rewind ? RetimeMode.Nearest : RetimeMode.Blend,
            Effects = rewind
                ? [Effect.Create("video.vhs").WithParameter("tracking", AnimatedValue.Constant(0.9f)), Effect.Create("video.glitch").WithParameter("amount", AnimatedValue.Constant(0.25f))]
                : [Effect.Create("video.letterbox").WithParameter("aspect", AnimatedValue.Constant(2.39f))],
        };

        Sequence sequence = found.Sequence;
        var above = new Track(Id.New(), TrackKind.Video, HandlerHelp.TrackName(sequence, TrackKind.Video), sequence.NextTrackOrder(), [copy]);
        Sequence updated = sequence.AddTrack(above);
        context.Changed(copy.Id);
        context.Changed(above.Id);
        Project result = project.ReplaceSequence(updated);

        if (!rewind)
        {
            // REPLAY in the top left corner, inside the bars.
            string label = Id.New();
            result = context.Run(result with { ActiveSequenceId = sequence.Id }, new AddTitleCommand(at, "REPLAY", "caption-bold", shown, ClipId: label, SequenceId: sequence.Id));
            ProjectSettings settings = result.SettingsFor(sequence);
            string corner = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{-settings.Width * 0.36:0}, {-settings.Height * 0.3:0}");
            result = context.Run(result, new SetParamCommand(label, "position", corner)) with { ActiveSequenceId = project.ActiveSequenceId };
        }

        return result;
    }
}
