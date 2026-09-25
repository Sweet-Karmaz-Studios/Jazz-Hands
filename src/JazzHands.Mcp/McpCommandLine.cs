namespace JazzHands.Mcp;

/// <summary>jazz-mcp.exe's arguments, the same as <c>jazz mcp</c>'s.</summary>
public static class McpCommandLine
{
    /// <summary>
    /// Reads <c>--attach [target]</c>, <c>--project &lt;path&gt;</c> and <c>--save-on-exit</c>.
    /// Neither --attach nor --project attaches to the newest editor, which is what a person
    /// running Claude Code beside the editor means.
    /// </summary>
    public static McpHostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool attach = false;
        string? target = null;
        string? project = null;
        bool save = false;

        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--attach":
                    attach = true;
                    if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        target = args[++index];
                    }

                    break;
                case "--project":
                    project = index + 1 < args.Count ? args[++index] : throw new ArgumentException("--project needs a .jazz path.");
                    break;
                case "--save-on-exit":
                    save = true;
                    break;
                case "--json" or "--list-tools":
                    break;
                default:
                    throw new ArgumentException($"'{args[index]}' is not an option: --attach [target], --project <path>, --save-on-exit, --list-tools.");
            }
        }

        if (attach && project is not null)
        {
            throw new ArgumentException("Say --attach or --project, not both: attached, the editor has the project open already.");
        }

        return new McpHostOptions(attach || project is null, target, project, save);
    }
}
