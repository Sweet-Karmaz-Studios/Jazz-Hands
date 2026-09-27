using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Control;
using JazzHands.Core.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog.Extensions.Logging;

namespace JazzHands.Mcp;

/// <summary>How <c>jazz mcp</c> was started.</summary>
/// <param name="Attach">Attach to a running editor rather than open a project here.</param>
/// <param name="Target">Which editor: empty for the newest, <c>pipe:&lt;name&gt;</c>, a pid, or host:port.</param>
/// <param name="Project">Headless: the project to open, or to start at that path when it does not exist.</param>
/// <param name="SaveOnExit">Headless: save the project when the client goes, if it changed.</param>
/// <param name="RegistryPath">The instance list, or null for the per-user one.</param>
/// <param name="Serve">Headless: how the session's own control server listens, or null for the default pipe.</param>
public sealed record McpHostOptions(bool Attach, string? Target = null, string? Project = null, bool SaveOnExit = false, string? RegistryPath = null, ControlServerOptions? Serve = null);

/// <summary>
/// The MCP server: <c>jazz mcp</c> and jazz-mcp.exe. Tools from <see cref="JazzTools"/>, the
/// <c>jazz://</c> resources, and the <c>build_trailer</c> and <c>review_cut</c> prompts, over stdio.
/// </summary>
/// <remarks>
/// Attached, every call goes to the editor the person is looking at, as the client <c>mcp</c>
/// (history and the Command Console show <c>rpc:mcp</c>), and the timeline outlines what changed.
/// Headless, the project is opened here with a control server of its own on a pipe, so
/// <c>jazz --attach</c> can look in; history says <c>mcp</c>.
/// </remarks>
public static class McpHost
{
    /// <summary>What the server tells a client about itself on connecting.</summary>
    public const string Instructions =
        "Jazz Hands is a video editor. Work in a loop: describe_timeline to read the project, edit with the clip_*, title_*, audio_* and other tools "
        + "(apply_batch for several steps at once, one undo step), render_frame or contact_sheet to look at the result, render_proof to watch it. "
        + "Times are strings like 00:00:02.500, 2.5s or 75f. Ids come from the tools' changedIds and from describe_timeline with detail full. "
        + "Every edit can be undone with undo. jazz://docs/workflow is the manual; jazz://docs/<area> documents each area's tools with examples. "
        + "jazz://events/recent shows what the person at the editor did by hand.";

