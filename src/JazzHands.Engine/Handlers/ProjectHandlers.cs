using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Builds a new empty project.</summary>
/// <remarks>
/// The handler returns the project rather than loading it, so the same code makes a project in a
/// test, on the command line, and in the GUI. The session is what decides that this replaces what
/// was open.
/// </remarks>
public sealed class NewProjectHandler : ICommandHandler<NewProjectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, NewProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new CommandException("invalid-name", "A project needs a name.");
        }

        ProjectSettings settings = ProjectSettings.Default;

        if (command.Fps is { } fps)
        {
            settings = settings with { FrameRate = fps };
        }

        if (command.Size is { } size)
        {
            settings = settings with { Width = size.Width, Height = size.Height };
        }

        Project created = Project.CreateNew(command.Name, settings, context.Clock);

        context.Changed(created.Id);
        context.Changed(created.Sequences[0].Id);
        context.Changed(created.Sequences[0].Tracks.Select(track => track.Id));

        return created;
    }
}

/// <summary>Reads a .jazz file.</summary>
public sealed class OpenProjectHandler : ICommandHandler<OpenProjectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, OpenProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ProjectLoad load;
        try
        {
            load = ProjectFile.Load(command.Path);
        }
        catch (ProjectFileException error)
        {
            throw new CommandException("cannot-open", error.Message);
        }

        if (!load.IsLoadable)
        {
            string first = load.Issues.First(issue => issue.Severity == Core.Validation.Severity.Error).ToString();
            throw new CommandException(
                "project-invalid",
                $"'{load.Path}' has errors and will not open. The first is {first}. Try 'jazz repair'.");
        }

        context.Changed(load.Project.Id);
        return load.Project;
    }
}

/// <summary>Writes the project to disk.</summary>
/// <remarks>
/// Not undoable, and the only handler in this phase with an effect outside the project. It
/// returns the project it wrote, which may differ from the one it was given by its media paths:
/// saving is where an absolute path becomes a relative one.
/// </remarks>
public sealed class SaveProjectHandler : ICommandHandler<SaveProjectCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SaveProjectCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string path = command.Path ?? context.ProjectPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CommandException(
                "no-path",
                "This project has never been saved, so it needs a path: jazz project save <file>.");
        }

        try
        {
            return ProjectFile.Save(path, project);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CommandException("cannot-save", $"'{path}' could not be written: {error.Message}");
        }
    }
}

/// <summary>Changes the project's default frame rate, size and audio format.</summary>
public sealed class SetProjectSettingsHandler : ICommandHandler<SetProjectSettingsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetProjectSettingsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ProjectSettings settings = HandlerHelp.Apply(
            project.Settings,
            command.Fps,
            command.Size,
            command.SampleRate,
            command.ChannelCount,
            command.ColorSpace);

        if (settings == project.Settings)
        {
            return project;
        }

        context.Changed(project.Id);
        return project with { Settings = settings };
    }
}
