using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Control;
using JazzHands.Core;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>
/// The jazz side of remote control: every generated verb with <c>--attach</c>, <c>jazz rpc</c>,
/// and <c>jazz serve</c>.
/// </summary>
/// <remarks>
/// Attached, a verb is built exactly as it is headless (the same parsing, times read at the
/// editor's frame rate) and sent over the control server as the registry command's JSON, so a
/// command typed here and the same one sent by the GUI are one command. Its answer prints the same
/// way. Nothing answering is exit code 4.
/// </remarks>
public static class RpcCommands
{
    private static readonly JsonSerializerOptions Pretty = new(JsonRpc.Options)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs a generated verb against the editor it is attached to.</summary>
    internal static int RunAttached(CommandMetadata metadata, ParseResult parse, List<Argument<string>> arguments, Dictionary<string, Option<string>> options)
    {
        bool json = parse.GetValue(JazzCli.JsonOption);
        return Attached(json, async client =>
        {
            Rational rate = await FrameRateAsync(client).ConfigureAwait(false);
            ParameterMetadata[] positional = [.. metadata.Arguments];
            object built = CommandRegistry.FromCommandLine(
                metadata.Name,
                [.. arguments.Select((argument, index) => GeneratedCommands.InputPath(positional[index], parse.GetValue(argument))).Where(value => value is not null)!],
                options.ToDictionary(pair => pair.Key, pair => GeneratedCommands.ValueOf(parse, pair.Value), StringComparer.Ordinal),
                rate);

            JsonNode? result = await client.CallCommandAsync(metadata.Name, CommandRegistry.ArgsToJson(built)).ConfigureAwait(false);
            if (metadata.IsQuery)
            {
                object? answer = result?["data"]?.Deserialize(metadata.ResultType!, JsonRpc.Options);
                Console.Out.WriteLine(GeneratedCommands.Render(answer, json));
            }
            else if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, version = result?["version"]?.GetValue<long>(), changed = result?["changedIds"], attached = client.Issuer }, Pretty));
            }
            else
            {
                int changed = (result?["changedIds"] as JsonArray)?.Count ?? 0;
                Console.Out.WriteLine($"{metadata.Name}: {Words.Count(changed, "item")} changed in the editor ({Where(client)}).");
            }

            // The editor answers before it goes; the uninstaller needs it gone before it checks
            // which files are in use, so wait for the process (not when it waits for exports).
            if (built is QuitAppCommand { WaitForExports: false } && !await client.WaitForServerExitAsync(QuitTimeout).ConfigureAwait(false))
            {
                Fail(json, "still-running", $"The editor accepted app.quit but was still running after {QuitTimeout.TotalSeconds:0} seconds.");
                return ExitCode.CommandError;
            }

            return ExitCode.Ok;
        });
    }

    /// <summary><c>jazz rpc</c>: the raw tools.</summary>
    public static Command BuildRpc()
    {
        var rpc = new Command("rpc", "Talk JSON-RPC to a running editor or 'jazz serve': call a method, list them, watch events, or pass stdin through.");
        rpc.Subcommands.Add(BuildCall());
        rpc.Subcommands.Add(BuildList());
        rpc.Subcommands.Add(BuildEvents());
        rpc.Subcommands.Add(BuildStdio());
        rpc.Subcommands.Add(BuildInstances());
        return rpc;
    }

    /// <summary><c>jazz serve</c>: a headless editor other processes can drive.</summary>
    public static Command BuildServe()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file to serve; made empty when it is not there." };
        var pipe = new Option<string?>("--pipe") { Description = "The pipe name. 'jazzhands' when free, else 'jazzhands-<pid>'." };
        var tcp = new Option<bool>("--tcp") { Description = "Listen on TCP too, on loopback, with a token clients must say." };
        var port = new Option<int>("--port") { Description = "The TCP port; 0 picks a free one.", DefaultValueFactory = _ => ControlServerOptions.DefaultTcpPort };
        var address = new Option<string>("--address") { Description = "The address TCP binds to. Other than loopback needs --allow-remote.", DefaultValueFactory = _ => "127.0.0.1" };
        var remote = new Option<bool>("--allow-remote") { Description = "Let TCP bind to an address other machines can reach. Only on a network you trust; the token is all that stands between it and anyone." };
        var token = new Option<string?>("--token") { Description = "The token TCP clients must say; made up when left out, and printed." };
        var save = new Option<bool>("--save-on-exit") { Description = "Save the project when stopped with Ctrl+C, if it has changes." };

        var command = new Command("serve", "Open a project headless and serve it over the control server until Ctrl+C: a GUI-less editor for scripts and Claude Code.")
        {
            project, pipe, tcp, port, address, remote, token, save,
        };

        command.SetAction(parse => Serve(
            parse.GetValue(project)!,
            new ControlServerOptions
            {
                PipeName = parse.GetValue(pipe),
                Tcp = parse.GetValue(tcp),
                TcpPort = parse.GetValue(port),
                TcpAddress = parse.GetValue(address)!,
                AllowRemote = parse.GetValue(remote),
                Token = parse.GetValue(token),
                Name = "jazz serve",
            },
            parse.GetValue(save),
            parse.GetValue(JazzCli.JsonOption)).GetAwaiter().GetResult());

        return command;
    }

    /// <summary><c>jazz --attach frame</c>: the editor's frame as its preview draws it.</summary>
    public static Command BuildAttachedFrame()
    {
        var at = new Option<string>("--at") { Description = "The sequence time to draw: 00:00:12.500, 750f or 12.5s.", Required = true };
        var output = new Option<string>("--out") { Description = "The .png to write.", Required = true };
        var size = new Option<string?>("--size") { Description = "How wide to draw it, as a size: 960x540; the height follows the sequence's shape." };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence; the active one when left out." };
        var command = new Command("frame", "Draw one frame of the editor's project as its preview shows it, to a PNG.") { at, output, size, sequence };

        command.SetAction(parse => Attached(parse.GetValue(JazzCli.JsonOption), async client =>
        {
            Rational rate = await FrameRateAsync(client).ConfigureAwait(false);
            var time = (Flicks)CommandValues.Parse(typeof(Flicks), parse.GetValue(at)!, rate, "at")!;
            var args = new JsonObject { ["at"] = time.Value, ["sequence"] = parse.GetValue(sequence) };
            if (parse.GetValue(size) is { Length: > 0 } text && CommandValues.Parse(typeof(FrameSize), text, rate, "size") is FrameSize fit)
            {
                args["width"] = fit.Width;
            }

            JsonNode? frame = await client.CallCommandAsync("render.frame", args).ConfigureAwait(false);
            string file = Path.GetFullPath(parse.GetValue(output)!);
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(frame!["data"]!.GetValue<string>())).ConfigureAwait(false);
            Console.Out.WriteLine(parse.GetValue(JazzCli.JsonOption)
                ? JsonSerializer.Serialize(new { ok = true, path = file, width = frame["width"]!.GetValue<int>(), height = frame["height"]!.GetValue<int>() }, Pretty)
                : $"Wrote {file}: {frame["width"]}x{frame["height"]} at {Timecode.FormatClock(time)}, from the editor.");
            return ExitCode.Ok;
        }));

        return command;
    }

    /// <summary>
    /// Connects, runs, and turns what can go wrong into exit codes: 4 when nothing answers, 1 when
    /// the editor refuses, 3 for a media error.
    /// </summary>
    internal static int Attached(bool json, Func<JazzClient, Task<int>> body)
    {
        try
        {
            return AttachedAsync(json, body).GetAwaiter().GetResult();
        }
        catch (AttachException error)
        {
            Fail(json, "attach-failed", error.Message);
            return ExitCode.AttachFailed;
        }
        catch (CommandException error)
        {
            GeneratedCommands.Report(error, json);
            return ExitCode.CommandError;
        }
        catch (JsonRpcException error)
        {
            Fail(json, error.CommandCode.Length > 0 ? error.CommandCode : $"rpc{error.Code}", error.Message);
            return error.Code == JsonRpc.MediaError ? ExitCode.MediaError : ExitCode.CommandError;
        }
    }

    /// <summary>How long <c>jazz app quit --attach</c> waits for the editor's process to end.</summary>
    internal static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(60);

    private static async Task<int> AttachedAsync(bool json, Func<JazzClient, Task<int>> body)
    {
        await using JazzClient client = await JazzClient.ConnectAsync(JazzCli.AttachTarget, "cli").ConfigureAwait(false);
        return await body(client).ConfigureAwait(false);
    }

    /// <summary>The rate the editor's active sequence reads timecode at.</summary>
    internal static async Task<Rational> FrameRateAsync(JazzClient client)
    {
        JsonNode? info = await client.CallAsync("session.info").ConfigureAwait(false);
        return info?["fps"]?.GetValue<string>() is { } fps && Rational.TryParse(fps, out Rational rate) ? rate : Rational.Fps30;
    }

    private static string Where(JazzClient client) =>
        client.Instance is { } instance ? $"{instance.Kind} {instance.Pid}" : "attached";

    private static Command BuildCall()
    {
        var method = new Argument<string>("method") { Description = "The method: a registry name (clip.split) or a session method (session.info)." };
        var parameters = new Argument<string?>("params") { Description = "Its params as a JSON object: '{\"clipId\": \"...\", \"at\": \"2s\"}'. None when left out.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("call", "Call one method and print the result as JSON.") { method, parameters };

        command.SetAction(parse =>
        {
            // Read before connecting: params that are not JSON are a usage error, said plainly,
            // not a crash (PowerShell 5 strips the inner quotes of '{"to":"2s"}', for one).
            JsonNode? given;
            try
            {
                given = parse.GetValue(parameters) is { Length: > 0 } text ? JsonNode.Parse(text) : null;
            }
            catch (JsonException error)
            {
                Fail(true, "bad-params", $"The params are not JSON: {error.Message} In PowerShell 5, escape the inner quotes: '{{\\\"to\\\":\\\"2s\\\"}}'.");
                return ExitCode.UsageError;
            }

            return Attached(true, async client =>
            {
                JsonNode? result = await client.CallAsync(parse.GetValue(method)!, given).ConfigureAwait(false);
                Console.Out.WriteLine(result?.ToJsonString(Pretty) ?? "null");
                return ExitCode.Ok;
            });
        });

        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List every method: the session methods and every registry command and query, with their params. Needs no editor.");
        command.SetAction(parse =>
        {
            JsonArray catalog = ControlServer.Catalog();
            if (parse.GetValue(JazzCli.JsonOption))
            {
                Console.Out.WriteLine(catalog.ToJsonString(Pretty));
                return ExitCode.Ok;
            }

            foreach (JsonNode? method in catalog)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{method!["name"],-28} {method["kind"],-8} {method["description"]}"));
            }

            return ExitCode.Ok;
        });

        return command;
    }

    private static Command BuildEvents()
    {
        var events = new Option<string>("--events") { Description = "Which events, comma separated: " + string.Join(", ", ControlServer.EventNames) + ". All of them by default.", DefaultValueFactory = _ => "*" };
        var command = new Command("events", "Print the editor's events as they happen, one JSON object a line, until Ctrl+C.") { events };

        command.SetAction(parse => Attached(true, async client =>
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                stop.Cancel();
            };

            await client.SubscribeAsync(parse.GetValue(events)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), stop.Token).ConfigureAwait(false);
            try
            {
                await foreach (JsonObject notification in client.Events.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    Console.Out.WriteLine(new JsonObject { ["event"] = notification["method"]?.DeepClone(), ["params"] = notification["params"]?.DeepClone() }.ToJsonString(JsonRpc.Options));
                }
            }
            catch (OperationCanceledException)
            {
                return ExitCode.Cancelled;
            }

            return ExitCode.Ok;
        }));

        return command;
    }

    private static Command BuildStdio()
    {
        var command = new Command("stdio", "Pass JSON-RPC lines from stdin to the editor and its lines to stdout, as they are: for embedding jazz in another program.");
        command.SetAction(_ =>
        {
            try
            {
                return StdioAsync().GetAwaiter().GetResult();
            }
            catch (AttachException error)
            {
                Console.Error.WriteLine($"jazz: attach-failed: {error.Message}");
                return ExitCode.AttachFailed;
            }
        });

        return command;
    }

    private static async Task<int> StdioAsync()
    {
        (Stream stream, _, string? token) = await JazzClient.OpenAsync(JazzCli.AttachTarget, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await using var channel = new LineChannel(stream);
        if (token is not null)
        {
            // TCP wants the token first; the caller's own hello, if it sends one, is answered too.
            await channel.SendAsync(JsonRpc.Line(JsonRpc.Notification("session.hello", new JsonObject { ["token"] = token, ["client"] = "stdio" }))).ConfigureAwait(false);
        }

        Task toEditor = Task.Run(async () =>
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!await channel.SendAsync(line).ConfigureAwait(false))
                {
                    break;
                }
            }

            await channel.FlushAndCloseAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        });

        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        while (await channel.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }

        await toEditor.ConfigureAwait(false);
        return ExitCode.Ok;
    }

    private static Command BuildInstances()
    {
        var command = new Command("instances", "List the running editors and 'jazz serve' processes, and how to reach them.");
        command.SetAction(parse =>
        {
            IReadOnlyList<InstanceInfo> instances = InstanceRegistry.List();
            if (parse.GetValue(JazzCli.JsonOption))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(instances.Select(instance => instance with { Token = instance.Token is null ? null : "(hidden)" }), Pretty));
            }
            else if (instances.Count == 0)
            {
                Console.Out.WriteLine("No editor is running.");
            }
            else
            {
                foreach (InstanceInfo instance in instances)
                {
                    Console.Out.WriteLine($"{instance.Pid,-7} {instance.Kind,-6} pipe:{instance.Pipe}{(instance.Tcp is null ? string.Empty : $"  tcp {instance.Tcp}")}  {(instance.Project.Length > 0 ? instance.Project : "(unsaved)")}");
                }
            }

            return ExitCode.Ok;
        });

        return command;
    }

    private static async Task<int> Serve(string file, ControlServerOptions options, bool saveOnExit, bool json)
    {
        string path = Path.GetFullPath(file.EndsWith(".jazz", StringComparison.OrdinalIgnoreCase) ? file : file + ".jazz");
        Project project;
        UnknownFields unknown = UnknownFields.None;
        if (File.Exists(path))
        {
            ProjectLoad load = ProjectFile.Load(path);
            if (!load.IsLoadable)
            {
                Fail(json, "project-invalid", $"'{path}' has errors and will not open. Run 'jazz validate' to see them.");
                return ExitCode.CommandError;
            }

            project = load.Project;
            unknown = load.Unknown;
        }
        else
        {
            project = Project.CreateNew(Path.GetFileNameWithoutExtension(path));
        }

        await using ServiceProvider services = new ServiceCollection()
            .AddJazzHandsEngine()
            .AddJazzHandsControlSettings()
            .AddSingleton<IExportService>(new ForegroundExportService())
            .BuildServiceProvider();
        await using var session = new Session(project, services, File.Exists(path) ? path : string.Empty, recovery: true) { DefaultIssuer = "serve" };
        await using var server = new ControlServer(
            new ControlTarget { Session = session, Selection = services.GetService<Engine.Selection.SelectionService>(), Exports = services.GetService<IExportService>(), Kind = "serve" },
            options);

        try
        {
            await server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Fail(json, "serve-failed", error.Message);
            return ExitCode.CommandError;
        }

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, pid = Environment.ProcessId, pipe = server.PipeName, tcp = server.TcpEndPoint?.ToString(), token = server.Token, project = path }, Pretty));
        }
        else
        {
            Console.Out.WriteLine($"Serving {path} on pipe {server.PipeName}{(server.TcpEndPoint is { } endPoint ? $" and {endPoint}, token {server.Token}" : string.Empty)}. Ctrl+C to stop.");
            if (server.IsReachableFromNetwork)
            {
                Console.Error.WriteLine("jazz: warning: other machines can reach this server; the token is all that stands between it and them.");
            }
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (saveOnExit && session.IsDirty)
        {
            ProjectFile.Save(path, session.Project, unknown);
            Console.Out.WriteLine($"Saved {path}.");
        }

        return ExitCode.Ok;
    }

    private static void Fail(bool json, string code, string message)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, code, error = message }, Pretty));
        }
        else
        {
            Console.Error.WriteLine($"jazz: {code}: {message}");
        }
    }
}
