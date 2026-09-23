using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>
/// Finds the playback engine for the transport commands.
/// </summary>
/// <remarks>
/// The transport commands change nothing in the project, so their handlers hand the project back
/// untouched and the dispatcher spends no undo step on them. They still go through the dispatcher
/// like everything else, which is what puts a seek after the edit that came before it in a script.
/// </remarks>
internal static class PlaybackHelp
{
    /// <summary>The engine, or a coded refusal when this process has none.</summary>
    internal static IPlaybackController Controller(IServiceProvider? services) =>
        services?.GetService<IPlaybackController>()
        ?? throw new CommandException(
            "no-playback",
            "Playback needs a running editor, and this is a headless session. Start Jazz Hands, or send the command to one with --attach.");

    /// <summary>The engine when there is one.</summary>
    internal static IPlaybackController? TryController(IServiceProvider? services) =>
        services?.GetService<IPlaybackController>();

    /// <summary>Runs an action against the engine and hands the project back unchanged.</summary>
    internal static Project Drive(Project project, HandlerContext context, Action<IPlaybackController> action)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        action(Controller(context.Services));
        return project;
    }
}

/// <summary>Plays from the playhead.</summary>
public sealed class PlayHandler : ICommandHandler<PlayCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PlayCommand command, HandlerContext context) =>
        PlaybackHelp.Drive(project, context, playback => playback.Play());
}

/// <summary>Pauses where the playhead is.</summary>
public sealed class PauseHandler : ICommandHandler<PauseCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PauseCommand command, HandlerContext context) =>
        PlaybackHelp.Drive(project, context, playback => playback.Pause());
}

/// <summary>Plays or pauses.</summary>
public sealed class TogglePlaybackHandler : ICommandHandler<TogglePlaybackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, TogglePlaybackCommand command, HandlerContext context) =>
        PlaybackHelp.Drive(project, context, playback => playback.Toggle());
}

/// <summary>Stops and goes back to where playback started.</summary>
public sealed class StopPlaybackHandler : ICommandHandler<StopPlaybackCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, StopPlaybackCommand command, HandlerContext context) =>
        PlaybackHelp.Drive(project, context, playback => playback.Stop());
}

/// <summary>Moves the playhead.</summary>
public sealed class SeekHandler : ICommandHandler<SeekCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SeekCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.To < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "The playhead cannot go before the start of the sequence.");
        }

        return PlaybackHelp.Drive(project, context, playback => playback.Seek(command.To));
    }
}

/// <summary>Steps by whole frames.</summary>
public sealed class StepHandler : ICommandHandler<StepCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, StepCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        return PlaybackHelp.Drive(project, context, playback => playback.Step(command.Frames));
    }
}

/// <summary>Plays at a rate.</summary>
public sealed class ShuttleHandler : ICommandHandler<ShuttleCommand>
{
    /// <summary>The fastest shuttle, either way. The J and L keys stop doubling here.</summary>
    public const double MaxRate = 32.0;

    /// <inheritdoc />
    public Project Handle(Project project, ShuttleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (double.IsNaN(command.Rate) || Math.Abs(command.Rate) > MaxRate)
        {
            throw new CommandException(
                "rate-out-of-range",
                $"A shuttle rate runs from -{MaxRate} to {MaxRate}; 0 pauses. {command.Rate} is outside that.");
        }

        return PlaybackHelp.Drive(project, context, playback => playback.Shuttle(command.Rate));
    }
}

/// <summary>Turns looping on or off.</summary>
public sealed class SetLoopHandler : ICommandHandler<SetLoopCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetLoopCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        return PlaybackHelp.Drive(project, context, playback => playback.Loop = command.On ?? !playback.Loop);
    }
}

/// <summary>Chooses the preview resolution.</summary>
public sealed class SetQualityHandler : ICommandHandler<SetQualityCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetQualityCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Enum.IsDefined(command.Quality))
        {
            throw new CommandException("invalid-value", $"{(int)command.Quality} is not a preview quality.");
        }

        return PlaybackHelp.Drive(project, context, playback => playback.Quality = command.Quality);
    }
}