    /// <summary>The version the server reports.</summary>
    public static string Version { get; } = typeof(McpHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>Connects to an editor, or opens the project headless.</summary>
    public static async Task<EditorLink> LinkAsync(McpHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Attach
            ? await AttachedLink.ConnectAsync(options.Target, options.RegistryPath, cancellationToken).ConfigureAwait(false)
            : await HeadlessLink.OpenAsync(options.Project, options.SaveOnExit, options.Serve ?? new ControlServerOptions { Name = "jazz mcp", RegistryPath = options.RegistryPath }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The server's options over a link: capabilities, instructions and every handler.</summary>
    public static McpServerOptions Options(EditorLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var tools = new JazzTools(link);

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "jazz", Title = "Jazz Hands", Version = Version },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Resources = new ResourcesCapability(),
                Prompts = new PromptsCapability(),
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = JazzTools.Definitions() }),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    CallToolRequestParams call = request.Params ?? throw new McpProtocolException("A tool call needs params.", McpErrorCode.InvalidParams);
                    JsonObject args = call.Arguments is { } given ? JsonSerializer.SerializeToNode(given)?.AsObject() ?? [] : [];
                    ProgressToken? token = call.ProgressToken;
                    McpServer server = request.Server;
                    return await tools.CallAsync(
                        call.Name,
                        args,
                        (progress, total, message) => token is { } known
                            ? server.NotifyProgressAsync(known, new ProgressNotificationValue { Progress = (float)progress, Total = total is { } whole ? (float)whole : null, Message = message }, cancellationToken: cancellationToken)
                            : Task.CompletedTask,
                        cancellationToken).ConfigureAwait(false);
                },
                ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult { Resources = Resources() }),
                ReadResourceHandler = async (request, cancellationToken) =>
                {
                    string uri = request.Params?.Uri ?? throw new McpProtocolException("Which resource?", McpErrorCode.InvalidParams);
                    (string mime, string text) = await ReadAsync(link, uri, cancellationToken).ConfigureAwait(false);
                    return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = mime, Text = text }] };
                },
                ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult { Prompts = McpPrompts.Definitions() }),
                GetPromptHandler = async (request, cancellationToken) =>
                {
                    GetPromptRequestParams prompt = request.Params ?? throw new McpProtocolException("Which prompt?", McpErrorCode.InvalidParams);
                    Dictionary<string, string> args = prompt.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() ?? string.Empty : pair.Value.ToString(), StringComparer.Ordinal) ?? [];
                    return await McpPrompts.GetAsync(tools, prompt.Name, args, cancellationToken).ConfigureAwait(false);
                },
            },
        };
    }

    /// <summary>Runs the server on stdin and stdout, or on the given streams, until the client goes.</summary>
    public static async Task RunAsync(EditorLink link, Stream? input = null, Stream? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        McpServerOptions options = Options(link);
        using var logging = new SerilogLoggerFactory(Serilog.Log.Logger);
        ITransport transport = input is not null && output is not null
            ? new StreamServerTransport(input, output, "jazz", logging)
            : new StdioServerTransport(options, logging);
        await using (transport.ConfigureAwait(false))
        {
            await using McpServer server = McpServer.Create(transport, options, logging);
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The resources a client can read.</summary>
    public static IList<Resource> Resources()
    {
        List<Resource> resources =
        [
            new() { Uri = "jazz://project", Name = "project", Description = "The open project as its .jazz JSON document", MimeType = "application/json" },
            new() { Uri = "jazz://schema", Name = "schema", Description = "The JSON schema of a .jazz project file", MimeType = "application/schema+json" },
            new() { Uri = "jazz://presets", Name = "presets", Description = "The export presets, built in and your own", MimeType = "application/json" },
            new() { Uri = "jazz://events/recent", Name = "recent events", Description = "The last 100 things that happened in the editor, including what the person did by hand", MimeType = "application/json" },
            new() { Uri = "jazz://cli-reference", Name = "cli reference", Description = "The jazz command line reference", MimeType = "text/markdown" },
            new() { Uri = "jazz://docs", Name = "docs", Description = "The manual's index: every area and its tools", MimeType = "text/markdown" },
            new() { Uri = "jazz://docs/workflow", Name = "workflow", Description = "How to edit with these tools: the loop, times, ids, undo, working alongside a person", MimeType = "text/markdown" },
            new() { Uri = "jazz://transcript", Name = "transcript", Description = "What is said in the active sequence, a line per sentence, each word after its index; jazz://transcript/<clipId> for one clip. speech_transcribe hears it first", MimeType = "text/plain" },
        ];
        resources.AddRange(McpDocs.Areas.Select(area => new Resource
        {
            Uri = $"jazz://docs/{area}",
            Name = $"docs/{area}",
            Description = $"The {area} tools: what they do, their arguments, examples",
            MimeType = "text/markdown",
        }));
        return resources;
    }

    /// <summary>Reads a resource.</summary>
    public static async Task<(string Mime, string Text)> ReadAsync(EditorLink link, string uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(uri);
        var indented = new JsonSerializerOptions(JsonRpc.Options) { WriteIndented = true };

        switch (uri)
        {
            case "jazz://project":
                JsonNode? project = await link.CallAsync("session.project", [], cancellationToken).ConfigureAwait(false);
                return ("application/json", project?.ToJsonString(indented) ?? "{}");
            case "jazz://schema":
                return ("application/schema+json", SchemaGenerator.Text);
            case "jazz://presets":
                JsonNode? presets = await link.CallAsync("presets.list", [], cancellationToken).ConfigureAwait(false);
                return ("application/json", presets?["data"]?.ToJsonString(indented) ?? "[]");
            case "jazz://events/recent":
                return ("application/json", new JsonArray([.. link.RecentEvents().Select(item => (JsonNode?)item)]).ToJsonString(indented));
            case "jazz://cli-reference":
                return ("text/markdown", Embedded("mcpdoc:cli-reference") ?? "The CLI reference was not built in.");
            case "jazz://docs":
                return ("text/markdown", McpDocs.Index());
            case "jazz://docs/workflow":
                return ("text/markdown", McpDocs.Workflow());
        }

        if (uri == "jazz://transcript" || uri.StartsWith("jazz://transcript/", StringComparison.Ordinal))
        {
            string target = uri.Length > "jazz://transcript/".Length ? uri["jazz://transcript/".Length..] : string.Empty;
            JsonObject args = target.Length > 0 ? new JsonObject { ["targetId"] = target } : [];
            JsonNode? transcript = await link.CallAsync("speech.transcript", args, cancellationToken).ConfigureAwait(false);
            JsonNode? data = transcript?["data"] ?? transcript;
            string text = data?["text"]?.GetValue<string>() ?? string.Empty;
            if (data?["untranscribed"] is JsonArray missing && missing.Count > 0)
            {
                text += $"Not transcribed yet (speech_transcribe hears them): {string.Join(", ", missing.Select(id => id?.GetValue<string>()))}\n";
            }

            return ("text/plain", text.Length > 0 ? text : "Nothing is said here that has been transcribed.\n");
        }

        if (uri.StartsWith("jazz://docs/", StringComparison.Ordinal) && McpDocs.Page(uri["jazz://docs/".Length..]) is { } page)
        {
            return ("text/markdown", page);
        }

        throw new McpProtocolException($"There is no resource '{uri}'. resources/list names them.", McpErrorCode.InvalidParams);
    }

    /// <summary>What <c>jazz mcp --list-tools</c> prints.</summary>
    public static string ListTools(bool json)
    {
        if (json)
        {
            return new JsonArray([.. JazzTools.All.Select(tool => (JsonNode?)new JsonObject
            {
                ["name"] = tool.Name,
                ["area"] = tool.Area,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.Schema.DeepClone(),
            })]).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        return string.Join(Environment.NewLine, JazzTools.All.Select(tool => string.Create(CultureInfo.InvariantCulture, $"{tool.Name,-30} {tool.Description}"))) + Environment.NewLine;
    }

    private static string? Embedded(string name)
    {
        using Stream? stream = typeof(McpHost).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
