using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>One parameter of a command or a query.</summary>
/// <param name="Name">The property name, in PascalCase.</param>
/// <param name="JsonName">How it is spelled in JSON, in camelCase.</param>
/// <param name="CliName">How it is spelled on the command line, in kebab-case.</param>
/// <param name="Type">The type it holds.</param>
/// <param name="Description">One line, from the attribute.</param>
/// <param name="Position">Its position as a positional argument, or -1 when it is an option.</param>
/// <param name="IsRequired">True when the record's constructor gives it no default.</param>
/// <param name="DefaultValue">The constructor's default, when it has one.</param>
public sealed record ParameterMetadata(
    string Name,
    string JsonName,
    string CliName,
    Type Type,
    string Description,
    int Position,
    bool IsRequired,
    object? DefaultValue)
{
    /// <summary>True when this parameter is written as a positional argument.</summary>
    public bool IsPositional => Position >= 0;
}

/// <summary>Everything the four surfaces need to know about one command or query.</summary>
/// <param name="Name">The name, as <c>area.verb</c>.</param>
/// <param name="Area">The part before the dot.</param>
/// <param name="Verb">The part after the dot.</param>
/// <param name="Type">The record type.</param>
/// <param name="Description">One line, from the attribute.</param>
/// <param name="IsQuery">True for a query, false for a command.</param>
/// <param name="Undoable">False for a command that cannot be taken back.</param>
/// <param name="NotUndoableReason">Why, when it cannot.</param>
/// <param name="ResultType">What a query returns, or null for a command.</param>
/// <param name="Parameters">Its parameters, in constructor order.</param>
public sealed record CommandMetadata(
    string Name,
    string Area,
    string Verb,
    Type Type,
    string Description,
    bool IsQuery,
    bool Undoable,
    string NotUndoableReason,
    Type? ResultType,
    ImmutableArray<ParameterMetadata> Parameters)
{
    /// <summary>The positional arguments, in order.</summary>
    public IEnumerable<ParameterMetadata> Arguments =>
        Parameters.Where(parameter => parameter.IsPositional).OrderBy(parameter => parameter.Position);

    /// <summary>The named options, in declaration order.</summary>
    public IEnumerable<ParameterMetadata> Options => Parameters.Where(parameter => !parameter.IsPositional);

    /// <summary>A one-line usage string, for help.</summary>
    public string Usage
    {
        get
        {
            var parts = new List<string> { "jazz", Area };
            if (Verb.Length > 0)
            {
                parts.Add(Verb);
            }

            parts.AddRange(Arguments.Select(argument =>
                argument.IsRequired ? $"<{argument.CliName}>" : $"[{argument.CliName}]"));
            parts.AddRange(Options.Select(option => option.IsRequired
                ? $"--{option.CliName} <{option.CliName}>"
                : $"[--{option.CliName} <{option.CliName}>]"));

            return string.Join(' ', parts);
        }
    }
}

/// <summary>
/// The catalogue of every command and query, found by reflection.
/// </summary>
/// <remarks>
/// One attribute, four surfaces. The CLI builds its verbs from this, the JSON-RPC server builds
/// its method list, the MCP server builds its tool schemas, and the GUI command console builds
/// its completions. Adding a command to any of those by hand is a bug, because it would exist on
/// one surface and not the others, and remote control by Claude Code is a headline feature rather
/// than an afterthought.
///
/// The scan runs once, over the assembly holding <see cref="ICommand"/>, so a command is
/// registered by existing rather than by being listed anywhere.
/// </remarks>
public static class CommandRegistry
{
    private static readonly Lazy<ImmutableArray<CommandMetadata>> LazyAll = new(Scan);

    private static readonly Lazy<ImmutableDictionary<string, CommandMetadata>> LazyByName = new(() =>
        LazyAll.Value.ToImmutableDictionary(entry => entry.Name, StringComparer.Ordinal));

    /// <summary>Every command and query, sorted by name.</summary>
    public static ImmutableArray<CommandMetadata> All => LazyAll.Value;

    /// <summary>Every command, sorted by name.</summary>
    public static IEnumerable<CommandMetadata> Commands => All.Where(entry => !entry.IsQuery);

    /// <summary>Every query, sorted by name.</summary>
    public static IEnumerable<CommandMetadata> Queries => All.Where(entry => entry.IsQuery);

    /// <summary>The metadata for a name, or null.</summary>
    public static CommandMetadata? Find(string name) =>
        LazyByName.Value.TryGetValue(name ?? string.Empty, out CommandMetadata? found) ? found : null;

