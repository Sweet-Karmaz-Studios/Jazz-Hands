using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using System.Text;

namespace JazzHands.Cli;

/// <summary>
/// <c>jazz docs --markdown</c>: writes <c>Docs/CLI.md</c> from the command tree itself, so the
/// reference cannot fall behind the commands.
/// </summary>
/// <remarks>
/// Every verb, its arguments and its options, with their descriptions and defaults, come from the
/// same <see cref="RootCommand"/> that parses a command line, which the registry builds for the
/// generated verbs. What a table cannot say lives in <c>Docs/cli</c>: <c>intro.md</c> at the top,
/// a note per verb in <c>notes/</c> (named for the verb's path, <c>trim.md</c>, <c>perf.decode.md</c>)
/// placed under its entry, and <c>guides.md</c> at the end. They are compiled into jazz.exe, so the
/// command works from anywhere; a test checks the checked-in file is what it writes.
/// </remarks>
public static class CliDocs
{
    /// <summary>Builds the verb.</summary>
    public static Command Build()
    {
        var markdown = new Option<bool>("--markdown") { Description = "Write the reference as Markdown. The only form there is, and the default." };
        var output = new Option<string?>("--out") { Description = "Write it to this file, as UTF-8, instead of to the console: Docs/CLI.md." };
        var command = new Command("docs", "Write the command reference, Docs/CLI.md, from the commands themselves.") { markdown, output };

        command.SetAction(parse =>
        {
            string text = Markdown(JazzCli.BuildRootCommand());
            if (parse.GetValue(output) is { Length: > 0 } file)
            {
                File.WriteAllText(Path.GetFullPath(file), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Console.Out.WriteLine($"Wrote {Path.GetFullPath(file)}.");
            }
            else
            {
                Console.Out.Write(text);
            }

            return ExitCode.Ok;
        });

        return command;
    }

    /// <summary>The whole reference.</summary>
    public static string Markdown(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var text = new StringBuilder();
        text.Append(Doc("intro").TrimEnd()).Append("\n\n");

        text.Append("## Global options\n\n");
        text.Append("| Option | Meaning |\n|---|---|\n");
        foreach (Option option in root.Options.Where(option => option.Recursive || option is HelpOption or VersionOption))
        {
            text.Append(Row(option)).Append('\n');
        }

        text.Append('\n').Append(Doc("exit-codes").TrimEnd()).Append("\n\n");

        Command[] verbs = [.. root.Subcommands.Where(command => command.Subcommands.Count == 0)];
        Command[] groups = [.. root.Subcommands.Where(command => command.Subcommands.Count > 0).OrderBy(command => command.Name, StringComparer.Ordinal)];

        text.Append("## Verbs\n\n");
        foreach (Command verb in verbs)
        {
            Entry(text, [verb]);
        }

        foreach (Command group in groups)
        {
            // A group that is a verb too (trim, export) is described by its own entry.
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"## `jazz {group.Name}`\n\n");
            if (group.Action is null)
            {
                text.Append(Sentence(group.Description)).Append("\n\n");
            }
            else
            {
                Entry(text, [group]);
            }

            foreach (Command verb in group.Subcommands)
            {
                Walk(text, [group, verb]);
            }
        }

        text.Append(Doc("guides").TrimEnd()).Append('\n');
        return text.ToString();
    }

    private static void Walk(StringBuilder text, Command[] path)
    {
        if (path[^1].Action is not null)
        {
            Entry(text, path);
        }

        foreach (Command child in path[^1].Subcommands)
        {
            Walk(text, [.. path, child]);
        }
    }

    /// <summary>One verb: its usage, what it does, its arguments and options, and its note.</summary>
    private static void Entry(StringBuilder text, Command[] path)
    {
        Command command = path[^1];
        string name = string.Join(' ', path.Select(part => part.Name));
        string arguments = string.Concat(command.Arguments.Select(argument => (argument.Arity.MinimumNumberOfValues > 0 ? $" <{argument.Name}>" : $" [{argument.Name}]") + (argument.Arity.MaximumNumberOfValues > 1 ? "..." : string.Empty)));
        Option[] options = [.. command.Options.Where(option => !option.Recursive && option is not HelpOption and not VersionOption)];

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"### `jazz {name}{arguments}`\n\n");
        text.Append(Sentence(command.Description)).Append("\n\n");

        if (command.Arguments.Count > 0)
        {
            text.Append("| Argument | Meaning |\n|---|---|\n");
            foreach (Argument argument in command.Arguments)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"| `<{argument.Name}>` | {Cell(argument.Description)}{(argument.Arity.MinimumNumberOfValues == 0 ? " Optional." : string.Empty)} |\n");
            }

            text.Append('\n');
        }

        if (options.Length > 0)
        {
            text.Append("| Option | Meaning |\n|---|---|\n");
            foreach (Option option in options)
            {
                text.Append(Row(option)).Append('\n');
            }

            text.Append('\n');
        }

        string note = Note(string.Join('.', path.Select(part => part.Name)));
        if (note.Length > 0)
        {
            text.Append(note.TrimEnd()).Append("\n\n");
        }
    }

    private static string Row(Option option)
    {
        // A switch takes no value: a bool, or a generated option that may be given on its own.
        bool flag = option.ValueType == typeof(bool) || option.Arity.MaximumNumberOfValues == 0 || (option.HelpName is null && option.Arity.MinimumNumberOfValues == 0 && option.ValueType == typeof(string));
        string value = flag ? string.Empty : $" <{option.HelpName ?? option.Name.TrimStart('-')}>";
        string names = string.Join(", ", new[] { option.Name }.Concat(option.Aliases.Order(StringComparer.Ordinal)).Select(alias => $"`{alias}{value}`"));
        string meaning = Cell(option.Description);
        if (option.Required)
        {
            meaning = $"Required. {meaning}";
        }
        else if (option.HasDefaultValue && option.GetDefaultValue() is { } fallback and not false and not "" && !meaning.Contains("Default:", StringComparison.Ordinal))
        {
            meaning = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{meaning} Default: {fallback}.");
        }

        return $"| {names} | {meaning} |";
    }

    private static string Sentence(string? text) =>
        text is not { Length: > 0 } words ? string.Empty
        : words.EndsWith('.') || words.EndsWith('!') || words.EndsWith('?') ? words
        : words + ".";

    private static string Cell(string? text) => Sentence(text).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Doc(string name) => Resource($"doc:{name}") ?? string.Empty;

    private static string Note(string name) => Resource($"note:{name}") ?? string.Empty;

    private static string? Resource(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
