using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Adds an empty sequence with one video and one audio track.</summary>
public sealed class CreateSequenceHandler : ICommandHandler<CreateSequenceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CreateSequenceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new CommandException("invalid-name", "A sequence needs a name.");
        }

        string id = HandlerHelp.IdOr(command.SequenceId);
        HandlerHelp.RequireUnused(project, id);

        var sequence = new Sequence(
            id,
            command.Name,
            EquatableArray.Create(
                new Track(Id.New(), TrackKind.Video, "V1", 0),
                new Track(Id.New(), TrackKind.Audio, "A1", 1)));

        context.Changed(id);
        context.Changed(sequence.Tracks.Select(track => track.Id));

        Project updated = project.AddSequence(sequence);

        if (command.SetActive)
        {
            context.Changed(project.Id);
            updated = updated with { ActiveSequenceId = id };
        }

        return updated;
    }
}

/// <summary>Changes a sequence's display name.</summary>
public sealed class RenameSequenceHandler : ICommandHandler<RenameSequenceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RenameSequenceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new CommandException("invalid-name", "A sequence needs a name.");
        }

        if (string.Equals(sequence.Name, command.Name, StringComparison.Ordinal))
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Name = command.Name });
    }
}

/// <summary>Removes a sequence.</summary>
public sealed class RemoveSequenceHandler : ICommandHandler<RemoveSequenceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveSequenceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        if (project.Sequences.Length == 1)
        {
            throw new CommandException("last-sequence", "A project needs at least one sequence.");
        }

        // A compound clip elsewhere would be left pointing at nothing, which the validator would
        // report as an error on every load from then on.
        foreach (Sequence other in project.Sequences)
        {
            if (string.Equals(other.Id, sequence.Id, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Track track in other.Tracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    if (string.Equals(clip.SequenceId, sequence.Id, StringComparison.Ordinal))
                    {
                        throw new CommandException(
                            "sequence-in-use",
                            $"'{sequence.Name}' is nested in '{other.Name}' as clip '{clip.Id}'. Remove that clip first.");
                    }
                }
            }
        }

        Project updated = project.RemoveSequence(sequence.Id);
        context.Changed(sequence.Id);

        if (string.Equals(project.ActiveSequenceId, sequence.Id, StringComparison.Ordinal))
        {
            context.Changed(project.Id);
            updated = updated with { ActiveSequenceId = updated.Sequences[0].Id };
        }

        return updated;
    }
}

/// <summary>Chooses the sequence the editor shows.</summary>
public sealed class SetActiveSequenceHandler : ICommandHandler<SetActiveSequenceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetActiveSequenceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        if (string.Equals(project.ActiveSequenceId, sequence.Id, StringComparison.Ordinal))
        {
            return project;
        }

        context.Changed(project.Id);
        return project with { ActiveSequenceId = sequence.Id };
    }
}

/// <summary>Gives a sequence its own frame rate, size or audio format.</summary>
public sealed class SetSequenceSettingsHandler : ICommandHandler<SetSequenceSettingsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSequenceSettingsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);

        if (command.Inherit)
        {
            if (sequence.Settings is null)
            {
                return project;
            }

            context.Changed(sequence.Id);
            return project.ReplaceSequence(sequence with { Settings = null });
        }

        ProjectSettings settings = HandlerHelp.Apply(
            sequence.Settings ?? project.Settings,
            command.Fps,
            command.Size,
            command.SampleRate,
            command.ChannelCount,
            command.ColorSpace);

        if (settings == sequence.Settings)
        {
            return project;
        }

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Settings = settings });
    }
}
