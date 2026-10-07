using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Remote;

/// <summary>
/// Reads a line typed into the Command Console as a JSON-RPC method and its params.
/// </summary>
/// <remarks>
/// Three spellings, so what works elsewhere works here:
/// <list type="bullet">
/// <item><c>clip.split {"clipId": "01J...", "at": "00:00:02.000"}</c>: the method and its JSON, as a script step or an RPC call.</item>
/// <item><c>clip split 01J... --at 00:00:02.000</c>: the <c>jazz</c> command line without the project, with or without <c>jazz</c> in front.</item>
/// <item><c>session.lock --reason batch --seconds 10</c>: a built-in with <c>--name value</c> pairs.</item>
/// </list>
/// A command line is read by the registry exactly as the CLI reads it, timecode against the
/// active sequence's rate, then sent as its JSON.
/// </remarks>
public static class ConsoleInput
{
    /// <summary>Every method the console knows: registry names first, then the built-ins.</summary>
    public static IReadOnlyList<string> Methods { get; } =
        [.. CommandRegistry.All.Select(entry => entry.Name).Concat(ControlServer.BuiltIns.Select(entry => entry.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The same, as a person types them after <c>jazz</c>: <c>clip split</c>.</summary>
    public static IReadOnlyList<string> Verbs { get; } =
        [.. CommandRegistry.All.Select(entry => entry.Verb.Length > 0 ? $"{entry.Area} {entry.Verb}" : entry.Area).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Reads a line.</summary>
    /// <exception cref="CommandException">The line names nothing the console knows, or its arguments do not fit.</exception>
    public static (string Method, JsonObject? Params) Parse(string line, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(line);

        string text = line.Trim();
        if (text.StartsWith("jazz ", StringComparison.Ordinal))
        {
            text = text[5..].TrimStart();
        }

        if (text.Length == 0)
        {
            throw new CommandException("missing-argument", "Type a command: clip.list, or clip split <id> --at 2s.");
        }

        int space = text.IndexOfAny([' ', '\t']);
        string first = space < 0 ? text : text[..space];
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        if (rest.StartsWith('{'))
        {
            try
            {
                return (first, JsonNode.Parse(rest) as JsonObject ?? throw new CommandException("invalid-argument", "The params are an object: {\"name\": value}."));
            }
            catch (JsonException error)
            {
                throw new CommandException("invalid-argument", $"That is not JSON: {error.Message}");
            }
        }

        List<string> tokens = Tokenize(text);
        CommandMetadata? metadata = null;
        int used = 1;
        if (tokens.Count >= 2 && CommandRegistry.Find($"{tokens[0]}.{tokens[1]}") is { } paired)
        {
            metadata = paired;
            used = 2;
        }
        else if (CommandRegistry.Find(tokens[0]) is { } named)
        {
            metadata = named;
        }

        (List<string> arguments, Dictionary<string, string?> options) = Split(tokens.Skip(used), metadata);

        if (metadata is not null)
        {
            object built = CommandRegistry.FromCommandLine(metadata.Name, arguments, options, frameRate);
            return (metadata.Name, CommandRegistry.ArgsToJson(built, metadata));
        }

        if (ControlServer.BuiltIns.Any(entry => entry.Name == tokens[0]))
        {
            if (arguments.Count > 0)
            {
                throw new CommandException("too-many-arguments", $"'{tokens[0]}' takes --name value pairs, or its params as JSON.");
            }

            var parameters = new JsonObject();
            foreach ((string name, string? value) in options)
            {
                parameters[JsonNamingPolicy.CamelCase.ConvertName(Pascal(name))] = Literal(value ?? "true");
            }

            return (tokens[0], parameters.Count > 0 ? parameters : null);
        }

        if (Alias(tokens[0], arguments, options, frameRate) is { } alias)
        {
            CommandMetadata aliased = CommandRegistry.Require(alias.Method);
            object built = CommandRegistry.FromCommandLine(aliased.Name, alias.Arguments, alias.Options, frameRate);
            return (aliased.Name, CommandRegistry.ArgsToJson(built, aliased));
        }

        throw new CommandException("unknown-command", $"There is no '{tokens[0]}'. Type the start of a name and press Tab.");
    }

    /// <summary>
    /// jazz.exe's own verbs, which are not registry commands, read as the command each comes down
    /// to on the open project, so a line that works in a shell works here: <c>frame</c> and
    /// <c>contact-sheet</c> write through <c>export.still</c> and <c>export.contact-sheet</c>, and
    /// <c>proof</c> and a foreground <c>export</c> are queued. The verbs that work on files rather
    /// than the open project say what to type instead; anything else is not one of them.
    /// </summary>
    private static (string Method, List<string> Arguments, Dictionary<string, string?> Options)? Alias(
        string verb,
        List<string> arguments,
        Dictionary<string, string?> options,
        Rational frameRate)
    {
        string? Take(string name)
        {
            options.Remove(name, out string? value);
            return value;
        }

        string Output() => Take("out") ?? throw new CommandException("missing-argument", $"'{verb}' needs --out, the file to write.");

        void Range()
        {
            if (Take("range") is { Length: > 0 } text)
            {
                var span = (TimeRange)CommandValues.Parse(typeof(TimeRange), text, frameRate, "range")!;
                options["start"] = $"{span.Start.ToFrames(frameRate, RoundingMode.Floor)}f";
                options["end"] = $"{span.End.ToFrames(frameRate, RoundingMode.Floor)}f";
            }
        }

        (string, List<string>, Dictionary<string, string?>) As(string method, string output) => (method, [output], options);

        if (verb is "frame" or "contact-sheet" or "proof" or "export" && arguments.Count > 0)
        {
            throw new CommandException("too-many-arguments", $"In the console '{verb}' works on the open project: leave the project file out.");
        }

        bool sheet = verb == "frame" && Take("sheet") is { } given && given != "false";
        switch (verb)
        {
            case "frame" when options.ContainsKey("node"):
                throw new CommandException("not-in-console", "--node draws to a file in jazz.exe only; the Nodes panel's viewer shows any node here.");
            case "frame" when sheet:
            {
                string output = Output();
                string times = Take("times") ?? "0,0.25,0.5,0.75,1";
                options["times"] = times;
                options["columns"] = Math.Min(times.Split(',', StringSplitOptions.RemoveEmptyEntries).Length, 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Take("size") is { Length: > 0 } size)
                {
                    options["width"] = ((FrameSize)CommandValues.Parse(typeof(FrameSize), size, frameRate, "size")!).Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return As("export.contact-sheet", output);
            }

            case "frame":
                return As("export.still", Output());
            case "contact-sheet":
            {
                string output = Output();
                if (Take("cols") is { } columns)
                {
                    options["columns"] = columns;
                }

                Range();
                return As("export.contact-sheet", output);
            }

            case "proof":
            {
                string output = Output();
                options["preset"] = "proof";
                options["mode"] = "encode";
                Range();
                return As("export.enqueue", output);
            }

            case "export":
            {
                if (options.ContainsKey("dry-run"))
                {
                    throw new CommandException("not-in-console", "A dry run is jazz.exe's: run jazz export --dry-run in a shell to see the plan.");
                }

                string output = Output();
                if (Take("no-chapters") is not null and not "false")
                {
                    options["chapters"] = "false";
                }

                return As("export.enqueue", output);
            }

            case "apply":
                throw new CommandException("not-in-console", "A script runs from a shell: jazz --attach apply <script.json> runs it here, step by step. One step at a time works in the console too.");
            case "frames":
                throw new CommandException("not-in-console", "frames writes a file per frame, from a shell. Here, contact-sheet --out <file.png> shows a whole sequence at once, and frame --at <time> --out <file.png> one frame.");
            case "trim" or "new" or "validate" or "fmt" or "repair" or "ids" or "serve" or "mcp" or "perf" or "rpc" or "docs" or "version":
                throw new CommandException("not-in-console", $"'{verb}' is jazz.exe's own and works on files rather than the open project: run jazz {verb} in a shell.");
            default:
                return null;
        }
    }

    /// <summary>The parameters of a method that a line has not given yet, as <c>--options</c>.</summary>
    public static IEnumerable<string> OptionsLeft(string method, string line)
    {
        if (CommandRegistry.Find(method) is not { } metadata)
        {
            return [];
        }

        HashSet<string> given = [.. Tokenize(line).Where(token => token.StartsWith("--", StringComparison.Ordinal)).Select(token => token.Split('=')[0])];
        return metadata.Parameters.Where(parameter => !parameter.IsPositional).Select(parameter => $"--{parameter.CliName}").Where(option => !given.Contains(option));
    }

    /// <summary>
    /// The method a line has got as far as naming, if it names one completely: the registry name
    /// for <c>clip split</c> or <c>clip.split</c>, the built-in for <c>session.info</c>.
    /// </summary>
    public static string? MethodOf(string line)
    {
        List<string> tokens = Tokenize(line.Trim().StartsWith("jazz ", StringComparison.Ordinal) ? line.Trim()[5..] : line);
        if (tokens.Count == 0)
        {
            return null;
        }

        if (tokens.Count >= 2 && CommandRegistry.Find($"{tokens[0]}.{tokens[1]}") is { } paired)
        {
            return paired.Name;
        }

        return CommandRegistry.Find(tokens[0])?.Name ?? (Methods.Contains(tokens[0]) ? tokens[0] : null);
    }

    /// <summary>Splits a line at spaces, keeping "quoted text" whole.</summary>
    public static List<string> Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool any = false;

        foreach (char character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (any)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(character);
                any = true;
            }
        }

        if (any)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static (List<string> Arguments, Dictionary<string, string?> Options) Split(IEnumerable<string> tokens, CommandMetadata? metadata)
    {
        var arguments = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        List<string> list = [.. tokens];

        // A command's switches take the next word only when it is true or false, as jazz.exe reads
        // them, so a positional after one (--ripple <clip-id>) stays a positional.
        HashSet<string> switches = metadata is null
            ? []
            : [.. metadata.Options.Where(parameter => (Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type) == typeof(bool)).Select(parameter => parameter.CliName)];

        for (int index = 0; index < list.Count; index++)
        {
            string token = list[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                arguments.Add(token);
                continue;
            }

            string name = token[2..];
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                options[name[..equals]] = name[(equals + 1)..];
            }
            else if (index + 1 < list.Count
                && !list[index + 1].StartsWith("--", StringComparison.Ordinal)
                && (!switches.Contains(name) || bool.TryParse(list[index + 1], out _)))
            {
                options[name] = list[++index];
            }
            else
            {
                options[name] = "true";
            }
        }

        return (arguments, options);
    }

    private static string Pascal(string kebab) =>
        string.Concat(kebab.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static JsonNode? Literal(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }
}
