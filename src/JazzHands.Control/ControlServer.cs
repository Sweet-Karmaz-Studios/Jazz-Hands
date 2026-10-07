using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using JazzHands.Engine.Frames;
using JazzHands.Engine.Logging;
using JazzHands.Engine.Playback;
using JazzHands.Engine.Selection;
using JazzHands.Media.Encode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Control;

/// <summary>How a control server listens.</summary>
public sealed record ControlServerOptions
{
    /// <summary>The default pipe, <c>\\.\pipe\jazzhands</c>. A second instance takes <c>jazzhands-&lt;pid&gt;</c>.</summary>
    public const string DefaultPipeName = "jazzhands";

    /// <summary>The TCP port when TCP is on and none is given.</summary>
    public const int DefaultTcpPort = 47800;

    /// <summary>Listen on a named pipe. On by default.</summary>
    public bool Pipe { get; init; } = true;

    /// <summary>The pipe name, or null for <see cref="DefaultPipeName"/>, or the per-process one when that is taken.</summary>
    public string? PipeName { get; init; }

    /// <summary>Listen on TCP too. Off by default (decision 272).</summary>
    public bool Tcp { get; init; }

    /// <summary>The address TCP binds to: loopback unless <see cref="AllowRemote"/>.</summary>
    public string TcpAddress { get; init; } = "127.0.0.1";

    /// <summary>The TCP port; 0 picks a free one.</summary>
    public int TcpPort { get; init; } = DefaultTcpPort;

    /// <summary>Let TCP bind to an address other machines can reach. The editor warns while this is on.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>The token a TCP client must say first, or null to make one.</summary>
    public string? Token { get; init; }

    /// <summary>What <c>instances.json</c> calls this instance.</summary>
    public string Name { get; init; } = "jazz";

    /// <summary>Write this instance into <c>instances.json</c> so clients can find it.</summary>
    public bool Register { get; init; } = true;

    /// <summary>The instance list, or null for the per-user one.</summary>
    public string? RegistryPath { get; init; }

    /// <summary>A response longer than this is sent in <c>rpc.chunk</c> slices: 16 MB.</summary>
    public int ChunkChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>Commands a client may send a second, steadily.</summary>
    public double CommandsPerSecond { get; init; } = 200;

    /// <summary>Commands a client may send at once before the steady rate applies.</summary>
    public double CommandBurst { get; init; } = 1000;

    /// <summary>How often a client hears the playhead move at most: ten times a second.</summary>
    public TimeSpan PlayheadInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>A request a client made that was not a command, for the Command Console.</summary>
/// <param name="Client">Who asked, as <c>rpc:&lt;name&gt;</c>.</param>
/// <param name="Method">What they asked.</param>
/// <param name="Params">With what.</param>
/// <param name="Ok">Whether it was answered.</param>
/// <param name="Error">Why not, or null.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record RemoteRequest(string Client, string Method, JsonNode? Params, bool Ok, string? Error, TimeSpan Elapsed);

/// <summary>An event the server sent its subscribers, for the Command Console.</summary>
/// <param name="Name">The event, <c>project.changed</c> and the rest; never <c>playhead.moved</c>.</param>
/// <param name="Payload">What subscribers were told.</param>
public sealed record ControlEvent(string Name, JsonObject Payload);

/// <summary>
/// The JSON-RPC 2.0 control server: every registry command and query as a method, the session's
/// events as notifications, over a named pipe and, when asked, TCP.
/// </summary>
/// <remarks>
/// <para>
/// A method is a registry name (<c>clip.split</c>) with the command's JSON arguments as params, the
/// shape <c>jazz apply</c> scripts use. A command runs through the session's queue with the
/// connection as its issuer, <c>rpc:&lt;client&gt;</c>, and answers <c>{ok, version,
/// changedIds}</c> once its change has been published; a query answers <c>{ok, version, data}</c>.
/// Built in: <c>rpc.list</c>, <c>session.hello</c>, <c>.info</c>, <c>.subscribe</c>,
/// <c>.unsubscribe</c>, <c>.lock</c>, <c>.unlock</c>, <c>.open</c>, <c>.save</c>, and
/// <c>render.frame</c>, a PNG of the frame as the preview draws it.
/// </para>
/// <para>
/// The pipe is the current user's only, and refuses the network. TCP is off unless asked for,
/// binds to loopback unless told otherwise, and wants the token in <c>session.hello</c> before
/// anything else. Each connection reads one request at a time; many connections run at once.
/// </para>
/// </remarks>
public sealed class ControlServer : IAsyncDisposable
{
    /// <summary>The events a client can subscribe to.</summary>
    public static readonly IReadOnlyList<string> EventNames =
    [
        "project.changed", "command.completed", "session.opened", "selection.changed",
        "playhead.moved", "export.progress", "export.done", "log",
    ];

