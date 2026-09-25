using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;

namespace JazzHands.Control;

/// <summary>What running one step came to, locally or in the editor.</summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Changed">The ids it changed, for <c>$last</c> and its <c>"as"</c> name.</param>
/// <param name="Answer">A query's answer.</param>
/// <param name="Code">Why not, as a code.</param>
/// <param name="Error">Why not, as a sentence.</param>
public sealed record ScriptOutcome(bool Ok, string[] Changed, JsonNode? Answer, string? Code, string? Error);

/// <summary>What one step of a script did.</summary>
public sealed record ScriptStepResult(int Index, string Command, bool Ok, string[] Changed, JsonNode? Answer, string? Code, string? Error, bool Skipped = false)
{
    /// <summary>The step as a line of text.</summary>
    public string Describe() =>
        !Ok ? $"{Index} {Command}: {Code}: {Error}"
        : Skipped ? $"{Index} {Command}: skipped in a dry run, it acts outside the project"
        : Answer is not null ? $"{Index} {Command}: answered"
        : string.Create(CultureInfo.InvariantCulture, $"{Index} {Command}: {Changed.Length} changed{(Changed.Length > 0 ? $" ({string.Join(", ", Changed.Take(3))}{(Changed.Length > 3 ? ", ..." : string.Empty)})" : string.Empty)}");

    /// <summary>The step as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["index"] = Index,
        ["command"] = Command,
        ["ok"] = Ok,
        ["skipped"] = Skipped,
        ["changed"] = new JsonArray([.. Changed.Select(id => (JsonNode?)id)]),
        ["answer"] = Answer?.DeepClone(),
        ["code"] = Code,
        ["error"] = Error,
    };
}

/// <summary>
/// Runs a script of steps, <c>{ "command", "args", "as" }</c>, one at a time: <c>jazz apply</c>'s
/// runner, shared with the MCP server's <c>apply_batch</c>.
/// </summary>
/// <remarks>
/// A step may name what it made with <c>"as": "boss"</c>, and later steps use it: <c>$boss.id</c>
/// is the first id it changed, <c>$boss.ids[1]</c> the second; <c>$last</c> is the step before.
/// <c>${scriptDir}</c>, <c>${projectDir}</c> (whatever folders the caller names) and
/// <c>${env:NAME}</c> are expanded in every string. Where each step runs is the caller's: an
/// executor gets the registry entry and the built command.
/// </remarks>
public static partial class ScriptRunner
{
    /// <summary>Runs the steps in order; the first failure stops them unless <paramref name="keepGoing"/>.</summary>
    /// <param name="steps">The script.</param>
    /// <param name="execute">Runs one built command or query.</param>
    /// <param name="rate">The frame rate timecode is read against, asked for each step.</param>
    /// <param name="folders">The <c>${name}</c> folders.</param>
    /// <param name="keepGoing">Run every step even after one fails.</param>
    /// <param name="dryRun">Skip steps that act outside the project.</param>
    /// <param name="onStep">Told of each step as it finishes.</param>
    public static async Task<(List<ScriptStepResult> Results, bool Failed)> RunAsync(
        JsonArray steps,
        Func<CommandMetadata, object, Task<ScriptOutcome>> execute,
        Func<Task<Rational>> rate,
        IReadOnlyDictionary<string, string> folders,
        bool keepGoing = false,
        bool dryRun = false,
        Action<ScriptStepResult>? onStep = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(folders);

        var named = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var results = new List<ScriptStepResult>();
        bool failed = false;

        for (int index = 0; index < steps.Count; index++)
        {
            ScriptStepResult result = await RunStepAsync(execute, rate, steps[index], index + 1, named, folders, dryRun).ConfigureAwait(false);
            results.Add(result);
            onStep?.Invoke(result);

            if (!result.Ok)
            {
                failed = true;
                if (!keepGoing)
                {
                    break;
                }
            }
        }

        return (results, failed);
    }

