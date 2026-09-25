using System.CommandLine;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Mcp;

namespace JazzHands.Cli;

/// <summary>
/// <c>jazz mcp</c>: the Model Context Protocol server on stdin and stdout, for Claude Code.
/// </summary>
/// <remarks>
/// <c>jazz mcp --attach</c> drives the editor the person has open (or <c>jazz serve</c>), live;
/// <c>jazz mcp --project trailer.jazz</c> opens the project here, headless, and saves it when a
/// tool says to or, with <c>--save-on-exit</c>, when the client goes. With neither it attaches to
/// the newest editor. <c>--list-tools</c> prints the tools and exits. The server itself is
/// <see cref="McpHost"/>, which jazz-mcp.exe runs too.
/// </remarks>
internal static class McpCommand
{
    /// <summary>Builds the verb.</summary>
    public static Command Build()
    {
        var project = new Option<string?>("--project") { Description = "Open this project here instead of attaching to an editor; a path that does not exist yet starts a new project there." };
        var save = new Option<bool>("--save-on-exit") { Description = "With --project, save it when the client disconnects, if it changed." };
        var list = new Option<bool>("--list-tools") { Description = "Print the tools and what each does, then exit. With --json, their schemas too." };

        var command = new Command("mcp", "Serve the Model Context Protocol on stdin and stdout for Claude Code: 'claude mcp add jazz -- jazz mcp --attach'.")
        {
            project, save, list,
        };

        command.SetAction(parse =>
        {
            if (parse.GetValue(list))
            {
                Console.Out.Write(McpHost.ListTools(parse.GetValue(JazzCli.JsonOption)));
                return ExitCode.Ok;
            }

            string? file = parse.GetValue(project);
            if (JazzCli.AttachTarget is not null && file is not null)
            {
                Console.Error.WriteLine("jazz: say --attach or --project, not both: attached, the editor has the project open already.");
                return ExitCode.UsageError;
            }

            var options = new McpHostOptions(file is null, JazzCli.AttachTarget, file, parse.GetValue(save));
            try
            {
                return RunAsync(options).GetAwaiter().GetResult();
            }
            catch (AttachException error)
            {
                Console.Error.WriteLine($"jazz: attach-failed: {error.Message}");
                return ExitCode.AttachFailed;
            }
            catch (CommandException error)
            {
                Console.Error.WriteLine($"jazz: {error.Code}: {error.Message}");
                return ExitCode.CommandError;
            }
        });

        return command;
    }

    private static async Task<int> RunAsync(McpHostOptions options)
    {
        await using EditorLink link = await McpHost.LinkAsync(options).ConfigureAwait(false);
        await McpHost.RunAsync(link).ConfigureAwait(false);
        return ExitCode.Ok;
    }
}
