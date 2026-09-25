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

        (List<string> arguments, Dictionary<string, string?> options) = Split(tokens.Skip(used));

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

        throw new CommandException("unknown-command", $"There is no '{tokens[0]}'. Type the start of a name and press Tab.");
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

    private static (List<string> Arguments, Dictionary<string, string?> Options) Split(IEnumerable<string> tokens)
    {
        var arguments = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        List<string> list = [.. tokens];

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
            else if (index + 1 < list.Count && !list[index + 1].StartsWith("--", StringComparison.Ordinal))
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
