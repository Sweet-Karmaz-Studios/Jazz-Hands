using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JazzHands.Core.Commands;

namespace JazzHands.Control;

/// <summary>What a remote command did.</summary>
/// <param name="Version">The project version after it.</param>
/// <param name="ChangedIds">What it touched.</param>
public sealed record RemoteResult(long Version, string[] ChangedIds);

/// <summary>An editor or <c>jazz serve</c> could not be reached.</summary>
public sealed class AttachException : Exception
{
    /// <summary>Creates one.</summary>
    public AttachException()
    {
    }

    /// <summary>Creates one with a message.</summary>
    public AttachException(string message)
        : base(message)
    {
    }

    /// <summary>Creates one with a message and a cause.</summary>
    public AttachException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A connection to a running editor's control server: call methods, send commands and queries as
/// their own records, hear events.
/// </summary>
/// <remarks>
/// <para>
/// A target is empty (the newest instance in <c>instances.json</c>, or the default pipe),
/// <c>pipe:&lt;name&gt;</c>, a process id, or <c>host:port</c> (or <c>tcp:host:port</c>) for TCP,
/// whose token comes from <c>instances.json</c> when the instance is on this machine, or from
/// <c>JAZZ_TOKEN</c>.
/// </para>
/// <para>
/// Typed calls are the registry's own records: <c>await client.ExecuteAsync(new SplitClipCommand(id,
/// at))</c> sends <c>clip.split</c> with the record's JSON, and <c>QueryAsync(new ListClipsQuery())</c>
/// comes back as <c>ClipInfo[]</c>. Calls may overlap; each waits for its own answer. A call the
/// server refuses as too fast is tried again when the server says.
/// </para>
/// </remarks>
public sealed class JazzClient : IAsyncDisposable
{
    private readonly LineChannel _channel;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<long, StringBuilder> _chunks = new();
    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _reader;
    private long _nextId;