    /// <summary>Reads a script file: a JSON array, or an object with <c>"steps"</c>; comments and trailing commas allowed.</summary>
    public static JsonArray Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new CommandException("file-not-found", $"'{path}' does not exist.");
        }

        try
        {
            JsonNode? node = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node as JsonArray
                ?? (node?["steps"] as JsonArray)
                ?? throw new CommandException("invalid-script", $"'{path}' is not a JSON array of steps.");
        }
        catch (JsonException error)
        {
            throw new CommandException("invalid-script", $"'{path}' is not valid JSON: {error.Message}");
        }
    }

    /// <summary>
    /// True when a step names what it made or uses what an earlier one made, so the steps have to
    /// run one at a time rather than as one batch.
    /// </summary>
    public static bool UsesReferences(JsonArray steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Any(step => step is JsonObject { } found && (found.ContainsKey("as") || ResultPattern().IsMatch(found["args"]?.ToJsonString() ?? string.Empty)));
    }

    /// <summary>Replaces the script's variables in every string of the arguments.</summary>
    public static JsonObject Substitute(JsonObject args, IReadOnlyDictionary<string, string[]> named, IReadOnlyDictionary<string, string> folders)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach ((string key, JsonNode? value) in args.ToArray())
        {
            args[key] = SubstituteNode(value, named, folders);
        }

        return args;
    }

    private static async Task<ScriptStepResult> RunStepAsync(
        Func<CommandMetadata, object, Task<ScriptOutcome>> execute,
        Func<Task<Rational>> rate,
        JsonNode? node,
        int index,
        Dictionary<string, string[]> named,
        IReadOnlyDictionary<string, string> folders,
        bool dryRun)
    {
        string name = node?["command"] is JsonValue commandValue && commandValue.TryGetValue(out string? given) ? given : "?";
        try
        {
            if (node is not JsonObject step || node["command"] is null)
            {
                throw new CommandException("invalid-step", "A step is an object with a \"command\" and its \"args\".");
            }

            CommandMetadata metadata = CommandRegistry.Find(name)
                ?? throw new CommandException("unknown-command", $"There is no command '{name}'. 'jazz --help' lists them.");
            JsonObject args = Substitute(step["args"]?.DeepClone() as JsonObject ?? [], named, folders);
            object built = CommandRegistry.FromJson(name, args, await rate().ConfigureAwait(false));

            if (dryRun && !metadata.IsQuery && !metadata.Undoable)
            {
                Remember(step, named, []);
                return new ScriptStepResult(index, name, true, [], null, null, null, Skipped: true);
            }

            ScriptOutcome outcome = await execute(metadata, built).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                return new ScriptStepResult(index, name, false, [], null, outcome.Code, outcome.Error);
            }

            Remember(step, named, outcome.Changed);
            return new ScriptStepResult(index, name, true, outcome.Changed, outcome.Answer, null, null);
        }
        catch (CommandException error)
        {
            return new ScriptStepResult(index, name, false, [], null, error.Code, error.Message);
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is CommandException inner)
        {
            return new ScriptStepResult(index, name, false, [], null, inner.Code, inner.Message);
        }
    }

    /// <summary>What a step made, as $last and under its "as" name.</summary>
    private static void Remember(JsonObject step, Dictionary<string, string[]> named, string[] changed)
    {
        named["last"] = changed;
        if (step["as"] is JsonValue alias && alias.TryGetValue(out string? name) && name.Length > 0)
        {
            named[name] = changed;
        }
    }

    private static JsonNode? SubstituteNode(JsonNode? node, IReadOnlyDictionary<string, string[]> named, IReadOnlyDictionary<string, string> folders) => node switch
    {
        JsonObject nested => Substitute(nested, named, folders),
        JsonArray list => new JsonArray([.. list.Select(item => SubstituteNode(item?.DeepClone(), named, folders))]),
        JsonValue value when value.TryGetValue(out string? text) => JsonValue.Create(Expand(text, named, folders)),
        _ => node,
    };

    private static string Expand(string text, IReadOnlyDictionary<string, string[]> named, IReadOnlyDictionary<string, string> folders)
    {
        string expanded = FolderPattern().Replace(text, match =>
        {
            string variable = match.Groups["var"].Value;
            if (variable.StartsWith("env:", StringComparison.Ordinal))
            {
                return Environment.GetEnvironmentVariable(variable[4..])
                    ?? throw new CommandException("unknown-variable", $"The environment variable '{variable[4..]}' is not set.");
            }

            return folders.TryGetValue(variable, out string? folder)
                ? folder
                : throw new CommandException("unknown-variable", $"'${{{variable}}}' is not a script variable: {string.Join(", ", folders.Keys.Select(key => $"${{{key}}}"))} and ${{env:NAME}} are.");
        });

        return ResultPattern().Replace(expanded, match =>
        {
            string name = match.Groups["name"].Value;
            if (!named.TryGetValue(name, out string[]? ids))
            {
                throw new CommandException("unknown-variable", $"'${name}' names no earlier step. Give a step \"as\": \"{name}\" to name what it makes.");
            }

            int index = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
            return index < ids.Length
                ? ids[index]
                : throw new CommandException("unknown-variable", $"'{match.Value}' asks for id {index}, and that step changed {ids.Length}.");
        });
    }

    [GeneratedRegex(@"\$\{(?<var>[^}]+)\}")]
    private static partial Regex FolderPattern();

    [GeneratedRegex(@"\$(?<name>[A-Za-z_][A-Za-z0-9_-]*)\.ids?(\[(?<n>\d+)\])?")]
    private static partial Regex ResultPattern();
}
