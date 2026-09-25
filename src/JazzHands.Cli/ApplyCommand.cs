using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>
/// <c>jazz apply</c>: runs a script of commands against a project in one session, and saves only
/// when every step worked.
/// </summary>
/// <remarks>
/// <para>
/// A script is a JSON array (comments and trailing commas allowed) of steps shaped as a command
/// is sent over JSON-RPC: <c>{ "command": "clip.add", "args": { ... } }</c>. Times may be written
/// as the command line takes them (<c>"00:00:04.000"</c>, <c>"90f"</c>, <c>"1.5s"</c>, ranges as
/// <c>"0:10-0:25"</c>), read at the active sequence's rate.
/// </para>
/// <para>
/// A step may name its result with <c>"as": "boss"</c>, and later steps use what it made: <c>$boss.id</c>
/// is the first id it changed, <c>$boss.ids[1]</c> the second; <c>$last</c> is the step before.
/// <c>${scriptDir}</c> and <c>${projectDir}</c> are the folders of the script and the project, and
/// <c>${env:NAME}</c> an environment variable, so a script can find its media wherever it is run.
/// </para>
/// <para>
/// Steps run one at a time so each can use what the one before made. The first that fails stops
/// the script and nothing is saved, so the file is as it was; <c>--continue</c> runs the rest and
/// saves what worked. <c>--dry-run</c> runs every step that only changes the project and saves
/// nothing, skipping those with effects outside it (exports, cache clearing). Queries may be steps
/// too; their answers are in the <c>--json</c> output.
/// </para>
/// </remarks>
public static partial class ApplyCommand
{
    private static readonly JsonSerializerOptions Text = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the verb: with a project headless, or the script alone with --attach.</summary>
    public static Command Build()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file to change." };
        var script = new Argument<string>("script") { Description = "The script: a JSON array of { \"command\", \"args\", \"as\" } steps." };
        var keepGoing = new Option<bool>("--continue") { Description = "Run every step even after one fails, and save what worked." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Run the steps that only change the project, report, and save nothing." };
        var noSave = new Option<bool>("--no-save") { Description = "Do not write the project back." };

        if (JazzCli.AttachTarget is not null)
        {
            var attached = new Command("apply", "Run a script of commands in the editor it is attached to, step by step; the editor keeps the result, and saving is the editor's.")
            {
                script, keepGoing,
            };
            attached.SetAction(parse => RpcCommands.Attached(parse.GetValue(JazzCli.JsonOption), client => RunAttached(client, parse.GetValue(script)!, parse.GetValue(keepGoing), parse.GetValue(JazzCli.JsonOption))));
            return attached;
        }

        var command = new Command("apply", "Run a script of commands against a project in one session; save only if every step worked.")
        {
            project, script, keepGoing, dryRun, noSave,
        };

        command.SetAction(parse => Run(
            parse.GetValue(project)!,
            parse.GetValue(script)!,
            parse.GetValue(keepGoing),
            parse.GetValue(dryRun),
            parse.GetValue(noSave),
            parse.GetValue(JazzCli.JsonOption)).GetAwaiter().GetResult());

        return command;
    }

