using System.Text.Json.Nodes;
using JazzHands.Control;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Mcp;

/// <summary>
/// Where the MCP server's calls go: an editor it attached to, or a session it opened itself.
/// </summary>
/// <remarks>
/// Either way a call is a JSON-RPC method and its params, answered as the control server answers
/// a client, so every tool is written once. Errors arrive as <see cref="JsonRpcException"/>.
/// </remarks>
public abstract class EditorLink : IAsyncDisposable
{
    /// <summary>How many events <c>jazz://events/recent</c> keeps.</summary>
    public const int RecentEventCount = 100;

    private readonly Lock _gate = new();
    private readonly Queue<JsonObject> _recent = new();

    /// <summary>"attached" or "headless".</summary>
    public abstract string Mode { get; }

    /// <summary>What the server calls this client's commands in history.</summary>
    public abstract string Issuer { get; }

    /// <summary>True when files the editor writes can be read here: always, until TCP from another machine.</summary>
    public virtual bool SharesFiles => true;

    /// <summary>Calls a method and returns its result.</summary>
    /// <exception cref="JsonRpcException">The editor answered with an error.</exception>
    public abstract Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancellationToken);

    /// <summary>The latest events, oldest first: what the person did by hand, among the rest.</summary>
    public IReadOnlyList<JsonObject> RecentEvents()
    {
        lock (_gate)
        {
            return [.. _recent.Select(item => (JsonObject)item.DeepClone())];
        }
    }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>Remembers an event.</summary>
    protected void Remember(string name, JsonNode? payload)
    {
        if (name == "playhead.moved")
        {
            return;
        }

        lock (_gate)
        {
            _recent.Enqueue(new JsonObject { ["at"] = DateTimeOffset.UtcNow, ["event"] = name, ["params"] = payload?.DeepClone() });
            while (_recent.Count > RecentEventCount)
            {
                _recent.Dequeue();
            }
        }
    }
}

/// <summary>A running editor or <c>jazz serve</c>, over the pipe or TCP.</summary>
public sealed class AttachedLink : EditorLink
{
    private readonly JazzClient _client;
    private readonly Task _events;

    private AttachedLink(JazzClient client)
    {
        _client = client;
        _events = Task.Run(ReadEventsAsync);
    }

    /// <inheritdoc />
    public override string Mode => "attached";

    /// <inheritdoc />
    public override string Issuer => _client.Issuer;

    /// <summary>Connects to <paramref name="target"/> (empty for the newest instance) and listens to its events.</summary>
    public static async Task<AttachedLink> ConnectAsync(string? target, string? registryPath = null, CancellationToken cancellationToken = default)
    {
        JazzClient client = await JazzClient.ConnectAsync(target, "mcp", TimeSpan.FromSeconds(5), registryPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.SubscribeAsync(ControlServer.EventNames.Where(name => name != "playhead.moved"), cancellationToken).ConfigureAwait(false);
            return new AttachedLink(client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public override Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancellationToken) =>
        _client.IsClosed
            ? throw new JsonRpcException(JsonRpc.InternalError, "The editor has gone: it closed, or 'jazz serve' stopped. Start it again and reconnect.")
            : _client.CallAsync(method, args, cancellationToken);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        await _events.ConfigureAwait(false);
    }

    private async Task ReadEventsAsync()
    {
        await foreach (JsonObject notification in _client.Events.ReadAllAsync().ConfigureAwait(false))
        {
            Remember(notification["method"]?.GetValue<string>() ?? "?", notification["params"]);
        }
    }
}

/// <summary>
/// A project opened in this process: <c>jazz mcp --project</c>. It serves the same methods through
/// a control server of its own, which also listens on a pipe, so <c>jazz --attach</c> can join in.
/// </summary>
public sealed class HeadlessLink : EditorLink
{
    private readonly ServiceProvider _services;
    private readonly bool _saveOnExit;

    private HeadlessLink(ServiceProvider services, Session session, ControlServer server, bool saveOnExit)
    {
        _services = services;
        Session = session;
        Server = server;
        _saveOnExit = saveOnExit;
        server.EventRaised += (_, raised) => Remember(raised.Name, raised.Payload);
    }

    /// <summary>The session.</summary>
    public Session Session { get; }

    /// <summary>The control server over it.</summary>
    public ControlServer Server { get; }

    /// <inheritdoc />
    public override string Mode => "headless";

    /// <inheritdoc />
    public override string Issuer => "mcp";

    /// <summary>
    /// Opens a project, or starts a new one that will be saved there when a path is given for a
    /// file that does not exist yet.
    /// </summary>
    public static async Task<HeadlessLink> OpenAsync(string? path, bool saveOnExit, ControlServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        string full = path is { Length: > 0 } ? Path.GetFullPath(path) : string.Empty;
        Project project;
        if (full.Length > 0 && File.Exists(full))
        {
            ProjectLoad load = ProjectFile.Load(full);
            project = load.IsLoadable
                ? load.Project
                : throw new JazzHands.Core.Commands.CommandException("project-invalid", $"'{full}' has errors and will not open. Run 'jazz validate' to see them.");
        }
        else
        {
            project = Project.CreateNew(full.Length > 0 ? Path.GetFileNameWithoutExtension(full) : "Untitled");
        }

        ServiceProvider services = new ServiceCollection()
            .AddJazzHandsEngine()
            .AddJazzHandsControlSettings()
            .AddSingleton<IExportService>(new ForegroundExportService())
            .BuildServiceProvider();
        var session = new Session(project, services, full, recovery: full.Length > 0) { DefaultIssuer = "mcp" };
        var server = new ControlServer(
            new ControlTarget { Session = session, Exports = services.GetRequiredService<IExportService>(), Kind = "mcp" },
            options ?? new ControlServerOptions { Name = "jazz mcp" });
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        return new HeadlessLink(services, session, server, saveOnExit);
    }

    /// <inheritdoc />
    public override async Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancellationToken)
    {
        JsonObject response = await Server.CallLocalAsync(method, args, Issuer).ConfigureAwait(false);
        if (response["error"] is JsonObject error)
        {
            throw new JsonRpcException(error["code"]?.GetValue<int>() ?? JsonRpc.InternalError, error["message"]?.GetValue<string>() ?? "It failed.", error["data"]?.DeepClone());
        }

        return response["result"]?.DeepClone();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_saveOnExit && Session.IsDirty && Session.ProjectPath.Length > 0)
        {
            await Session.ExecuteAsync(new JazzHands.Core.Commands.SaveProjectCommand()).ConfigureAwait(false);
        }

        await Server.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