    /// <summary>The metadata for a name, or a coded error naming the closest matches.</summary>
    public static CommandMetadata Require(string name)
    {
        if (Find(name) is { } found)
        {
            return found;
        }

        string[] near =
        [
            .. All.Select(entry => entry.Name)
                .Where(candidate => candidate.Contains(name ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .Take(3),
        ];

        string hint = near.Length > 0 ? $" Did you mean {string.Join(", ", near)}?" : string.Empty;
        throw new CommandException("unknown-command", $"There is no command called '{name}'.{hint}");
    }

    /// <summary>The metadata for a command instance.</summary>
    public static CommandMetadata Describe(object command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return All.FirstOrDefault(entry => entry.Type == command.GetType())
            ?? throw new CommandException(
                "unregistered-command",
                $"{command.GetType().Name} has no [Command] or [Query] attribute.");
    }

    /// <summary>The name of a command instance.</summary>
    public static string NameOf(object command) => Describe(command).Name;

    /// <summary>
    /// Writes a command as <c>{"command": "clip.split", "args": {...}}</c>.
    /// </summary>
    /// <remarks>
    /// This is the shape of a line in the history log, an entry in a <c>jazz apply</c> script and
    /// a JSON-RPC parameter object, so it has one definition rather than three.
    /// </remarks>
    public static JsonObject ToJson(object command)
    {
        CommandMetadata metadata = Describe(command);

        return new JsonObject
        {
            ["command"] = metadata.Name,
            ["args"] = ArgsToJson(command, metadata),
        };
    }

    /// <summary>Writes just a command's arguments.</summary>
    public static JsonObject ArgsToJson(object command, CommandMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        metadata ??= Describe(command);
        var args = new JsonObject();

        foreach (ParameterMetadata parameter in metadata.Parameters)
        {
            object? value = metadata.Type.GetProperty(parameter.Name)!.GetValue(command);

            // A parameter sitting at its default says nothing, and leaving it out keeps a history
            // line and an apply script readable.
            if (!parameter.IsRequired && Equals(value, parameter.DefaultValue))
            {
                continue;
            }

            args[parameter.JsonName] = value is ICommand[] nested
                ? new JsonArray([.. nested.Select(ToJson)])
                : JsonSerializer.SerializeToNode(value, parameter.Type, JazzJson.Options);
        }

        return args;
    }

    /// <summary>Reads a command back from <c>{"command": ..., "args": {...}}</c>.</summary>
    public static object FromJson(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        string name = document["command"]?.GetValue<string>()
            ?? throw new CommandException("missing-command", "A command object needs a 'command' member.");

        return FromJson(name, document["args"] as JsonObject ?? []);
    }

    /// <summary>Builds a command of the given name from its arguments.</summary>
    public static object FromJson(string name, JsonObject args)
    {
        ArgumentNullException.ThrowIfNull(args);

        CommandMetadata metadata = Require(name);
        object?[] values = new object?[metadata.Parameters.Length];

        for (int index = 0; index < metadata.Parameters.Length; index++)
        {
            ParameterMetadata parameter = metadata.Parameters[index];

            if (!args.TryGetPropertyValue(parameter.JsonName, out JsonNode? node) || node is null)
            {
                values[index] = parameter.IsRequired
                    ? throw new CommandException(
                        "missing-argument",
                        $"'{name}' needs '{parameter.JsonName}'.")
                    : parameter.DefaultValue;
                continue;
            }

            try
            {
                // A batch holds commands rather than values, so its list is read back through
                // the registry the same way it was written.
                values[index] = parameter.Type == typeof(ICommand[]) && node is JsonArray nested
                    ? nested.Select(entry => (ICommand)FromJson(
                        entry as JsonObject
                            ?? throw new CommandException("invalid-argument", "A batch holds command objects."))).ToArray()
                    : node.Deserialize(parameter.Type, JazzJson.Options);
            }
            catch (JsonException error)
            {
                throw new CommandException(
                    "invalid-argument",
                    $"'{parameter.JsonName}' of '{name}' is not a {FriendlyTypeName(parameter.Type)}: {error.Message}");
            }
        }

        return Construct(metadata, values);
    }

    /// <summary>
    /// Builds a command from the way it is typed on a command line.
    /// </summary>
    /// <param name="name">The command name.</param>
    /// <param name="arguments">Positional values, in order.</param>
    /// <param name="options">Named values, keyed by kebab-case option name.</param>
    /// <param name="frameRate">The rate timecode is read against.</param>
    public static object FromCommandLine(
        string name,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> options,
        Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);

        CommandMetadata metadata = Require(name);
        object?[] values = new object?[metadata.Parameters.Length];

        int positional = 0;
        for (int index = 0; index < metadata.Parameters.Length; index++)
        {
            ParameterMetadata parameter = metadata.Parameters[index];
            string? text;

            if (parameter.IsPositional)
            {
                text = positional < arguments.Count ? arguments[positional] : null;
                positional++;
            }
            else if (!options.TryGetValue(parameter.CliName, out text))
            {
                text = null;
            }

            if (text is null)
            {
                values[index] = parameter.IsRequired
                    ? throw new CommandException(
                        "missing-argument",
                        parameter.IsPositional
                            ? $"'{name}' needs <{parameter.CliName}>."
                            : $"'{name}' needs --{parameter.CliName}.")
                    : parameter.DefaultValue;
                continue;
            }

            values[index] = CommandValues.Parse(parameter.Type, text, frameRate, parameter.CliName);
        }

        if (positional < arguments.Count)
        {
            throw new CommandException(
                "too-many-arguments",
                $"'{name}' takes {positional} argument(s), not {arguments.Count}.");
        }

        return Construct(metadata, values);
    }

    /// <summary>Writes a command as the command line a person would type for it.</summary>
    public static string ToCommandLine(object command, Rational frameRate)
    {
        CommandMetadata metadata = Describe(command);
        var parts = new List<string> { "jazz", metadata.Area };
        if (metadata.Verb.Length > 0)
        {
            parts.Add(metadata.Verb);
        }


        foreach (ParameterMetadata argument in metadata.Arguments)
        {
            object? value = metadata.Type.GetProperty(argument.Name)!.GetValue(command);
            if (value is not null)
            {
                parts.Add(Quote(CommandValues.Format(value, frameRate)));
            }
        }

        foreach (ParameterMetadata option in metadata.Options)
        {
            object? value = metadata.Type.GetProperty(option.Name)!.GetValue(command);
            if (value is null || Equals(value, option.DefaultValue))
            {
                continue;
            }

            parts.Add($"--{option.CliName}");
            parts.Add(Quote(CommandValues.Format(value, frameRate)));
        }

        return string.Join(' ', parts);
    }

    private static object Construct(CommandMetadata metadata, object?[] values)
    {
        try
        {
            return metadata.Type.GetConstructors()[0].Invoke(values);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            // A record's own validation, such as a rational with a zero denominator, surfaces as
            // an ordinary command error rather than as a reflection failure nobody can read.
            throw new CommandException("invalid-argument", error.InnerException.Message, string.Empty);
        }
    }

    private static ImmutableArray<CommandMetadata> Scan()
    {
        var found = ImmutableArray.CreateBuilder<CommandMetadata>();

        foreach (Type type in typeof(ICommand).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            if (type.GetCustomAttribute<CommandAttribute>() is { } command)
            {
                found.Add(Build(
                    type,
                    command.Name,
                    command.Description,
                    isQuery: false,
                    command.Undoable,
                    command.NotUndoableReason,
                    resultType: null));
            }
            else if (type.GetCustomAttribute<QueryAttribute>() is { } query)
            {
                found.Add(Build(
                    type,
                    query.Name,
                    query.Description,
                    isQuery: true,
                    undoable: false,
                    notUndoableReason: "Queries do not change anything.",
                    resultType: ResultTypeOf(type)));
            }
        }

        var duplicates = found
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Two commands share a name: {string.Join(", ", duplicates)}. A name is the CLI verb, the "
                + "JSON-RPC method and the MCP tool, so it has to be unique.");
        }