    private static readonly ConcurrentDictionary<Type, MethodInfo> QueryMethods = new();

    private readonly ILogger _log = Log.ForContext<ControlServer>();
    private readonly ControlTarget _target;
    private readonly ControlServerOptions _options;
    private readonly ConcurrentDictionary<ControlConnection, byte> _connections = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _listeners = [];
    private readonly Lock _renderGate = new();
    private TcpListener? _tcp;
    private ControlConnection? _local;
    private StillRenderer? _renderer;
    private int _clientNumber;
    private bool _started;
    private int _disposed;

    /// <summary>A server over a target; call <see cref="StartAsync"/> to listen.</summary>
    public ControlServer(ControlTarget target, ControlServerOptions? options = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? new ControlServerOptions();
    }

    /// <summary>Raised after every request that is not a command, for the Command Console.</summary>
    public event EventHandler<RemoteRequest>? RequestHandled;

    /// <summary>
    /// Raised with every event the server broadcasts, whether or not anyone subscribed, except the
    /// playhead, which moves too often to be worth a line. Raised on the thread that caused it.
    /// </summary>
    public event EventHandler<ControlEvent>? EventRaised;

    /// <summary>The pipe listened on, or null.</summary>
    public string? PipeName { get; private set; }

    /// <summary>Where TCP listens, or null.</summary>
    public IPEndPoint? TcpEndPoint { get; private set; }

    /// <summary>The token TCP clients must say, or null without TCP.</summary>
    public string? Token { get; private set; }

    /// <summary>What this server is attached to.</summary>
    public ControlTarget Target => _target;

    /// <summary>The server's options.</summary>
    public ControlServerOptions Options => _options;

    /// <summary>The clients connected now, by the names they gave.</summary>
    public IReadOnlyList<string> Clients => [.. _connections.Keys.Select(connection => connection.Issuer).Order(StringComparer.Ordinal)];

    /// <summary>True while TCP listens on an address other machines can reach.</summary>
    public bool IsReachableFromNetwork => TcpEndPoint is { } endPoint && !IPAddress.IsLoopback(endPoint.Address);

    /// <summary>Starts listening, subscribes to the target's events, and registers the instance.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        Subscribe();

        if (_options.Pipe)
        {
            NamedPipeServerStream first = CreateFirstPipe(out string name);
            PipeName = name;
            _listeners.Add(Task.Run(() => PipeLoopAsync(first)));
        }

        if (_options.Tcp)
        {
            IPAddress address = IPAddress.Parse(_options.TcpAddress);
            if (!IPAddress.IsLoopback(address) && !_options.AllowRemote)
            {
                throw new InvalidOperationException($"The control server binds TCP to {address} only with AllowRemote; loopback is 127.0.0.1.");
            }

            Token = _options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _tcp = new TcpListener(address, _options.TcpPort);
            _tcp.Start();
            TcpEndPoint = (IPEndPoint)_tcp.LocalEndpoint;
            _listeners.Add(Task.Run(TcpLoopAsync));
            if (IsReachableFromNetwork)
            {
                _log.Warning("The control server listens on {EndPoint}, which other machines can reach; a token is required", TcpEndPoint);
            }
        }

        if (_options.Register)
        {
            InstanceRegistry.Register(Describe(), _options.RegistryPath);
        }

        _log.Information("Control server listening on pipe {Pipe}{Tcp}", PipeName ?? "(none)", TcpEndPoint is null ? string.Empty : $" and {TcpEndPoint}");
        return Task.CompletedTask;
    }

    /// <summary>This instance as <c>instances.json</c> lists it.</summary>
    public InstanceInfo Describe() => new(
        Environment.ProcessId,
        _target.Kind,
        _options.Name,
        PipeName,
        TcpEndPoint?.ToString(),
        Token,
        _target.Session.ProjectPath,
        DateTimeOffset.UtcNow);

