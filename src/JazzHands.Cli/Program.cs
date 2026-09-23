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
            ProjectCommands.AddTo(root);
            GeneratedCommands.AddTo(root);
            root.Subcommands.Add(BuildPerfCommand());
            return root;
        }

        private static Command BuildPerfCommand()
        {
            var perf = new Command("perf", "Measure the engine. Numbers land in Docs/PERF.md.");
            perf.Subcommands.Add(BuildDecodeBenchmarkCommand());
            perf.Subcommands.Add(BuildScrubBenchmarkCommand());
            return perf;
        }

        private static Command BuildDecodeBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "The media file to decode." };
            var software = new Option<bool>("--software")
            {
                Description = "Force the software decoder instead of trying D3D11VA.",
            };
            var reuse = new Option<bool>("--reuse")
            {
                Description = "Keep one decoder across passes and rewind, the way playback loops do.",
            };
            var passes = new Option<int>("--passes")
            {
                Description = "Decode the file this many times. More than one checks for leaks.",
                DefaultValueFactory = _ => 1,
            };

            var command = new Command("decode", "Decode a file as fast as possible and report throughput.")
            {
                file,
                software,
                passes,
                reuse,
            };

            command.SetAction(parseResult =>
            {
                FileInfo target = parseResult.GetValue(file)!;
                if (!target.Exists)
                {
                    Console.Error.WriteLine($"jazz: '{target.FullName}' does not exist.");
                    return ExitCode.CommandError;
                }

                if (parseResult.GetValue(VerboseOption))
                {
                    LogSetup.ConfigureForCli(LogEventLevel.Debug);
                }

                try
                {
                    DecodeBenchmarkResult result = DecodeBenchmark.Run(
                        target.FullName,
                        useHardware: !parseResult.GetValue(software),
                        passes: Math.Max(1, parseResult.GetValue(passes)),
                        reuseDecoder: parseResult.GetValue(reuse));

                    Console.Out.WriteLine(parseResult.GetValue(JsonOption) ? result.ToJson() : result.ToText());
                    return ExitCode.Ok;
                }
                catch (JazzHands.Media.Interop.FfmpegException ex)
                {
                    Console.Error.WriteLine($"jazz: {ex.Message}");
                    return ExitCode.MediaError;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"jazz: {ex.Message}");
                    return ExitCode.CommandError;
                }
            });

            return command;
        }

        private static Command BuildScrubBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "The media file to scrub." };
            var software = new Option<bool>("--software")
            {
                Description = "Force the software decoder instead of trying D3D11VA.",
            };
            var requests = new Option<int>("--requests")
            {
                Description = "How many random seeks to make.",
                DefaultValueFactory = _ => 200,
            };
            var seed = new Option<int>("--seed")
            {
                Description = "The random seed, so a run repeats exactly.",
                DefaultValueFactory = _ => 20260923,
            };
            var drag = new Option<bool>("--drag")
            {
                Description = "Move the playhead in small steps, the way a hand does, instead of at random.",
            };
            var reverse = new Option<bool>("--reverse")
            {
                Description = "Play backwards one frame at a time, priming each group of pictures.",
            };
            var nearest = new Option<bool>("--nearest")
            {
                Description = "Take the nearest keyframe instead of the exact frame, the way shuttling does.",
            };

            var command = new Command(
                "scrub",
                "Seek to random times and report how long a frame takes to reach a texture.")
            {
                file,
                software,
                requests,
                seed,
                drag,
                reverse,
                nearest,
            };

            command.SetAction(parseResult =>
            {
                FileInfo target = parseResult.GetValue(file)!;
                if (!target.Exists)
                {
                    Console.Error.WriteLine($"jazz: '{target.FullName}' does not exist.");
                    return ExitCode.CommandError;
                }

                if (parseResult.GetValue(VerboseOption))
                {
                    LogSetup.ConfigureForCli(LogEventLevel.Debug);
                }

                try
                {
                    ScrubBenchmarkResult result = ScrubBenchmark.Run(
                        target.FullName,
                        requests: Math.Max(1, parseResult.GetValue(requests)),
                        useHardware: !parseResult.GetValue(software),
                        seed: parseResult.GetValue(seed),
                        pattern: parseResult.GetValue(reverse)
                            ? ScrubPattern.Reverse
                            : parseResult.GetValue(drag) ? ScrubPattern.Drag : ScrubPattern.Random,
                        mode: parseResult.GetValue(nearest)
                            ? JazzHands.Media.Decode.SeekMode.Nearest
                            : JazzHands.Media.Decode.SeekMode.Exact);

                    Console.Out.WriteLine(parseResult.GetValue(JsonOption)
                        ? System.Text.Json.JsonSerializer.Serialize(result, JazzHands.Core.Serialization.JazzJson.Options)
                        : ScrubBenchmark.Describe(result));

                    return ExitCode.Ok;
                }
                catch (JazzHands.Media.Interop.FfmpegException ex)
                {
                    Console.Error.WriteLine($"jazz: {ex.Message}");
                    return ExitCode.MediaError;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"jazz: {ex.Message}");
                    return ExitCode.CommandError;
                }
            });

            return command;
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
