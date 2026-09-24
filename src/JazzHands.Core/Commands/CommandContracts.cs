using System.Collections.Immutable;

namespace JazzHands.Core.Commands;

/// <summary>
/// Something that changes the project.
/// </summary>
/// <remarks>
/// Every editing operation in Jazz Hands is one of these, and nothing else is allowed to mutate a
/// <see cref="Model.Project"/>. That is what makes the GUI, the CLI, the JSON-RPC server and the
/// MCP server the same editor rather than three imitations of one: they all build a command
/// record and hand it to the dispatcher.
///
/// A command is a record of serializable values. It carries ids and times, never pieces of the
/// project model, because it has to survive being written to a JSON script, sent down a pipe, and
/// replayed from a crash log.
/// </remarks>
public interface ICommand;

/// <summary>
/// A command that folds into the undo step before it when it only continues the same edit.
/// </summary>
/// <remarks>
/// Dragging a slider sends a command for every step so the preview follows the drag, and one
/// undo should take the whole drag back, not the last pixel of it. The undo stack merges a
/// command into the step before when this says it continues it, nothing else has happened in
/// between and it arrives within a second or so. The project is exactly what it would have been;
/// only the history is shorter.
/// </remarks>
public interface IMergeableCommand : ICommand
{
    /// <summary>True when this command changes the same thing <paramref name="previous"/> did, to another value.</summary>
    bool Continues(ICommand previous);
}

/// <summary>Something that reads the project without changing it.</summary>
/// <typeparam name="TResult">What the query returns.</typeparam>
public interface IQuery<out TResult>;

/// <summary>Marks a record as a command and names it.</summary>
/// <remarks>
/// The name is the single source of truth for four surfaces: the CLI verb (<c>jazz clip split</c>),
/// the JSON-RPC method (<c>clip.split</c>), the MCP tool name, and the GUI command console. Adding
/// a command to any of those by hand is a bug.
/// </remarks>
/// <param name="name">The command name, as <c>area.verb</c> in kebab-case.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute(string name) : Attribute
{
    /// <summary>The command name, as <c>area.verb</c>.</summary>
    public string Name { get; } = name;

    /// <summary>One line, shown in help and in the MCP tool schema.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// False for a command that cannot be taken back.
    /// </summary>
    /// <remarks>
    /// Almost everything is undoable, because the dispatcher snapshots the project before and
    /// restores it after; handlers never write inverse logic. The exceptions are commands with
    /// effects outside the project, such as saving a file or clearing a cache, and each one has
    /// to say why in <see cref="NotUndoableReason"/>.
    /// </remarks>
    public bool Undoable { get; init; } = true;

    /// <summary>Why this command cannot be undone. Required when <see cref="Undoable"/> is false.</summary>
    public string NotUndoableReason { get; init; } = string.Empty;
}

/// <summary>Marks a record as a query and names it.</summary>
/// <param name="name">The query name, as <c>area.verb</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class QueryAttribute(string name) : Attribute
{
    /// <summary>The query name.</summary>
    public string Name { get; } = name;

    /// <summary>One line, shown in help.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>Marks a command property as a positional argument on the command line.</summary>
/// <param name="position">Where it sits, counting from zero.</param>
/// <param name="description">One line, shown in help.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ArgAttribute(int position, string description) : Attribute
{
    /// <summary>Where it sits on the command line.</summary>
    public int Position { get; } = position;

    /// <summary>One line, shown in help.</summary>
    public string Description { get; } = description;
}

/// <summary>Marks a command property as a named option on the command line.</summary>
/// <param name="name">The option name in kebab-case, without the leading dashes.</param>
/// <param name="description">One line, shown in help.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class OptionAttribute(string name, string description) : Attribute
{
    /// <summary>The option name in kebab-case.</summary>
    public string Name { get; } = name;

    /// <summary>One line, shown in help.</summary>
    public string Description { get; } = description;
}

/// <summary>
/// An ordinary failure of a command: something the user asked for that cannot be done.
/// </summary>
/// <remarks>
/// Not a bug. Trimming a clip past the end of its source, naming a clip that is not there, or
/// editing a locked track are all things people do every day, and each one has a stable code the
/// CLI turns into an exit status, the API into an error object and the UI into a sentence. Real
/// bugs throw ordinary exceptions and are not caught.
/// </remarks>
public sealed class CommandException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">A stable kebab-case code, for example clip-not-found.</param>
    /// <param name="message">A sentence a person can act on.</param>
    /// <param name="path">A JSON pointer into the project, when there is one.</param>
    public CommandException(string code, string message, string path = "")
        : base(message)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Creates the exception with an underlying cause.</summary>
    public CommandException(string message, Exception inner)
        : base(message, inner) =>
        Code = "command-failed";

    /// <summary>Creates the exception with a message alone. Prefer the coded constructor.</summary>
    public CommandException(string message)
        : base(message) =>
        Code = "command-failed";

    /// <summary>Creates the exception with no message. Prefer the coded constructor.</summary>
    public CommandException() => Code = "command-failed";

    /// <summary>A stable kebab-case code tools can match on.</summary>
    public string Code { get; } = "command-failed";

    /// <summary>A JSON pointer into the project, or empty.</summary>
    public string Path { get; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => Path.Length == 0 ? $"{Code}: {Message}" : $"{Path}: {Code}: {Message}";
}

/// <summary>What running a command produced.</summary>
/// <param name="Ok">True when the command ran.</param>
/// <param name="Version">The project version after it ran.</param>
/// <param name="ChangedIds">The ids of everything the command touched.</param>
/// <param name="Code">The error code when it failed.</param>
/// <param name="Error">The error message when it failed.</param>
/// <param name="Path">A JSON pointer to what went wrong, when there is one.</param>
public sealed record CommandResult(
    bool Ok,
    long Version,
    ImmutableArray<string> ChangedIds,
    string? Code = null,
    string? Error = null,
    string? Path = null)
{
    /// <summary>A successful result.</summary>
    public static CommandResult Success(long version, ImmutableArray<string> changed) =>
        new(true, version, changed);

    /// <summary>A failed result built from a command exception.</summary>
    public static CommandResult Failure(long version, CommandException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new CommandResult(false, version, [], error.Code, error.Message, error.Path);
    }

    /// <summary>Throws when the command failed, for callers that would rather have an exception.</summary>
    public CommandResult EnsureOk() => Ok
        ? this
        : throw new CommandException(Code ?? "command-failed", Error ?? "The command failed.", Path ?? string.Empty);
}