    /// <summary>
    /// Answers a request from inside the editor, the Command Console's, exactly as a remote client
    /// would be answered: the same methods, the same errors, the full JSON-RPC response. Commands
    /// run as <paramref name="issuer"/> and are not rate limited. It works whether or not the
    /// server is listening.
    /// </summary>
    public async Task<JsonObject> CallLocalAsync(string method, JsonNode? parameters, string issuer = "console")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        ControlConnection local = LocalConnection(issuer);
        // Through text, as a pipe would carry it, so the values are read the way a client's are.
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters is null ? null : JsonNode.Parse(parameters.ToJsonString()) };
        return (JsonObject)(await HandleAsync(local, request).ConfigureAwait(false))!;
    }

    private ControlConnection LocalConnection(string issuer)
    {
        lock (_renderGate)
        {
            if (_local is null || _local.Issuer != issuer)
            {
                _local?.Channel.Close();
                _local = new ControlConnection(
                    new LineChannel(Stream.Null),
                    "local",
                    requiresHello: false,
                    issuer,
                    new RateLimiter(1e9, 1e9),
                    _options.PlayheadInterval)
                {
                    FixedIssuer = issuer,
                };
            }

            return _local;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        Unsubscribe();
        _tcp?.Stop();

        foreach (ControlConnection connection in _connections.Keys)
        {
            connection.Notify("session.closed", new JsonObject { ["reason"] = "The editor is closing." });
            await connection.Channel.FlushAndCloseAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_listeners).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }

        if (_options.Register && _started)
        {
            InstanceRegistry.Unregister(Environment.ProcessId, PipeName, _options.RegistryPath);
        }

        lock (_renderGate)
        {
            _renderer?.Dispose();
            _local?.Channel.Close();
        }

        _stopping.Dispose();
    }

    /// <summary>Answers one message (a request, a notification or a batch) for a connection.</summary>
    internal async Task<JsonNode?> HandleAsync(ControlConnection connection, JsonNode? message)
    {
        if (message is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                return JsonRpc.Error(null, JsonRpc.InvalidRequest, "An empty batch asks for nothing.");
            }

            // A batch counts once against the rate, like a batch command.
            if (!connection.Limiter.TryTake(out TimeSpan wait))
            {
                return RateLimited(null, wait);
            }

            var answers = new JsonArray();
            foreach (JsonNode? request in batch)
            {
                if (await HandleOneAsync(connection, request, counted: true).ConfigureAwait(false) is { } answer)
                {
                    answers.Add(answer);
                }
            }

            return answers.Count > 0 ? answers : null;
        }

        return await HandleOneAsync(connection, message, counted: false).ConfigureAwait(false);
    }

    private async Task<JsonNode?> HandleOneAsync(ControlConnection connection, JsonNode? message, bool counted)
    {
        if (message is not JsonObject request || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue(out string? method))
        {
            return JsonRpc.Error(message is JsonObject bad ? bad["id"] : null, JsonRpc.InvalidRequest, "A request is an object with a \"method\".");
        }

        JsonNode? id = request["id"];
        bool notification = !request.ContainsKey("id");
        JsonNode? parameters = request["params"];
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        JsonObject response;
        try
        {
            if (connection.RequiresHello && method != "session.hello")
            {
                response = JsonRpc.Error(id, JsonRpc.Unauthorized, "Say session.hello with the token first.");
            }
            else
            {
                JsonNode? result = await DispatchAsync(connection, method, parameters, counted).ConfigureAwait(false);
                response = JsonRpc.Result(id, result);
            }
        }
        catch (JsonRpcException error)
        {
            response = JsonRpc.Error(id, error.Code, error.Message, error.Detail);
        }
        catch (CommandException error)
        {
            int code = error.Code switch
            {
                "locked" => JsonRpc.Locked,
                "unknown-command" => JsonRpc.MethodNotFound,
                "missing-argument" or "invalid-argument" or "invalid-value" or "invalid-range" or "missing-value" or "unsupported-type" or "invalid-id" => JsonRpc.InvalidParams,
                _ => JsonRpc.CommandError,
            };
            response = JsonRpc.Error(id, code, error.Message, CommandData(error.Code, error.Path, _target.Session.Version));
        }
        catch (FfmpegException error)
        {
            response = JsonRpc.Error(id, JsonRpc.MediaError, error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Error(error, "Control request {Method} failed", method);
            response = JsonRpc.Error(id, JsonRpc.InternalError, $"The server failed: {error.Message}");
        }

        if (CommandRegistry.Find(method) is not { IsQuery: false })
        {
            RequestHandled?.Invoke(this, new RemoteRequest(
                connection.Issuer,
                method,
                parameters?.DeepClone(),
                response["error"] is null,
                response["error"]?["message"]?.GetValue<string>(),
                System.Diagnostics.Stopwatch.GetElapsedTime(started)));
        }

        return notification ? null : response;
    }

    private async Task<JsonNode?> DispatchAsync(ControlConnection connection, string method, JsonNode? parameters, bool counted)
    {
        JsonObject args = parameters switch
        {
            null => [],
            JsonObject given => given,
            _ => throw new JsonRpcException(JsonRpc.InvalidParams, "Params are an object of named arguments."),
        };

        switch (method)
        {
            case "rpc.list":
                return Catalog();
            case "session.hello":
                return Hello(connection, args);
            case "session.project":
                return JsonNode.Parse(JazzHands.Core.Serialization.ProjectFile.Render(_target.Session.Project));
            case "session.info":
                return Info();
            case "session.subscribe":
                return connection.Subscribe(Events(args), add: true);
            case "session.unsubscribe":
                return connection.Subscribe(args["events"] is null ? EventNames : Events(args), add: false);
            case "session.lock":
                return LockSession(connection, args);
            case "session.unlock":
                return new JsonObject { ["released"] = _target.Session.Unlock(connection.Issuer) };
            case "session.open":
                return await RunAsync(connection, new OpenProjectCommand(Text(args, "path"), args["discard"]?.GetValue<bool>() ?? false), counted).ConfigureAwait(false);
            case "session.save":
                return await RunAsync(connection, new SaveProjectCommand(args["path"]?.GetValue<string>()), counted).ConfigureAwait(false);
            case "render.contact-sheet":
                return ContactSheet(args);
            case "render.frame":
                return Render(args);
        }

        if (_target.HostMethods.TryGetValue(method, out Func<JsonObject, Task<JsonNode?>>? host))
        {
            return await host(args).ConfigureAwait(false);
        }

        CommandMetadata metadata = CommandRegistry.Require(method);
        object built = CommandRegistry.FromJson(method, args, FrameRate());
        if (metadata.IsQuery)
        {
            MethodInfo query = QueryMethods.GetOrAdd(metadata.ResultType!, static type => typeof(Session).GetMethod(nameof(Session.Query))!.MakeGenericMethod(type));
            object? answer;
            try
            {
                answer = query.Invoke(_target.Session, [built]);
            }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }

            return new JsonObject { ["ok"] = true, ["version"] = _target.Session.Version, ["data"] = JsonRpc.ToNode(answer) };
        }

        return await RunAsync(connection, (ICommand)built, counted).ConfigureAwait(false);
    }

    private async Task<JsonNode> RunAsync(ControlConnection connection, ICommand command, bool counted)
    {
        if (!counted && !connection.Limiter.TryTake(out TimeSpan wait))
        {
            throw new JsonRpcException(JsonRpc.RateLimited, $"More than {_options.CommandsPerSecond:0} commands a second. Try again in {wait.TotalMilliseconds:0} ms.", new JsonObject { ["retryAfterMs"] = (int)Math.Ceiling(wait.TotalMilliseconds) });
        }

        CommandResult result = await _target.Session.ExecuteAsync(command, connection.Issuer, _stopping.Token).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new CommandException(result.Code ?? "command-failed", result.Error ?? "The command failed.", result.Path ?? string.Empty);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = result.Version,
            ["changedIds"] = new JsonArray([.. result.ChangedIds.Select(changed => (JsonNode?)changed)]),
        };
    }

    private JsonObject Hello(ControlConnection connection, JsonObject args)
    {
        if (connection.RequiresHello)
        {
            string? token = args["token"]?.GetValue<string>();
            if (Token is null || token is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(Token)))
            {
                // Told why, then gone: the answer goes out before the connection closes.
                connection.CloseAfterReply = true;
                throw new JsonRpcException(JsonRpc.Unauthorized, "That is not this server's token.");
            }

            connection.RequiresHello = false;
        }

        if (args["client"]?.GetValue<string>() is { Length: > 0 } name)
        {
            connection.Name = Clean(name);
        }

        return new JsonObject
        {
            ["server"] = "jazz hands",
            ["protocol"] = 1,
            ["pid"] = Environment.ProcessId,
            ["kind"] = _target.Kind,
            ["issuer"] = connection.Issuer,
            ["project"] = _target.Session.ProjectPath,
            ["version"] = _target.Session.Version,
            ["events"] = new JsonArray([.. EventNames.Select(eventName => (JsonNode?)eventName)]),
        };
    }

    private JsonObject Info()
    {
        Session session = _target.Session;
        Project project = session.Project;
        Sequence? active = project.ActiveSequence;
        ProjectSettings settings = active is null ? project.Settings : project.SettingsFor(active);
        return new JsonObject
        {
            ["name"] = project.Name,
            ["path"] = session.ProjectPath,
            ["version"] = session.Version,
            ["dirty"] = session.IsDirty,
            ["activeSequenceId"] = active?.Id,
            ["fps"] = settings.FrameRate.ToString(),
            ["width"] = settings.Width,
            ["height"] = settings.Height,
            ["kind"] = _target.Kind,
            ["lock"] = session.Lock is { } held ? new JsonObject { ["owner"] = held.Owner, ["reason"] = held.Reason, ["until"] = held.Until } : null,
            ["clients"] = new JsonArray([.. Clients.Select(client => (JsonNode?)client)]),
        };
    }

    private JsonObject LockSession(ControlConnection connection, JsonObject args)
    {
        double seconds = Math.Clamp(args["seconds"]?.GetValue<double>() ?? 30, 1, 300);
        string reason = args["reason"]?.GetValue<string>() ?? "a batch of edits";
        if (!_target.Session.TryLock(connection.Issuer, reason, TimeSpan.FromSeconds(seconds), out SessionLock held))
        {
            throw new JsonRpcException(JsonRpc.Locked, $"{held.Owner} holds the session ({held.Reason}) until {held.Until:HH:mm:ss}.", new JsonObject { ["owner"] = held.Owner, ["until"] = held.Until });
        }

        connection.HoldsLock = true;
        return new JsonObject { ["owner"] = held.Owner, ["reason"] = held.Reason, ["until"] = held.Until };
    }

    private JsonObject Render(JsonObject args)
    {
        Project project = _target.Session.Project;
        Sequence sequence = (args["sequence"]?.GetValue<string>() is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", "There is no such sequence.");
        Rational rate = project.SettingsFor(sequence).FrameRate;
        Flicks at = TimeOf(args["at"], rate, "at") ?? throw new JsonRpcException(JsonRpc.InvalidParams, "render.frame needs \"at\": a time as text or flicks.");
        int width = Math.Clamp(args["width"]?.GetValue<int>() ?? 960, 16, 7680);
        if (at < Flicks.Zero || at >= sequence.Duration)
        {
            throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(at)} is outside '{sequence.Name}', which is {Timecode.FormatClock(sequence.Duration)} long.");
        }

        (int drawnWidth, int drawnHeight, byte[] bgra) frame;
        lock (_renderGate)
        {
            _renderer ??= new StillRenderer();
            frame = _renderer.RenderPreview(project, sequence, at, width, _target.Session.ProjectPath, args["node"]?.GetValue<string>());
        }

        byte[] png = PngWriter.Encode(frame.drawnWidth, frame.drawnHeight, frame.bgra);
        return new JsonObject
        {
            ["width"] = frame.drawnWidth,
            ["height"] = frame.drawnHeight,
            ["format"] = "png",
            ["at"] = at.Value,
            ["data"] = Convert.ToBase64String(png),
        };
    }

    /// <summary>
    /// A contact sheet as <c>export.contact-sheet</c> draws it, sent rather than written, so a
    /// client on another machine gets the picture and not a path on this one.
    /// </summary>
    private JsonObject ContactSheet(JsonObject args)
    {
        Project project = _target.Session.Project;
        Sequence sequence = (args["sequence"]?.GetValue<string>() is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", "There is no such sequence.");
        Rational rate = project.SettingsFor(sequence).FrameRate;
        Flicks? start = TimeOf(args["start"], rate, "start");
        Flicks? end = TimeOf(args["end"], rate, "end");
        double[]? fractions = args["times"] is JsonArray times ? [.. times.Select(time => time?.GetValue<double>() ?? 0)] : null;

        ContactSheetImage sheet;
        lock (_renderGate)
        {
            _renderer ??= new StillRenderer();
            sheet = StillExport.DrawContactSheet(
                project,
                _target.Session.ProjectPath,
                _renderer,
                args["columns"]?.GetValue<int>() ?? 4,
                args["rows"]?.GetValue<int>() ?? 4,
                args["width"]?.GetValue<int>() ?? 1920,
                sequence.Id,
                ExportOverrideText.Range(start, end),
                fractions: fractions,
                clipId: args["clip"]?.GetValue<string>());
        }

        byte[] png = PngWriter.Encode(sheet.Width, sheet.Height, sheet.Bgra);
        return new JsonObject
        {
            ["width"] = sheet.Width,
            ["height"] = sheet.Height,
            ["format"] = "png",
            ["times"] = new JsonArray([.. sheet.Times.Select(time => (JsonNode?)time.Value)]),
            ["data"] = Convert.ToBase64String(png),
        };
    }

    /// <summary>A time given as text (timecode, seconds, frames) or as flicks, or null when it is not given.</summary>
    private static Flicks? TimeOf(JsonNode? node, Rational rate, string name) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out long flicks) => new Flicks(flicks),
        JsonValue value when value.TryGetValue(out string? text) => (Flicks)CommandValues.Parse(typeof(Flicks), text, rate, name)!,
        _ => throw new JsonRpcException(JsonRpc.InvalidParams, $"\"{name}\" is a time as text or flicks."),
    };

    /// <summary>Every method, with what it takes: what <c>jazz rpc list</c> prints.</summary>
    public static JsonArray Catalog()
    {
        var methods = new JsonArray();
        foreach ((string name, string description) in BuiltIns)
        {
            methods.Add(new JsonObject { ["name"] = name, ["kind"] = "session", ["description"] = description });
        }

        foreach (CommandMetadata metadata in CommandRegistry.All)
        {
            methods.Add(new JsonObject
            {
                ["name"] = metadata.Name,
                ["kind"] = metadata.IsQuery ? "query" : "command",
                ["description"] = metadata.Description,
                ["undoable"] = metadata.IsQuery ? null : metadata.Undoable,
                ["params"] = new JsonArray([.. metadata.Parameters.Select(parameter => (JsonNode?)new JsonObject
                {
                    ["name"] = parameter.JsonName,
                    ["type"] = TypeName(parameter.Type),
                    ["required"] = parameter.IsRequired,
                    ["description"] = parameter.Description,
                })]),
            });
        }

        return methods;
    }

    /// <summary>The session methods that are not registry commands.</summary>
    public static IReadOnlyList<(string Name, string Description)> BuiltIns { get; } =
    [
        ("rpc.list", "Every method, with its params"),
        ("session.hello", "Name the connection ({client}), and give the token when TCP asks for one"),
        ("session.info", "The open project: name, path, version, unsaved changes, frame rate, size, lock, clients"),
        ("session.project", "The open project as its .jazz document, the JSON the file would hold"),
        ("session.subscribe", "Hear events: {events: [...]} from " + string.Join(", ", EventNames)),
        ("session.unsubscribe", "Stop hearing events: {events: [...]}, or all of them"),
        ("session.lock", "Hold the session for a batch of your own: {reason, seconds}; others are refused until you let go or it runs out"),
        ("session.unlock", "Let go of the session"),
        ("session.open", "Open a project: {path, discard}"),
        ("session.save", "Save the project: {path} to save as"),
        ("render.frame", "A frame as the preview draws it, as a base64 PNG: {at, width, sequence, node}"),
        ("render.contact-sheet", "A contact sheet as export.contact-sheet draws it, as a base64 PNG: {columns, rows, width, sequence, start, end, times, clip}"),
    ];

    private static string TypeName(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        return bare switch
        {
            _ when bare == typeof(Flicks) => "time",
            _ when bare == typeof(Rational) => "rate",
            _ when bare == typeof(TimeRange) => "range",
            _ when bare.IsEnum => string.Join("|", Enum.GetNames(bare).Select(System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName)),
            _ => bare.Name,
        };
    }

    private static IEnumerable<string> Events(JsonObject args)
    {
        if (args["events"] is not JsonArray list)
        {
            throw new JsonRpcException(JsonRpc.InvalidParams, "Say which events: {\"events\": [\"project.changed\"]}, or [\"*\"] for all.");
        }

        string[] names = [.. list.Select(item => item?.GetValue<string>() ?? string.Empty)];
        if (names.Contains("*"))
        {
            return EventNames;
        }

        string[] unknown = [.. names.Where(name => !EventNames.Contains(name))];
        return unknown.Length == 0
            ? names
            : throw new JsonRpcException(JsonRpc.InvalidParams, $"There is no event {string.Join(", ", unknown)}; there are {string.Join(", ", EventNames)}.");
    }

    private static string Text(JsonObject args, string name) =>
        args[name]?.GetValue<string>() ?? throw new JsonRpcException(JsonRpc.InvalidParams, $"This needs \"{name}\".");

    private static string Clean(string name) =>
        new([.. name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(40)]);

    private static JsonObject CommandData(string code, string path, long version)
    {
        var data = new JsonObject { ["code"] = code, ["version"] = version };
        if (path.Length > 0)
        {
            data["path"] = path;
        }

        return data;
    }

    private static JsonObject RateLimited(JsonNode? id, TimeSpan wait) =>
        JsonRpc.Error(id, JsonRpc.RateLimited, "Too many commands too fast.", new JsonObject { ["retryAfterMs"] = (int)Math.Ceiling(wait.TotalMilliseconds) });

    private Rational FrameRate()
    {
        Project project = _target.Session.Project;
        return project.ActiveSequence is { } active ? project.SettingsFor(active).FrameRate : project.Settings.FrameRate;
    }

    private NamedPipeServerStream CreateFirstPipe(out string name)
    {
        string wanted = _options.PipeName ?? ControlServerOptions.DefaultPipeName;
        try
        {
            name = wanted;
            return CreatePipe(wanted, first: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException && _options.PipeName is null)
        {
            // Another instance has the default name; this one is findable by its pid, and by a
            // number after it should one process host more than one server.
            for (int attempt = 1; ; attempt++)
            {
                name = attempt == 1
                    ? $"{ControlServerOptions.DefaultPipeName}-{Environment.ProcessId}"
                    : $"{ControlServerOptions.DefaultPipeName}-{Environment.ProcessId}-{attempt}";
                try
                {
                    return CreatePipe(name, first: true);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException && attempt < 16)
                {
                }
            }
        }
    }

    /// <summary>A pipe the current user may open and nobody over the network may.</summary>
    private static NamedPipeServerStream CreatePipe(string name, bool first)
    {
        var security = new PipeSecurity();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(user.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task PipeLoopAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? waiting = first;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                waiting ??= CreatePipe(PipeName!, first: false);
                await waiting.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                NamedPipeServerStream connected = waiting;
                waiting = null;
                Serve(connected, "pipe", requiresHello: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log.Warning(error, "The control pipe failed to take a connection");
                await (waiting?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
                waiting = null;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        await (waiting?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }

    private async Task TcpLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await _tcp!.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                client.NoDelay = true;
                Serve(client.GetStream(), "tcp", requiresHello: true);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
        }
    }

    private void Serve(Stream stream, string transport, bool requiresHello)
    {
        var connection = new ControlConnection(
            new LineChannel(stream),
            transport,
            requiresHello,
            $"client{Interlocked.Increment(ref _clientNumber)}",
            new RateLimiter(_options.CommandsPerSecond, _options.CommandBurst),
            _options.PlayheadInterval);
        _connections[connection] = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.RunAsync(this, _options.ChunkChars).ConfigureAwait(false);
            }
            finally
            {
                _connections.TryRemove(connection, out _);
                if (connection.HoldsLock)
                {
                    _target.Session.Unlock(connection.Issuer);
                }

                await connection.Channel.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    private void Subscribe()
    {
        _target.Session.ProjectChanged += OnProjectChanged;
        _target.Session.CommandCompleted += OnCommandCompleted;
        if (_target.Selection is { } selection)
        {
            selection.Changed += OnSelectionChanged;
        }

        if (_target.Playback is { } playback)
        {
            playback.PlayheadMoved += OnPlayheadMoved;
        }

        if (_target.Exports is { } exports)
        {
            exports.Changed += OnExportChanged;
        }

        if (LogSetup.RingBuffer is { } log)
        {
            log.EntryWritten += OnLog;
        }
    }

    private void Unsubscribe()
    {
        _target.Session.ProjectChanged -= OnProjectChanged;
        _target.Session.CommandCompleted -= OnCommandCompleted;
        if (_target.Selection is { } selection)
        {
            selection.Changed -= OnSelectionChanged;
        }

        if (_target.Playback is { } playback)
        {
            playback.PlayheadMoved -= OnPlayheadMoved;
        }

        if (_target.Exports is { } exports)
        {
            exports.Changed -= OnExportChanged;
        }

        if (LogSetup.RingBuffer is { } log)
        {
            log.EntryWritten -= OnLog;
        }
    }

    private void Broadcast(string name, Func<JsonObject> payload)
    {
        JsonObject? built = null;
        if (EventRaised is { } local)
        {
            built = payload();
            local(this, new ControlEvent(name, (JsonObject)built.DeepClone()));
        }

        foreach (ControlConnection connection in _connections.Keys)
        {
            if (connection.IsSubscribed(name))
            {
                built ??= payload();
                connection.Notify(name, built.DeepClone());
            }
        }
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs args)
    {
        Broadcast("project.changed", () => new JsonObject
        {
            ["version"] = args.Version,
            ["command"] = args.CommandName,
            ["changedIds"] = new JsonArray([.. args.ChangedIds.Select(id => (JsonNode?)id)]),
            ["origin"] = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(args.Origin.ToString()),
            ["issuer"] = args.Issuer,
        });

        if (args.Origin == ChangeOrigin.Load)
        {
            Broadcast("session.opened", () => new JsonObject { ["path"] = _target.Session.ProjectPath, ["name"] = args.Project.Name, ["version"] = args.Version });
            if (_options.Register && _started)
            {
                InstanceRegistry.Register(Describe(), _options.RegistryPath);
            }
        }
    }

    private void OnCommandCompleted(object? sender, CommandCompletedEventArgs args) =>
        Broadcast("command.completed", () => new JsonObject
        {
            ["command"] = args.Name,
            ["args"] = args.Args.DeepClone(),
            ["issuer"] = args.Issuer,
            ["ok"] = args.Result.Ok,
            ["code"] = args.Result.Code,
            ["error"] = args.Result.Error,
            ["version"] = args.Result.Version,
            ["changedIds"] = new JsonArray([.. args.Result.ChangedIds.Select(id => (JsonNode?)id)]),
            ["ms"] = Math.Round(args.Elapsed.TotalMilliseconds, 2),
        });

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args) =>
        Broadcast("selection.changed", () => new JsonObject { ["ids"] = new JsonArray([.. args.Ids.Select(id => (JsonNode?)id)]) });

    private void OnPlayheadMoved(object? sender, PlayheadMovedEventArgs args)
    {
        foreach (ControlConnection connection in _connections.Keys)
        {
            if (connection.IsSubscribed("playhead.moved"))
            {
                connection.Playhead(new JsonObject
                {
                    ["position"] = args.Position.Value,
                    ["timecode"] = Timecode.FormatClock(args.Position),
                    ["frame"] = args.Frame,
                    ["state"] = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(args.State.ToString()),
                    ["rate"] = args.Rate,
                });
            }
        }
    }

    private void OnExportChanged(object? sender, ExportJobInfo job)
    {
        Broadcast("export.progress", () => (JsonObject)JsonRpc.ToNode(job)!);
        if (job.IsFinished)
        {
            Broadcast("export.done", () => (JsonObject)JsonRpc.ToNode(job)!);
        }
    }

    private void OnLog(LogEntry entry) =>
        Broadcast("log", () => new JsonObject
        {
            ["at"] = entry.Timestamp,
            ["level"] = entry.Level.ToString().ToLowerInvariant(),
            ["source"] = entry.Source,
            ["message"] = entry.Message,
        });
}

/// <summary>One client of the control server.</summary>
internal sealed class ControlConnection(LineChannel channel, string transport, bool requiresHello, string name, RateLimiter limiter, TimeSpan playheadInterval)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);
    private JsonObject? _pendingPlayhead;
    private long _lastPlayhead;
    private bool _playheadScheduled;

    public LineChannel Channel { get; } = channel;

    public string Transport { get; } = transport;

    public bool RequiresHello { get; set; } = requiresHello;

    public string Name { get; set; } = name;

    /// <summary>
    /// Who the history and the console say made a change: the editor's own MCP server and command
    /// line by their names, as they are recorded when they run headless, so Claude Code is `mcp`
    /// either way; any other client as `rpc:` and the name it gave. Never `gui` or another of
    /// the editor's own, whatever a client calls itself.
    /// </summary>
    public string Issuer => FixedIssuer ?? (Name is "mcp" or "cli" ? Name : $"rpc:{Name}");

    /// <summary>An issuer that is not a remote client's, for the editor's own console.</summary>
    public string? FixedIssuer { get; init; }

    public RateLimiter Limiter { get; } = limiter;

    public bool HoldsLock { get; set; }

    /// <summary>Close once the answer in hand has been sent: a wrong token.</summary>
    public bool CloseAfterReply { get; set; }

    public async Task RunAsync(ControlServer server, int chunkChars)
    {
        while (await Channel.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException error)
            {
                await Channel.SendAsync(JsonRpc.Line(JsonRpc.Error(null, JsonRpc.ParseError, $"That is not JSON: {error.Message}"))).ConfigureAwait(false);
                continue;
            }

            if (await server.HandleAsync(this, message).ConfigureAwait(false) is { } answer)
            {
                await SendAsync(answer, chunkChars).ConfigureAwait(false);
            }

            if (CloseAfterReply)
            {
                await Channel.FlushAndCloseAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>A response, in slices when it is too long for one frame.</summary>
    private async Task SendAsync(JsonNode answer, int chunkChars)
    {
        string text = JsonRpc.Line(answer);
        if (text.Length <= chunkChars || answer is not JsonObject { } single || single["id"] is not { } id)
        {
            await Channel.SendAsync(text).ConfigureAwait(false);
            return;
        }

        int count = (text.Length + chunkChars - 1) / chunkChars;
        for (int index = 0; index < count; index++)
        {
            string slice = text.Substring(index * chunkChars, Math.Min(chunkChars, text.Length - (index * chunkChars)));
            await Channel.SendAsync(JsonRpc.Line(JsonRpc.Notification(JsonRpc.ChunkMethod, new JsonObject
            {
                ["id"] = id.DeepClone(),
                ["index"] = index,
                ["count"] = count,
                ["data"] = slice,
            }))).ConfigureAwait(false);
        }
    }

    public bool IsSubscribed(string name)
    {
        lock (_gate)
        {
            return _events.Contains(name);
        }
    }

    public JsonObject Subscribe(IEnumerable<string> names, bool add)
    {
        lock (_gate)
        {
            foreach (string name in names)
            {
                if (add)
                {
                    _events.Add(name);
                }
                else
                {
                    _events.Remove(name);
                }
            }

            return new JsonObject { ["events"] = new JsonArray([.. _events.Order(StringComparer.Ordinal).Select(name => (JsonNode?)name)]) };
        }
    }

    public void Notify(string method, JsonNode payload) => Channel.TrySend(JsonRpc.Line(JsonRpc.Notification(method, payload)));

    /// <summary>The playhead, at most ten times a second: the latest one, never a backlog.</summary>
    public void Playhead(JsonObject payload)
    {
        lock (_gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastPlayhead, now) >= playheadInterval)
            {
                _lastPlayhead = now;
                Notify("playhead.moved", payload);
                return;
            }

            _pendingPlayhead = payload;
            if (_playheadScheduled)
            {
                return;
            }

            _playheadScheduled = true;
        }

        _ = Task.Delay(playheadInterval).ContinueWith(
            _ =>
            {
                JsonObject? latest;
                lock (_gate)
                {
                    latest = _pendingPlayhead;
                    _pendingPlayhead = null;
                    _playheadScheduled = false;
                    _lastPlayhead = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (latest is not null)
                {
                    Notify("playhead.moved", latest);
                }
            },
            TaskScheduler.Default);
    }
}