    /// <summary>Runs a script; the exit code says whether every step worked.</summary>
    public static async Task<int> Run(string projectPath, string scriptPath, bool keepGoing, bool dryRun, bool noSave, bool json)
    {
        string path = Path.GetFullPath(projectPath);
        string scriptFull = Path.GetFullPath(scriptPath);

        JsonArray steps;
        ProjectLoad load;
        try
        {
            steps = ReadScript(scriptFull);
            load = ProjectFile.Load(path);
            if (!load.IsLoadable)
            {
                throw new CommandException("project-invalid", $"'{path}' has errors and will not open. Run 'jazz validate' to see them.");
            }
        }
        catch (CommandException error)
        {
            Report(error, json);
            return ExitCode.CommandError;
        }
        catch (ProjectFileException error)
        {
            Report(new CommandException("cannot-open", error.Message), json);
            return ExitCode.CommandError;
        }

        await using ServiceProvider services = new ServiceCollection()
            .AddJazzHandsEngine()
            .AddSingleton<IExportService>(new ForegroundExportService())
            .BuildServiceProvider();
        await using var session = new Session(load.Project, services, path) { DefaultIssuer = "cli" };

        async Task<Outcome> Local(CommandMetadata metadata, object built)
        {
            if (metadata.IsQuery)
            {
                object? answer = typeof(Session).GetMethod(nameof(Session.Query))!.MakeGenericMethod(metadata.ResultType!).Invoke(session, [built]);
                return new Outcome(true, [], JsonSerializer.SerializeToNode(answer, JazzJson.Options), null, null);
            }

            CommandResult result = await session.ExecuteAsync((ICommand)built).ConfigureAwait(false);
            return new Outcome(result.Ok, [.. result.ChangedIds], null, result.Code, result.Error);
        }

        Task<Rational> Rate()
        {
            Project project = session.Project;
            return Task.FromResult(project.ActiveSequence is { } active ? project.SettingsFor(active).FrameRate : project.Settings.FrameRate);
        }

        var folders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scriptDir"] = Path.GetDirectoryName(scriptFull) ?? ".",
            ["projectDir"] = Path.GetDirectoryName(path) ?? ".",
        };
        (List<StepResult> results, bool failed) = await RunSteps(steps, Local, Rate, folders, keepGoing, dryRun, json).ConfigureAwait(false);

        bool save = !dryRun && !noSave && (!failed || keepGoing) && results.Any(result => result.Ok && result.Changed.Length > 0);
        if (save)
        {
            ProjectFile.Save(path, session.Project, load.Unknown);
        }

