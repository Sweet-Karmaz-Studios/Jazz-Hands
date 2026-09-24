using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>
/// Builds the jazz verbs from the command registry.
/// </summary>
/// <remarks>
/// Nothing here knows what any command is. Every verb, argument and option is read from
/// <see cref="CommandRegistry"/>, so a command written this afternoon is on the command line
/// this afternoon, spelled the same way it is spelled over JSON-RPC and to MCP. Writing the verbs
/// out by hand would guarantee that the three drifted, and remote control by Claude Code is a
/// headline feature rather than an afterthought.
///
/// The shape is <c>jazz &lt;area&gt; &lt;verb&gt; &lt;project.jazz&gt; [args] [options]</c>. The
/// project comes first because every headless invocation needs one; from Phase 25 a running
/// instance can be driven instead with <c>--attach</c>.
/// </remarks>
public static class GeneratedCommands
{
    /// <summary>Adds every registered command and query to the root command.</summary>
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var areas = new Dictionary<string, Command>(StringComparer.Ordinal);

        foreach (CommandMetadata metadata in CommandRegistry.All)
        {
            if (Skip(metadata))
            {
                continue;
            }

            Command verb = Build(metadata);

            if (metadata.Verb.Length == 0)
            {
                root.Subcommands.Add(verb);
                continue;
            }

            if (!areas.TryGetValue(metadata.Area, out Command? area))
            {
                // project, clip, track and the rest already exist as hand-written groups in a
                // couple of cases; reuse them rather than making a second one.
                area = root.Subcommands.FirstOrDefault(existing =>
                    string.Equals(existing.Name, metadata.Area, StringComparison.Ordinal))
                    ?? new Command(metadata.Area, $"Commands and queries about {metadata.Area}.");

                if (!root.Subcommands.Contains(area))
                {
                    root.Subcommands.Add(area);
                }

                areas[metadata.Area] = area;
            }

            area.Subcommands.Add(verb);
        }
    }

    /// <summary>
    /// The commands the generated CLI leaves alone.
    /// </summary>
    /// <remarks>
    /// A batch holds commands rather than values, so it arrives as a script rather than as
    /// something typed; <c>jazz apply</c> in Phase 24 is how. The project verbs that make, open
    /// and save a file are hand-written in <see cref="ProjectCommands"/>, because they are about
    /// which file is open rather than about editing one.
    /// </remarks>
    private static bool Skip(CommandMetadata metadata) =>
        metadata.Type == typeof(BatchCommand)
        || metadata.Name is "project.new" or "project.open" or "project.save";

    private static Command Build(CommandMetadata metadata)
    {
        var project = new Argument<string>("project") { Description = "The .jazz file to work on." };
        var noSave = new Option<bool>("--no-save") { Description = "Do not write the project back." };

        var verb = new Command(
            metadata.Verb.Length > 0 ? metadata.Verb : metadata.Area,
            metadata.Description)
        {
            project,
        };

        var arguments = new List<Argument<string>>();
        var options = new Dictionary<string, Option<string>>(StringComparer.Ordinal);

        foreach (ParameterMetadata parameter in metadata.Arguments)
        {
            var argument = new Argument<string>(parameter.CliName)
            {
                Description = parameter.Description,
                Arity = parameter.IsRequired ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne,
            };

            arguments.Add(argument);
            verb.Arguments.Add(argument);
        }

        foreach (ParameterMetadata parameter in metadata.Options)
        {
            var option = new Option<string>($"--{parameter.CliName}")
            {
                Description = parameter.Description,
                Required = parameter.IsRequired,

                // A switch on its own means true: --auto rather than --auto true, which still works.
                Arity = IsSwitch(parameter) ? ArgumentArity.ZeroOrOne : ArgumentArity.ExactlyOne,
            };

            options[parameter.CliName] = option;
            verb.Options.Add(option);
        }

        if (!metadata.IsQuery)
        {
            verb.Options.Add(noSave);
        }

        verb.SetAction(parse => Run(metadata, parse, project, arguments, options, noSave));

        return verb;
    }

    /// <summary>A true or false option, which may be given on its own.</summary>
    private static bool IsSwitch(ParameterMetadata parameter) =>
        (Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type) == typeof(bool);

    /// <summary>An option's text: what was typed after it, "true" for a switch given alone, or null when it was not given.</summary>
    private static string? ValueOf(System.CommandLine.ParseResult parse, Option<string> option) =>
        parse.GetValue(option) ?? (parse.GetResult(option) is not null && option.Arity.MinimumNumberOfValues == 0 ? "true" : null);

    private static int Run(
        CommandMetadata metadata,
        System.CommandLine.ParseResult parse,
        Argument<string> projectArgument,
        List<Argument<string>> arguments,
        Dictionary<string, Option<string>> options,
        Option<bool> noSave)
    {
        bool json = parse.GetValue(JazzCli.JsonOption);
        string path = parse.GetValue(projectArgument)!;

        try
        {
            ProjectLoad load = ProjectFile.Load(path);

            if (!load.IsLoadable)
            {
                throw new CommandException(
                    "project-invalid",
                    $"'{load.Path}' has errors and will not open. Run 'jazz validate' to see them.");
            }

            // Timecode is read against the sequence the command will act on, so 00:00:01:12 means
            // the same thing on the command line as it does everywhere else.
            Rational frameRate = load.Project.ActiveSequence is { } active
                ? load.Project.SettingsFor(active).FrameRate
                : load.Project.Settings.FrameRate;

            object built = CommandRegistry.FromCommandLine(
                metadata.Name,
                [.. arguments.Select(argument => parse.GetValue(argument)).Where(value => value is not null)!],
                options.ToDictionary(pair => pair.Key, pair => ValueOf(parse, pair.Value), StringComparer.Ordinal),
                frameRate);

            using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
            return Execute(metadata, built, load, path, services, json, !parse.GetValue(noSave)).GetAwaiter().GetResult();
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
    }

    private static async Task<int> Execute(
        CommandMetadata metadata,
        object built,
        ProjectLoad load,
        string path,
        ServiceProvider services,
        bool json,
        bool save)
    {
        // A headless invocation is one session, so its undo history is empty before the command
        // runs and gone after it. Saying that is better than "nothing to undo", which sounds like
        // the project has no history rather than like the request makes no sense here.
        if (built is UndoCommand or RedoCommand)
        {
            throw new CommandException(
                "needs-a-session",
                "Undo belongs to an open session, and a headless jazz invocation is a new one every time. "
                + "Use --attach to reach a running instance, or edit the .jazz file and save.");
        }

        await using var session = new Session(load.Project, services, System.IO.Path.GetFullPath(path));

        if (metadata.IsQuery)
        {
            object? answer = QueryReflection.Ask(session, built, metadata.ResultType!);
            Console.Out.WriteLine(Render(answer, json));
            return ExitCode.Ok;
        }

        CommandResult result = await session.ExecuteAsync((ICommand)built).ConfigureAwait(false);

        if (!result.Ok)
        {
            Report(new CommandException(result.Code!, result.Error!, result.Path ?? string.Empty), json);
            return ExitCode.CommandError;
        }

        if (save)
        {
            ProjectFile.Save(path, session.Project, load.Unknown);
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { ok = true, version = result.Version, changed = result.ChangedIds, saved = save },
                Text));
        }
        else
        {
            Console.Out.WriteLine(
                $"{metadata.Name}: {result.ChangedIds.Length} item(s) changed{(save ? $", saved {path}" : string.Empty)}");
        }

        return ExitCode.Ok;
    }

    private static string Render(object? answer, bool json)
    {
        if (answer is string text)
        {
            return json ? JsonSerializer.Serialize(new { text }, Text) : text;
        }

        // Everything else is a record or an array of them, which reads well enough as JSON either
        // way. Phase 24 gives the common ones a human form.
        return JsonSerializer.SerializeToNode(answer, JazzJson.Options)?.ToJsonString(Text) ?? "null";
    }

    private static void Report(CommandException error, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new { ok = false, code = error.Code, error = error.Message, path = error.Path },
                Text));
        }
        else
        {
            Console.Error.WriteLine($"jazz: {error.Code}: {error.Message}");
        }
    }

    private static readonly JsonSerializerOptions Text = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",

        // As in the project file: an apostrophe in a description reads as one, not as \u0027.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Calls the session's generic Query from a place that only has a Type.</summary>
    private static class QueryReflection
    {
        internal static object? Ask(Session session, object query, Type resultType) =>
            typeof(Session)
                .GetMethod(nameof(Session.Query))!
                .MakeGenericMethod(resultType)
                .Invoke(session, [query]);
    }
}
