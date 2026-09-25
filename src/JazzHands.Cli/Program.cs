using System.CommandLine;
using JazzHands.Audio.Output;
using JazzHands.Cli;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Logging;
using Serilog.Events;

// Every jazz invocation logs to %LOCALAPPDATA%\JazzHands\logs and warns on stderr. Human output
// goes to stdout so "jazz describe --json | jq" stays clean.
LogSetup.ConfigureForCli();
EffectCatalog.LoadUserTransitions();

try
{
    // --attach takes an optional target, which System.CommandLine would take from the verb after it
    // (--attach clip split ...), so it is lifted out here and the tree built for attaching.
    args = JazzCli.LiftAttach(args);
    RootCommand root = JazzCli.BuildRootCommand();
    ParseResult parsed = root.Parse(args);

    // Chosen before anything makes a device, so every render in this process is on it.
    if (parsed.GetValue(JazzCli.GpuOption) is { Length: > 0 } gpu)
    {
        if (!JazzHands.Render.GpuChoice.TryParse(gpu, out JazzHands.Render.GpuChoice? choice))
        {
            Console.Error.WriteLine($"jazz: --gpu takes auto, warp or an adapter number, not '{gpu}'.");
            return ExitCode.UsageError;
        }

        JazzHands.Render.RenderDevice.Preferred = choice;
    }

    int code = parsed.Invoke();

    // System.CommandLine prints what was wrong and exits 1; the contract says a usage error is 2,
    // so a script can tell "you typed it wrong" from "it was refused".
    return parsed.Errors.Count > 0 ? ExitCode.UsageError : code;
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

        /// <summary>The global --gpu option: which adapter renders.</summary>
        public static Option<string?> GpuOption { get; } = new("--gpu")
        {
            Description = "Which adapter renders: auto (the best GPU, the default), warp (the software rasterizer, as tests and CI use) or an adapter number from 'jazz version'. JAZZ_GPU sets the same.",
            Recursive = true,
        };

        /// <summary>The global --attach option, for help and the reference; Program lifts it out before parsing.</summary>
        public static Option<string?> AttachOption { get; } = new("--attach")
        {
            Description = "Send the command to a running editor or 'jazz serve' instead of opening a project, which is then left out: the newest one, or --attach pipe:<name>, a process id, or 127.0.0.1:47800 for TCP.",
            Recursive = true,
            Arity = ArgumentArity.ZeroOrOne,
        };

        /// <summary>Where to attach, when --attach was given: empty for the newest instance. Null when not attached.</summary>
        public static string? AttachTarget { get; set; }

        /// <summary>Takes --attach and its target, if it has one, out of the arguments and remembers them.</summary>
        public static string[] LiftAttach(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            int at = Array.FindIndex(args, arg => arg == "--attach" || arg.StartsWith("--attach=", StringComparison.Ordinal));
            if (at < 0)
            {
                AttachTarget = null;
                return args;
            }

            var rest = new List<string>(args);
            if (args[at].StartsWith("--attach=", StringComparison.Ordinal))
            {
                AttachTarget = args[at]["--attach=".Length..];
                rest.RemoveAt(at);
                return [.. rest];
            }

            string? next = at + 1 < args.Length ? args[at + 1] : null;
            bool isTarget = next is not null
                && (next.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase)
                    || next.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
                    || next.All(char.IsDigit)
                    || System.Net.IPEndPoint.TryParse(next, out System.Net.IPEndPoint? endPoint) && next.Contains(':', StringComparison.Ordinal));
            AttachTarget = isTarget ? next : string.Empty;
            rest.RemoveRange(at, isTarget ? 2 : 1);
            return [.. rest];
        }

        /// <summary>Builds the root command with every verb attached.</summary>
        public static RootCommand BuildRootCommand()
        {
            var root = new RootCommand("Jazz Hands video editor. Everything the GUI can do, jazz can do headless.");
            root.Options.Add(JsonOption);
            root.Options.Add(VerboseOption);
            root.Options.Add(GpuOption);
            root.Options.Add(AttachOption);
            if (AttachTarget is not null)
            {
                // Attached, a verb works on the running editor's project: the generated verbs, a
                // script, a frame of what it shows, and the rpc tools.
                GeneratedCommands.AddTo(root);
                root.Subcommands.Add(ApplyCommand.Build());
                root.Subcommands.Add(RpcCommands.BuildRpc());
                root.Subcommands.Add(RpcCommands.BuildAttachedFrame());
                root.Subcommands.Add(McpCommand.Build());
                return root;
            }

            root.Subcommands.Add(BuildVersionCommand());
            ProjectCommands.AddTo(root);
            ExportCommands.AddTo(root);
            InspectCommands.AddTo(root);
            root.Subcommands.Add(ApplyCommand.Build());
            root.Subcommands.Add(CliDocs.Build());
            root.Subcommands.Add(RpcCommands.BuildRpc());
            root.Subcommands.Add(RpcCommands.BuildServe());
            root.Subcommands.Add(McpCommand.Build());
            GeneratedCommands.AddTo(root);
            root.Subcommands.Add(BuildPerfCommand());
            return root;
        }

        private static Command BuildPerfCommand()
        {
            var perf = new Command("perf", "Measure the engine. Numbers land in Docs/PERF.md.");
            perf.Subcommands.Add(BuildDecodeBenchmarkCommand());
            perf.Subcommands.Add(BuildScrubBenchmarkCommand());
            perf.Subcommands.Add(BuildAudioBenchmarkCommand());
            perf.Subcommands.Add(BuildPlaybackBenchmarkCommand());
            perf.Subcommands.Add(BuildThumbnailBenchmarkCommand());
            return perf;
        }

        private static Command BuildThumbnailBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "A media file with a picture." };
            var zoom = new Option<double[]>("--zoom")
            {
                Description = "Pixels per second to measure at, repeatable. The fit zoom and 40 when left out.",
                AllowMultipleArgumentsPerToken = true,
            };

            var command = new Command(
                "thumbs",
                "Time a clip's thumbnails and waveform from a cold cache: visible, whole strip, and sound.")
            {
                file,
                zoom,
            };

            command.SetAction(parseResult =>
            {
                FileInfo target = parseResult.GetValue(file)!;
                if (!target.Exists)
                {
                    Console.Error.WriteLine($"jazz: '{target.FullName}' does not exist.");
                    return ExitCode.CommandError;
                }

                LogSetup.ConfigureForCli(parseResult.GetValue(VerboseOption) ? LogEventLevel.Debug : LogEventLevel.Warning);

                try
                {
                    ThumbnailBenchmarkResult result = ThumbnailBenchmark.Run(target.FullName, parseResult.GetValue(zoom));

                    Console.Out.WriteLine(parseResult.GetValue(JsonOption)
                        ? System.Text.Json.JsonSerializer.Serialize(result, JazzHands.Core.Serialization.JazzJson.Options)
                        : ThumbnailBenchmark.Describe(result));

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

        private static Command BuildPlaybackBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "A media file with a picture; it is laid end to end to fill the run." };
            var minutes = new Option<double>("--minutes")
            {
                Description = "How long to play.",
                DefaultValueFactory = _ => 5,
            };
            var software = new Option<bool>("--software")
            {
                Description = "Decode on the CPU, as CI and a machine without a video decoder do.",
            };
            var audible = new Option<bool>("--audible")
            {
                Description = "Play at full volume. Silent by default: the clock is the sound card's either way.",
            };
            var panel = new Option<string>("--panel")
            {
                Description = "The size of the preview surface each frame is drawn into.",
                DefaultValueFactory = _ => "2560x1440",
            };
            var layers = new Option<int>("--layers")
            {
                Description = "Video tracks to stack, each scaled and turned into its own quadrant, on its own decoder.",
                DefaultValueFactory = _ => 1,
            };
            var effects = new Option<string>("--effects")
            {
                Description = "Picture effects stacked on every clip at their defaults, by type id, comma separated: video.blur.gaussian,video.glow. An id that is not a picture effect is refused.",
            };

            var vfx = new Option<bool>("--vfx")
            {
                Description = "Over the file, two moving layers with motion blur (a title and a shape), a particle layer, and a heavy hit every four seconds: Phase 29a's bar.",
            };

            var command = new Command(
                "playback",
                "Play a long sequence through the playback engine and count dropped frames.")
            {
                file,
                minutes,
                software,
                audible,
                panel,
                layers,
                effects,
                vfx,
            };

            command.SetAction(parseResult =>
            {
                FileInfo target = parseResult.GetValue(file)!;
                if (!target.Exists)
                {
                    Console.Error.WriteLine($"jazz: '{target.FullName}' does not exist.");
                    return ExitCode.CommandError;
                }

                if (!CommandValues.TryParseSize(parseResult.GetValue(panel), out FrameSize size, out string? sizeError))
                {
                    Console.Error.WriteLine($"jazz: {sizeError}");
                    return ExitCode.CommandError;
                }

                string[] stack = (parseResult.GetValue(effects) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (stack.FirstOrDefault(typeId => EffectCatalog.Registry.Find(typeId) is not { Kind: EffectKind.Video }) is { } unknown)
                {
                    Console.Error.WriteLine($"jazz: '{unknown}' is not a picture effect. 'jazz effect list' names them.");
                    return ExitCode.CommandError;
                }

                LogSetup.ConfigureForCli(parseResult.GetValue(VerboseOption) ? LogEventLevel.Debug : LogEventLevel.Warning);

                try
                {
                    PlaybackBenchmarkResult result = PlaybackBenchmark.Run(
                        target.FullName,
                        TimeSpan.FromMinutes(Math.Max(0.05, parseResult.GetValue(minutes))),
                        hardware: !parseResult.GetValue(software),
                        audible: parseResult.GetValue(audible),
                        size.Width,
                        size.Height,
                        Math.Clamp(parseResult.GetValue(layers), 1, 16),
                        stack,
                        Console.Error,
                        parseResult.GetValue(vfx));

                    Console.Out.WriteLine(parseResult.GetValue(JsonOption)
                        ? System.Text.Json.JsonSerializer.Serialize(result, JazzHands.Core.Serialization.JazzJson.Options)
                        : PlaybackBenchmark.Describe(result));

                    return result.DroppedPercent < 0.1 ? ExitCode.Ok : ExitCode.CommandError;
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

        private static Command BuildAudioBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "A media file with audio; each stream becomes a track." };
            var clips = new Option<int>("--clips")
            {
                Description = "How many clips to cut across the tracks.",
                DefaultValueFactory = _ => 30,
            };
            var minutes = new Option<double>("--minutes")
            {
                Description = "How long to play.",
                DefaultValueFactory = _ => 10,
            };
            var device = new Option<string?>("--device")
            {
                Description = "A playback device id. The default device when left out.",
            };
            var audible = new Option<bool>("--audible")
            {
                Description = "Play at full volume. Silent by default: the mix and the device do the same work, and nobody has to listen to test tones.",
            };

            var command = new Command(
                "audio",
                "Play a many-clip project through the sound card and count the gaps.")
            {
                file,
                clips,
                minutes,
                device,
                audible,
            };

            command.SetAction(parseResult =>
            {
                FileInfo target = parseResult.GetValue(file)!;
                if (!target.Exists)
                {
                    Console.Error.WriteLine($"jazz: '{target.FullName}' does not exist.");
                    return ExitCode.CommandError;
                }

                if (!WasapiOutput.HasDevice())
                {
                    Console.Error.WriteLine("jazz: there is no playback device to measure.");
                    return ExitCode.CommandError;
                }

                LogSetup.ConfigureForCli(parseResult.GetValue(VerboseOption) ? LogEventLevel.Debug : LogEventLevel.Warning);

                try
                {
                    AudioBenchmarkResult result = AudioBenchmark.Run(
                        target.FullName,
                        Math.Max(1, parseResult.GetValue(clips)),
                        TimeSpan.FromMinutes(Math.Max(0.05, parseResult.GetValue(minutes))),
                        parseResult.GetValue(device),
                        parseResult.GetValue(audible),
                        Console.Error);

                    Console.Out.WriteLine(parseResult.GetValue(JsonOption)
                        ? System.Text.Json.JsonSerializer.Serialize(result, JazzHands.Core.Serialization.JazzJson.Options)
                        : AudioBenchmark.Describe(result));

                    return result.Underruns == 0 ? ExitCode.Ok : ExitCode.CommandError;
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

        private static Command BuildDecodeBenchmarkCommand()
        {
            var file = new Argument<FileInfo>("file") { Description = "The media file to decode." };
            var software = new Option<bool>("--software")
            {
                Description = "Force the software decoder instead of trying D3D11VA.",
            };
            var reuse = new Option<bool>("--reuse")
            {
                Description = "Keep one decoder across passes and rewind, the way playback loops do. This is what separates a decode leak from the cost of churning decoders.",
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
                Description = "Move the playhead in small steps, the way a hand does, instead of at random. This is what a scrub actually is; random access is the worst case.",
            };
            var reverse = new Option<bool>("--reverse")
            {
                Description = "Play backwards one frame at a time, priming each group of pictures. Reports how many frames missed the playback budget.",
            };
            var nearest = new Option<bool>("--nearest")
            {
                Description = "Take the nearest keyframe instead of the exact frame, the way shuttling does.",
            };
            var proxy = new Option<bool>("--proxy")
            {
                Description = "Scrub the file's half size proxy, making it first (in the cache) if there is none.",
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
                proxy,
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
                            : JazzHands.Media.Decode.SeekMode.Exact,
                        proxy: parseResult.GetValue(proxy));

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