        return [.. found.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static CommandMetadata Build(
        Type type,
        string name,
        string description,
        bool isQuery,
        bool undoable,
        string notUndoableReason,
        Type? resultType)
    {
        // Almost every name is area.verb. A handful are a single word because they belong to no
        // area and reading "jazz session undo" would be worse than reading "jazz undo".
        string[] halves = name.Split('.', 2);
        if (halves.Length == 0 || halves[0].Length == 0 || (halves.Length == 2 && halves[1].Length == 0))
        {
            throw new InvalidOperationException($"'{name}' on {type.Name} is not an area.verb name.");
        }

        ConstructorInfo[] constructors = type.GetConstructors();
        if (constructors.Length != 1)
        {
            throw new InvalidOperationException(
                $"{type.Name} needs exactly one public constructor; a command is a record of its parameters.");
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterMetadata>();

        foreach (ParameterInfo parameter in constructors[0].GetParameters())
        {
            PropertyInfo property = type.GetProperty(parameter.Name!)
                ?? throw new InvalidOperationException(
                    $"{type.Name} has a constructor parameter '{parameter.Name}' with no matching property.");

            ArgAttribute? argument = property.GetCustomAttribute<ArgAttribute>();
            OptionAttribute? option = property.GetCustomAttribute<OptionAttribute>();

            parameters.Add(new ParameterMetadata(
                property.Name,
                JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                option?.Name ?? CommandValues.ToKebabCase(property.Name),
                property.PropertyType,
                argument?.Description ?? option?.Description ?? string.Empty,
                argument?.Position ?? -1,
                !parameter.HasDefaultValue,
                parameter.HasDefaultValue ? parameter.DefaultValue : null));
        }

        return new CommandMetadata(
            name,
            halves[0],
            halves.Length == 2 ? halves[1] : string.Empty,
            type,
            description,
            isQuery,
            undoable,
            notUndoableReason,
            resultType,
            parameters.ToImmutable());
    }

    private static Type? ResultTypeOf(Type type)
    {
        foreach (Type contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IQuery<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static string FriendlyTypeName(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type).Name;

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