    private JazzClient(LineChannel channel, InstanceInfo? instance)
    {
        _channel = channel;
        Instance = instance;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>The instance connected to, when it was found in <c>instances.json</c>.</summary>
    public InstanceInfo? Instance { get; }

    /// <summary>What the server said to <c>session.hello</c>.</summary>
    public JsonObject? Hello { get; private set; }

    /// <summary>The issuer the server records this client's commands as.</summary>
    public string Issuer => Hello?["issuer"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Events the client has subscribed to, as they arrive: <c>{method, params}</c>.</summary>
    public ChannelReader<JsonObject> Events => _events.Reader;

    /// <summary>True once the connection has gone.</summary>
    public bool IsClosed => _channel.IsClosed;

    /// <summary>Connects and says hello.</summary>
    /// <param name="target">Which instance; see the remarks.</param>
    /// <param name="client">What the server calls this client: its commands are <c>rpc:&lt;client&gt;</c>.</param>
    /// <param name="timeout">How long to wait for the server.</param>
    /// <param name="registryPath">The instance list, or null for the per-user one.</param>
    /// <param name="cancellationToken">Stops connecting.</param>
    /// <exception cref="AttachException">Nothing answered.</exception>
    public static async Task<JazzClient> ConnectAsync(
        string? target = null,
        string client = "client",
        TimeSpan? timeout = null,
        string? registryPath = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(5);
        (Stream stream, InstanceInfo? instance, string? token) = await OpenAsync(target, wait, registryPath, cancellationToken).ConfigureAwait(false);
        var connected = new JazzClient(new LineChannel(stream), instance);
        try
        {
            var hello = new JsonObject { ["client"] = client };
            if (token is not null)
            {
                hello["token"] = token;
            }

            connected.Hello = (JsonObject?)await connected.CallAsync("session.hello", hello, cancellationToken).ConfigureAwait(false);
            return connected;
        }
        catch
        {
            await connected.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Opens the stream to an instance without speaking to it, for <c>jazz rpc stdio</c>, which
    /// passes lines through as they are.
    /// </summary>
    public static async Task<(Stream Stream, InstanceInfo? Instance, string? Token)> OpenAsync(
        string? target,
        TimeSpan timeout,
        string? registryPath = null,
        CancellationToken cancellationToken = default)
    {
        string wanted = (target ?? string.Empty).Trim();
        IReadOnlyList<InstanceInfo> instances = InstanceRegistry.List(registryPath);

        try
        {
            if (wanted.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase))
            {
                string name = wanted[5..];
                return (await PipeAsync(name, timeout, cancellationToken).ConfigureAwait(false), instances.FirstOrDefault(instance => string.Equals(instance.Pipe, name, StringComparison.OrdinalIgnoreCase)), null);
            }

            if (int.TryParse(wanted, out int pid))
            {
                InstanceInfo instance = instances.FirstOrDefault(candidate => candidate.Pid == pid && candidate.Pipe is not null)
                    ?? throw new AttachException($"No running editor has process id {pid}. 'jazz rpc instances' lists them.");
                return (await PipeAsync(instance.Pipe!, timeout, cancellationToken).ConfigureAwait(false), instance, null);
            }

            string tcp = wanted.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? wanted[4..] : wanted;
            if (tcp.Length > 0 && IPEndPoint.TryParse(tcp, out IPEndPoint? endPoint))
            {
                InstanceInfo? instance = instances.FirstOrDefault(candidate => candidate.Tcp is { } known && IPEndPoint.TryParse(known, out IPEndPoint? listening) && listening.Port == endPoint.Port);
                string? token = Environment.GetEnvironmentVariable("JAZZ_TOKEN") is { Length: > 0 } given ? given : instance?.Token;
                var socket = new TcpClient { NoDelay = true };
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(timeout);
                await socket.ConnectAsync(endPoint, linked.Token).ConfigureAwait(false);
                return (socket.GetStream(), instance, token);
            }

            if (wanted.Length > 0)
            {
                throw new AttachException($"'{wanted}' is not a target: say pipe:<name>, a process id, or host:port.");
            }

            // The newest instance that has a pipe, else the default pipe.
            InstanceInfo? newest = instances.FirstOrDefault(candidate => candidate.Pipe is not null);
            string pipe = newest?.Pipe ?? ControlServerOptions.DefaultPipeName;
            return (await PipeAsync(pipe, timeout, cancellationToken).ConfigureAwait(false), newest, null);
        }
        catch (Exception error) when (error is TimeoutException or IOException or SocketException or OperationCanceledException or UnauthorizedAccessException && !cancellationToken.IsCancellationRequested)
        {
            throw new AttachException(
                wanted.Length == 0
                    ? "No editor is running to attach to. Open Jazz Hands, or run 'jazz serve <project>'."
                    : $"Nothing answered at '{wanted}': {error.Message}",
                error);
        }
    }

    /// <summary>Calls a method and waits for its result.</summary>
    /// <exception cref="JsonRpcException">The server answered with an error.</exception>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await CallOnceAsync(method, parameters?.DeepClone(), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonRpcException error) when (error.Code == JsonRpc.RateLimited && attempt < 50)
            {
                int after = error.Detail?["retryAfterMs"]?.GetValue<int>() ?? 50;
                await Task.Delay(Math.Clamp(after, 1, 5000), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs a command on the server.</summary>
    /// <exception cref="CommandException">The server refused it; the code and path are the server's.</exception>
    public async Task<RemoteResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        JsonNode? result = await CallCommandAsync(CommandRegistry.NameOf(command), CommandRegistry.ArgsToJson(command), cancellationToken).ConfigureAwait(false);
        return new RemoteResult(
            result?["version"]?.GetValue<long>() ?? 0,
            [.. (result?["changedIds"] as JsonArray ?? []).Select(id => id!.GetValue<string>())]);
    }

    /// <summary>Asks the server a query, answered as the query's own result type.</summary>
    public async Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        JsonNode? result = await CallCommandAsync(CommandRegistry.NameOf(query), CommandRegistry.ArgsToJson(query), cancellationToken).ConfigureAwait(false);
        JsonNode? data = result?["data"];
        return data is null ? default! : data.Deserialize<TResult>(JsonRpc.Options)!;
    }

    /// <summary>Calls a registry method, turning a command error back into a <see cref="CommandException"/>.</summary>
    public async Task<JsonNode?> CallCommandAsync(string method, JsonObject args, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync(method, args, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error) when (error.CommandCode.Length > 0)
        {
            throw new CommandException(error.CommandCode, error.Message, error.Detail?["path"]?.GetValue<string>() ?? string.Empty);
        }
    }

    /// <summary>Starts hearing events: names from <see cref="ControlServer.EventNames"/>, or "*".</summary>
    public Task SubscribeAsync(IEnumerable<string> events, CancellationToken cancellationToken = default) =>
        CallAsync("session.subscribe", new JsonObject { ["events"] = new JsonArray([.. events.Select(name => (JsonNode?)name)]) }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Close();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<JsonNode?> CallOnceAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (!await _channel.SendAsync(JsonRpc.Line(JsonRpc.Request(id, method, parameters)), cancellationToken).ConfigureAwait(false))
            {
                throw new AttachException("The editor has gone.");
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
            using CancellationTokenRegistration closed = _channel.Closed.Register(() => answer.TrySetException(new AttachException("The editor has gone.")));
            JsonObject response = await answer.Task.ConfigureAwait(false);
            if (response["error"] is JsonObject error)
            {
                throw new JsonRpcException(error["code"]?.GetValue<int>() ?? JsonRpc.InternalError, error["message"]?.GetValue<string>() ?? "The server failed.", error["data"]?.DeepClone());
            }

            return response["result"]?.DeepClone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _channel.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                foreach (JsonNode? item in message is JsonArray batch ? batch : [message])
                {
                    if (item is JsonObject single)
                    {
                        Route(single);
                    }
                }
            }
        }
        finally
        {
            _events.Writer.TryComplete();
            foreach (TaskCompletionSource<JsonObject> waiting in _pending.Values)
            {
                waiting.TrySetException(new AttachException("The editor has gone."));
            }
        }
    }

    private void Route(JsonObject message)
    {
        if (message["id"] is JsonValue idValue && idValue.TryGetValue(out long id) && message.ContainsKey("method") is false)
        {
            if (_pending.TryGetValue(id, out TaskCompletionSource<JsonObject>? waiting))
            {
                waiting.TrySetResult(message);
            }

            return;
        }

        string? method = message["method"]?.GetValue<string>();
        if (method == JsonRpc.ChunkMethod && message["params"] is JsonObject chunk)
        {
            long chunkId = chunk["id"]!.GetValue<long>();
            StringBuilder text = _chunks.GetOrAdd(chunkId, _ => new StringBuilder());
            text.Append(chunk["data"]!.GetValue<string>());
            if (chunk["index"]!.GetValue<int>() == chunk["count"]!.GetValue<int>() - 1)
            {
                _chunks.TryRemove(chunkId, out _);
                if (JsonNode.Parse(text.ToString()) is JsonObject whole)
                {
                    Route(whole);
                }
            }

            return;
        }

        if (method is not null)
        {
            _events.Writer.TryWrite(message);
        }
    }

    private static async Task<Stream> PipeAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