        int ran = results.Count(result => result.Ok);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { ok = !failed, steps = results.Select(result => result.ToJson()), saved = save, dryRun },
                Text));
        }
        else
        {
            string ending = save ? $", saved {path}" : dryRun ? ", dry run: nothing saved" : failed && !keepGoing ? ", nothing saved" : string.Empty;
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ran} of {steps.Count} step(s) worked{ending}."));
        }

        return failed ? ExitCode.CommandError : ExitCode.Ok;
    }

    /// <summary>
    /// Runs a script in the editor a client is attached to. Each step is a command the editor
    /// runs and remembers, so they undo there one by one; a step that fails stops the rest, and
    /// what ran before it stays, as it would have had a person done it.
    /// </summary>
    private static async Task<int> RunAttached(Control.JazzClient client, string scriptPath, bool keepGoing, bool json)
    {
        string scriptFull = Path.GetFullPath(scriptPath);
        JsonArray steps = ReadScript(scriptFull);
        JsonNode? info = await client.CallAsync("session.info").ConfigureAwait(false);
        string projectPath = info?["path"]?.GetValue<string>() ?? string.Empty;

        async Task<Outcome> Remote(CommandMetadata metadata, object built)
        {
            JsonNode? result = await client.CallCommandAsync(metadata.Name, CommandRegistry.ArgsToJson(built)).ConfigureAwait(false);
            return metadata.IsQuery
                ? new Outcome(true, [], result?["data"]?.DeepClone(), null, null)
                : new Outcome(true, [.. (result?["changedIds"] as JsonArray ?? []).Select(id => id!.GetValue<string>())], null, null, null);
        }

        var folders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scriptDir"] = Path.GetDirectoryName(scriptFull) ?? ".",
            ["projectDir"] = projectPath.Length > 0 ? Path.GetDirectoryName(projectPath) ?? "." : Directory.GetCurrentDirectory(),
        };
        (List<StepResult> results, bool failed) = await RunSteps(steps, Remote, () => RpcCommands.FrameRateAsync(client), folders, keepGoing, dryRun: false, json).ConfigureAwait(false);

        int ran = results.Count(result => result.Ok);
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = !failed, steps = results.Select(result => result.ToJson()), attached = client.Issuer }, Text));
        }
        else
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ran} of {steps.Count} step(s) worked in the editor."));
        }

        return failed ? ExitCode.CommandError : ExitCode.Ok;
    }

    private static async Task<(List<StepResult> Results, bool Failed)> RunSteps(
        JsonArray steps,
        Func<CommandMetadata, object, Task<Outcome>> execute,
        Func<Task<Rational>> rate,
        Dictionary<string, string> folders,
        bool keepGoing,
        bool dryRun,
        bool json)
    {
        var named = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var results = new List<StepResult>();
        bool failed = false;

        for (int index = 0; index < steps.Count; index++)
        {
            StepResult result = await RunStep(execute, rate, steps[index], index + 1, named, folders, dryRun).ConfigureAwait(false);
            results.Add(result);
            if (!json)
            {
                Console.Out.WriteLine(result.Describe());
            }

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

    /// <summary>Reads a script: a JSON array, comments and trailing commas allowed.</summary>
    internal static JsonArray ReadScript(string path)
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

    private static async Task<StepResult> RunStep(
        Func<CommandMetadata, object, Task<Outcome>> execute,
        Func<Task<Rational>> rate,
        JsonNode? node,
        int index,
        Dictionary<string, string[]> named,
        Dictionary<string, string> folders,
        bool dryRun)
    {
        string name = node?["command"]?.GetValue<string>() ?? "?";
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
                return new StepResult(index, name, true, [], null, null, null, Skipped: true);
            }

            Outcome outcome = await execute(metadata, built).ConfigureAwait(false);
            if (!outcome.Ok)
            {
                return new StepResult(index, name, false, [], null, outcome.Code, outcome.Error);
            }

            Remember(step, named, outcome.Changed);
            return new StepResult(index, name, true, outcome.Changed, outcome.Answer, null, null);
        }
        catch (CommandException error)
        {
            return new StepResult(index, name, false, [], null, error.Code, error.Message);
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is CommandException inner)
        {
            return new StepResult(index, name, false, [], null, inner.Code, inner.Message);
        }
    }

    /// <summary>What a step made, as $last and under its "as" name.</summary>
    private static void Remember(JsonObject step, Dictionary<string, string[]> named, string[] changed)
    {
        named["last"] = changed;
        if (step["as"]?.GetValue<string>() is { Length: > 0 } alias)
        {
            named[alias] = changed;
        }
    }

    /// <summary>Replaces the script's variables in every string of the arguments.</summary>
    internal static JsonObject Substitute(JsonObject args, IReadOnlyDictionary<string, string[]> named, IReadOnlyDictionary<string, string> folders)
    {
        foreach ((string key, JsonNode? value) in args.ToArray())
        {
            args[key] = SubstituteNode(value, named, folders);
        }

        return args;
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
                : throw new CommandException("unknown-variable", $"'${{{variable}}}' is not a script variable: ${{scriptDir}}, ${{projectDir}} and ${{env:NAME}} are.");
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

    private static void Report(CommandException error, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, code = error.Code, error = error.Message }, Text));
        }
        else
        {
            Console.Error.WriteLine($"jazz: {error.Code}: {error.Message}");
        }
    }

    [GeneratedRegex(@"\$\{(?<var>[^}]+)\}")]
    private static partial Regex FolderPattern();

    [GeneratedRegex(@"\$(?<name>[A-Za-z_][A-Za-z0-9_-]*)\.ids?(\[(?<n>\d+)\])?")]
    private static partial Regex ResultPattern();

    /// <summary>What running a step came to, locally or in the editor.</summary>
    private sealed record Outcome(bool Ok, string[] Changed, JsonNode? Answer, string? Code, string? Error);

    /// <summary>What one step did.</summary>
    private sealed record StepResult(int Index, string Command, bool Ok, string[] Changed, JsonNode? Answer, string? Code, string? Error, bool Skipped = false)
    {
        public string Describe() =>
            !Ok ? $"{Index} {Command}: {Code}: {Error}"
            : Skipped ? $"{Index} {Command}: skipped in a dry run, it acts outside the project"
            : Answer is not null ? $"{Index} {Command}: answered"
            : string.Create(CultureInfo.InvariantCulture, $"{Index} {Command}: {Changed.Length} changed{(Changed.Length > 0 ? $" ({string.Join(", ", Changed.Take(3))}{(Changed.Length > 3 ? ", ..." : string.Empty)})" : string.Empty)}");

        public object ToJson() => new { index = Index, command = Command, ok = Ok, skipped = Skipped, changed = Changed, answer = Answer, code = Code, error = Error };
    }
}
