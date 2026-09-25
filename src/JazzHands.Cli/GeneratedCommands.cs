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
                    ?? new Command(metadata.Area, AreaDescription(metadata.Area));

                if (!root.Subcommands.Contains(area))
                {
                    root.Subcommands.Add(area);
                }

                areas[metadata.Area] = area;
            }

            area.Subcommands.Add(verb);
        }
    }

    /// <summary>What each area of verbs is for, as <c>jazz --help</c> lists it.</summary>
    public static string AreaDescription(string area) => area switch
    {
        "audio" => "Sound on clips: gain, pan, fades, channel maps, muting a stream, detaching sound; the master volume and limiter; the meters.",
        "cache" => "The thumbnail, waveform and proxy cache: what it holds, its size limit, emptying it.",
        "chapter" => "Chapters: markers that become a file's chapters, from markers or imported from media.",
        "clip" => "Clips on the timeline: add, move, trim, split, ripple, roll, slip, slide, speed, freeze and delete.",
        "clipboard" => "Clips as JSON, to paste with 'clip paste' here or in another project.",
        "color" => "Colour: sample the picture's colour at a point. Tone mapping is on clips and the project.",
        "diagnostics" => "What the session has noticed: fallbacks and missing files.",
        "effect" => "Effects on clips and tracks: add, set parameters, bypass, reorder, presets. 'effect list' names every type.",
        "export" => "Exports: plan, queue in a running editor, stills, contact sheets and batches. 'jazz export <project>' exports now.",
        "fonts" => "The font families titles can use.",
        "history" => "The session's undo history.",
        "keyframe" => "Keyframes on any animatable parameter: add, move, set value, interpolation and handles, remove.",
        "marker" => "Markers on a sequence or a clip: points and ranges, names, colours, chapters.",
        "mask" => "Masks on clips and effects: shapes, paths, feather, expansion, how they combine.",
        "media" => "Media files: import, probe, relink, reprobe, set conform options, remove.",
        "param" => "Any parameter by its address, on a clip, a track, an effect or a mask.",
        "playback" => "The transport of a running editor: play, pause, seek, loop, rate.",
        "presets" => "Export presets, built in and your own. Need no project.",
        "project" => "The project: settings, default tone mapping, a summary.",
        "proxy" => "Proxies: half size copies of heavy media for smooth editing.",
        "scopes" => "Waveform, vectorscope and histogram measurements of the picture.",
        "selection" => "What is selected in a running editor.",
        "sequence" => "Sequences: create, rename, set active, their own settings.",
        "subtitle" => "Subtitles: import and export SRT, VTT and ASS; cues, their text, times and place; styles.",
        "timeline" => "The timeline as a whole: describe it, magnetic mode. In and out points are in playback.",
        "title" => "Titles: add from presets, set text, font, look and animation, measure.",
        "track" => "Tracks: add, remove, rename, reorder, lock, mute, solo, sync lock, volume and pan.",
        "transition" => "Transitions on cuts: add, change, remove, and the default ones.",
        "trim" => "Quick Trim: keep and cut stretches of one recording. 'jazz trim <file>' does it in one line.",
        _ => $"Commands and queries about {area}.",
    };

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
        var optionalProject = new Option<string?>("--project") { Description = "A .jazz file to read alongside, for its fonts folder and relative paths." };

        var verb = new Command(
            metadata.Verb.Length > 0 ? metadata.Verb : metadata.Area,
            metadata.Description);

        // A standalone command works on the editor's own settings or on a file, not a project, so
        // there is no file to name and nothing to save; a project may still be given for what it
        // adds, such as its own fonts.
        if (!metadata.Standalone)
        {
            verb.Arguments.Add(project);
        }
        else
        {
            verb.Options.Add(optionalProject);
        }

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
                Description = WithDefault(parameter),
                HelpName = IsSwitch(parameter) ? null : Placeholder(parameter),
                Required = parameter.IsRequired,

                // A switch on its own means true: --auto rather than --auto true, which still works.
                Arity = IsSwitch(parameter) ? ArgumentArity.ZeroOrOne : ArgumentArity.ExactlyOne,
            };

            options[parameter.CliName] = option;
            verb.Options.Add(option);
        }

        if (!metadata.IsQuery && !metadata.Standalone)
        {
            verb.Options.Add(noSave);
        }

        verb.SetAction(parse => Run(metadata, parse, project, arguments, options, noSave, optionalProject));

        return verb;
    }

    /// <summary>
    /// A file or files a command reads (a parameter called Path, Paths or File), made absolute
    /// against the current folder when that is where it is. Anything else, and a path that is only
    /// there relative to the project, is left for the command to resolve as it always does.
    /// </summary>
    internal static string? InputPath(ParameterMetadata parameter, string? text)
    {
        if (text is null || parameter.Name is not ("Path" or "Paths" or "File"))
        {
            return text;
        }

        static string Absolute(string entry)
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0 || System.IO.Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            // A glob is found by its folder.
            string probe = trimmed.IndexOfAny(['*', '?']) is var wild and >= 0
                ? System.IO.Path.GetDirectoryName(trimmed[..wild]) is { Length: > 0 } folder ? folder : "."
                : trimmed;
            return File.Exists(probe) || Directory.Exists(probe) ? System.IO.Path.GetFullPath(trimmed) : trimmed;
        }

        return parameter.Name == "Paths"
            ? string.Join(",", text.Split(',').Select(Absolute))
            : Absolute(text);
    }

    /// <summary>An option's description, and its default when leaving it out means something.</summary>
    internal static string WithDefault(ParameterMetadata parameter)
    {
        string description = parameter.Description.TrimEnd('.');
        string shown = parameter.IsRequired || parameter.DefaultValue is null or false or "" ? string.Empty : CommandValues.Format(parameter.DefaultValue, Rational.Fps30);
        return shown.Length == 0 ? description + "." : $"{description}. Default: {shown}.";
    }

    /// <summary>What an option or argument takes, as help shows it: time, rate, size, n, or the choices.</summary>
    internal static string Placeholder(ParameterMetadata parameter)
    {
        Type type = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
        return type switch
        {
            _ when type == typeof(Flicks) => "time",
            _ when type == typeof(Rational) => "rate",
            _ when type == typeof(FrameSize) => "size",
            _ when type == typeof(TimeRange) => "range",
            _ when type == typeof(EquatableArray<TimeRange>) => "ranges",
            _ when type == typeof(int) || type == typeof(long) => "n",
            _ when type == typeof(double) || type == typeof(float) => "number",
            _ when type.IsEnum => string.Join("|", Enum.GetNames(type).Select(JsonNamingPolicy.KebabCaseLower.ConvertName)),
            _ when type == typeof(EquatableArray<string>) || type == typeof(string[]) => "list",
            _ when parameter.Name.EndsWith("Id", StringComparison.Ordinal) => "id",
            _ when parameter.Name.EndsWith("Ids", StringComparison.Ordinal) => "ids",
            _ => parameter.CliName,
        };
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
        Option<bool> noSave,
        Option<string?> optionalProject)
    {
        bool json = parse.GetValue(JazzCli.JsonOption);
        string path = metadata.Standalone ? parse.GetValue(optionalProject) ?? string.Empty : parse.GetValue(projectArgument)!;

        try
        {
            ProjectLoad load = path.Length == 0
                ? new ProjectLoad(Project.CreateNew("standalone"), string.Empty, UnknownFields.None, [], [])
                : ProjectFile.Load(path);

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

            // A file typed in a shell is where the shell is, not where the project is.
            ParameterMetadata[] positional = [.. metadata.Arguments];
            object built = CommandRegistry.FromCommandLine(
                metadata.Name,
                [.. arguments.Select((argument, index) => InputPath(positional[index], parse.GetValue(argument))).Where(value => value is not null)!],
                options.ToDictionary(pair => pair.Key, pair => ValueOf(parse, pair.Value), StringComparer.Ordinal),
                frameRate);

            // A headless process has no queue to leave an export in, so one queued here runs now.
            using ServiceProvider services = new ServiceCollection()
                .AddJazzHandsEngine()
                .AddSingleton<Engine.Export.IExportService>(new Engine.Export.ForegroundExportService())
                .BuildServiceProvider();
            return Execute(metadata, built, load, path, services, json, !metadata.Standalone && !parse.GetValue(noSave)).GetAwaiter().GetResult();
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

        await using var session = new Session(load.Project, services, path.Length == 0 ? string.Empty : System.IO.Path.GetFullPath(path));

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
        if (!json)
        {
            return HumanText.Render(answer);
        }

        return answer is string text
            ? JsonSerializer.Serialize(new { text }, Text)
            : JsonSerializer.SerializeToNode(answer, JazzJson.Options)?.ToJsonString(Text) ?? "null";
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
