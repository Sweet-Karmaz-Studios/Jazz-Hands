using System.CommandLine;
using JazzHands.Cli;
using JazzHands.Engine.Logging;
using Serilog.Events;

// Every jazz invocation logs to %LOCALAPPDATA%\JazzHands\logs and warns on stderr. Human output
// goes to stdout so "jazz describe --json | jq" stays clean.
LogSetup.ConfigureForCli();

try
{
    RootCommand root = JazzCli.BuildRootCommand();
    return root.Parse(args).Invoke();
}
finally
{
    LogSetup.Shutdown();
}

namespace JazzHands.Cli
{
    /// <summary>
    /// Builds the jazz command tree. From Phase 24 onwards most of this is generated from
    /// CommandRegistry so a command exists in the CLI, JSON-RPC and MCP the moment it is written.
    /// </summary>
    public static class JazzCli
    {
        /// <summary>The global --json switch: machine output instead of human text.</summary>
        public static Option<bool> JsonOption { get; } = new("--json")
        {
            Description = "Emit a single JSON object instead of human-readable text.",
            Recursive = true,
        };

        /// <summary>The global --verbose switch: lower the console log level.</summary>
        public static Option<bool> VerboseOption { get; } = new("--verbose", "-v")
        {
            Description = "Log engine detail to stderr.",
            Recursive = true,
        };

        /// <summary>Builds the root command with every verb attached.</summary>
        public static RootCommand BuildRootCommand()
        {
            var root = new RootCommand("Jazz Hands video editor. Everything the GUI can do, jazz can do headless.");
            root.Options.Add(JsonOption);
            root.Options.Add(VerboseOption);
            root.Subcommands.Add(BuildVersionCommand());
            return root;
        }

        private static Command BuildVersionCommand()
        {
            var command = new Command("version", "Print the Jazz Hands, .NET, FFmpeg and GPU versions.");
            command.SetAction(parseResult =>
            {
                if (parseResult.GetValue(VerboseOption))
                {
                    LogSetup.ConfigureForCli(LogEventLevel.Debug);
                }

                VersionReport report = VersionReport.Collect();
                Console.Out.WriteLine(parseResult.GetValue(JsonOption) ? report.ToJson() : report.ToText());
                return ExitCode.Ok;
            });

            return command;
        }
    }
}
