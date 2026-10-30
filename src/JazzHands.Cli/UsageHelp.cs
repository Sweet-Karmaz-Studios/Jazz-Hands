using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace JazzHands.Cli;

/// <summary>
/// <c>--help</c> as System.CommandLine writes it, with a subcommand's usage line naming its parents
/// without their arguments.
/// </summary>
/// <remarks>
/// <c>export</c> and <c>trim</c> are verbs of their own with arguments as well as parents, and the
/// stock help put those arguments into every child's usage: <c>jazz export &lt;project&gt; enqueue
/// &lt;project&gt; &lt;output&gt;</c>, which is not a command line that works (QA, 2026-10-29). The
/// help is written to a buffer, the usage line has each parent's arguments taken out, and the rest
/// goes out as it was. 2.0's HelpBuilder is not public, so this is the place to do it.
/// </remarks>
internal sealed class UsageHelp : SynchronousCommandLineAction
{
    private readonly HelpAction _inner;

    private UsageHelp(HelpAction inner) => _inner = inner;

    /// <inheritdoc />
    public override bool ClearsParseErrors => _inner.ClearsParseErrors;

    /// <summary>Puts this in front of the root's help option, which every subcommand shares.</summary>
    public static void Install(RootCommand root)
    {
        foreach (HelpOption help in root.Options.OfType<HelpOption>())
        {
            if (help.Action is HelpAction inner)
            {
                help.Action = new UsageHelp(inner);
            }
        }
    }

    /// <inheritdoc />
    public override int Invoke(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        TextWriter output = parseResult.InvocationConfiguration.Output;
        using var buffer = new StringWriter();
        parseResult.InvocationConfiguration.Output = buffer;
        int code;
        try
        {
            code = _inner.Invoke(parseResult);
        }
        finally
        {
            parseResult.InvocationConfiguration.Output = output;
        }

        output.Write(WithoutParentArguments(buffer.ToString(), Path(parseResult)));
        return code;
    }

    /// <summary>The names from the root to the command the help is for.</summary>
    internal static string[] Path(ParseResult parseResult)
    {
        var names = new List<string>();
        for (SymbolResult? result = parseResult.CommandResult; result is not null; result = result.Parent)
        {
            if (result is CommandResult command)
            {
                names.Insert(0, command.Command.Name);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The help with the line after <c>Usage:</c> keeping each command name on <paramref name="path"/>
    /// and, of the words after them, only those after the last.
    /// </summary>
    internal static string WithoutParentArguments(string help, IReadOnlyList<string> path)
    {
        string[] lines = help.Split('\n');
        int usage = Array.FindIndex(lines, line => line.TrimEnd('\r') == "Usage:");
        if (usage < 0 || usage + 1 >= lines.Length || path.Count < 3)
        {
            return help;
        }

        string line = lines[usage + 1];
        string ending = line.EndsWith('\r') ? "\r" : string.Empty;
        string indent = line[..(line.Length - line.TrimStart().Length)];
        string[] words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var kept = new List<string>();
        int next = 0;
        bool skipping = false;
        foreach (string word in words)
        {
            if (next < path.Count && word == path[next])
            {
                kept.Add(word);
                next++;
                skipping = next < path.Count;
            }
            else if (!skipping)
            {
                kept.Add(word);
            }
        }

        // Every name found in order, or the line was not what this expects and is left alone.
        if (next < path.Count)
        {
            return help;
        }

        lines[usage + 1] = indent + string.Join(' ', kept) + ending;
        return string.Join('\n', lines);
    }
}