/// <summary>Sends the playhead somewhere by name.</summary>
/// <remarks>
/// "Next" and "previous" are strict: standing on an edit and asking for the next one goes to the
/// one after, which is what pressing the key twice has to do. When there is nowhere to go the
/// command fails with <c>no-target</c> and the playhead stays put.
/// </remarks>
public sealed class GoToHandler : ICommandHandler<GoToCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, GoToCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IPlaybackController playback = PlaybackHelp.Controller(context.Services);
        Sequence sequence = HandlerHelp.Sequence(project, null);
        Flicks frame = project.SettingsFor(sequence).FrameDuration;
        Flicks here = playback.Position;

        Flicks? target = command.Target switch
        {
            GoToTarget.Start => Flicks.Zero,
            GoToTarget.End => Flicks.Max(Flicks.Zero, sequence.Duration - frame),
            GoToTarget.NextEdit => TimelineQueries.NextEditPoint(sequence, here),
            GoToTarget.PrevEdit => TimelineQueries.PreviousEditPoint(sequence, here),
            GoToTarget.NextMarker => NextMarker(sequence, here),
            GoToTarget.PrevMarker => PreviousMarker(sequence, here),
            GoToTarget.In => sequence.InOut?.Start,
            GoToTarget.Out => sequence.InOut is { } range ? Flicks.Max(range.Start, range.End - frame) : null,
            _ => throw new CommandException("invalid-value", $"{(int)command.Target} is not somewhere to go."),
        };

        if (target is not { } time)
        {
            throw new CommandException("no-target", Nowhere(command.Target));
        }

        playback.Seek(time);
        return project;
    }

    private static Flicks? NextMarker(Sequence sequence, Flicks after)
    {
        Flicks? best = null;
        foreach (Marker marker in sequence.Markers)
        {
            if (marker.Time > after && (best is null || marker.Time < best))
            {
                best = marker.Time;
            }
        }

        return best;
    }

    private static Flicks? PreviousMarker(Sequence sequence, Flicks before)
    {
        Flicks? best = null;
        foreach (Marker marker in sequence.Markers)
        {
            if (marker.Time < before && (best is null || marker.Time > best))
            {
                best = marker.Time;
            }
        }

        return best;
    }

    private static string Nowhere(GoToTarget target) => target switch
    {
        GoToTarget.NextEdit => "There is no edit after the playhead.",
        GoToTarget.PrevEdit => "There is no edit before the playhead.",
        GoToTarget.NextMarker => "There is no marker after the playhead.",
        GoToTarget.PrevMarker => "There is no marker before the playhead.",
        _ => "The sequence has no in and out points. Set them with 'jazz playback set-in' and 'set-out'.",
    };
}

/// <summary>Sets the in point.</summary>
public sealed class SetInPointHandler : ICommandHandler<SetInPointCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetInPointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        Flicks frame = project.SettingsFor(sequence).FrameDuration;
        Flicks start = InOutHelp.Snap(InOutHelp.TimeOrPlayhead(command.At, context), project, sequence);

        // An out point that would end up at or before the new in point goes to the end of the
        // sequence, or one frame past the in point when the sequence is empty there.
        Flicks end = sequence.InOut is { } range && range.End > start
            ? range.End
            : Flicks.Max(sequence.Duration, start + frame);

        return InOutHelp.Apply(project, sequence, new TimeRange(start, end - start), context);
    }
}

/// <summary>Sets the out point.</summary>
public sealed class SetOutPointHandler : ICommandHandler<SetOutPointCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetOutPointCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        Flicks frame = project.SettingsFor(sequence).FrameDuration;

        // The frame at the out point is inside the range, so the range ends one frame later.
        Flicks end = InOutHelp.Snap(InOutHelp.TimeOrPlayhead(command.At, context), project, sequence) + frame;
        Flicks start = sequence.InOut is { } range && range.Start < end ? range.Start : Flicks.Zero;

        return InOutHelp.Apply(project, sequence, new TimeRange(start, end - start), context);
    }
}

/// <summary>Removes the in and out points.</summary>
public sealed class ClearInOutHandler : ICommandHandler<ClearInOutCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearInOutCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        if (sequence.InOut is null)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { InOut = null });
    }
}

/// <summary>What playback is doing.</summary>
public sealed class GetPlaybackStateHandler : IQueryHandler<GetPlaybackStateQuery, PlaybackStateInfo>
{
    /// <inheritdoc />
    public PlaybackStateInfo Handle(Project project, GetPlaybackStateQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PlaybackHelp.Controller(context.Services).Describe();
    }
}

/// <summary>What the in and out handlers share.</summary>
internal static class InOutHelp
{
    /// <summary>The time the command gave, or the playhead of a running editor.</summary>
    internal static Flicks TimeOrPlayhead(Flicks? given, HandlerContext context)
    {
        if (given is { } time)
        {
            if (time < Flicks.Zero)
            {
                throw new CommandException("time-out-of-range", "An in or out point cannot be before the start of the sequence.");
            }

            return time;
        }

        return PlaybackHelp.TryController(context.Services)?.Position
            ?? throw new CommandException(
                "no-playhead",
                "There is no playhead in a headless session. Say where with --at.");
    }

    /// <summary>Puts a time on the frame it falls in, so a range always holds whole frames.</summary>
    internal static Flicks Snap(Flicks time, Project project, Sequence sequence)
    {
        Rational rate = project.SettingsFor(sequence).FrameRate;
        return Flicks.FromFrames(time.ToFrames(rate, RoundingMode.Floor), rate);
    }

    /// <summary>Stores a range, or hands the project back when it is already the range.</summary>
    internal static Project Apply(Project project, Sequence sequence, TimeRange range, HandlerContext context)
    {
        if (sequence.InOut == range)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { InOut = range });
    }
}
