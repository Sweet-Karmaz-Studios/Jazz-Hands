using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using ModelContextProtocol.Protocol;

namespace JazzHands.Mcp;

/// <summary>A tool: its definition for <c>tools/list</c>, and what calling it does.</summary>
/// <param name="Name">The tool's name.</param>
/// <param name="Area">The <c>jazz://docs</c> page it is written up on.</param>
/// <param name="Description">What it does, under 300 characters.</param>
/// <param name="Schema">Its input schema.</param>
/// <param name="Method">The registry name it runs, for a generated tool; null for a hand-written one.</param>
/// <param name="Covers">The registry name a hand-written tool stands in for, so it is not listed twice.</param>
/// <param name="ReadOnly">True when it changes nothing.</param>
public sealed record JazzTool(string Name, string Area, string Description, JsonObject Schema, string? Method, string? Covers, bool ReadOnly);

/// <summary>Progress a long tool reports, or nothing when the client did not ask.</summary>
public delegate Task ReportProgress(double progress, double? total, string message);

/// <summary>
/// Every tool the MCP server offers and what each does: one per registry command and query, and
/// the hand-written ones shaped for a model (images, a proof, a batch, waiting on an export).
/// </summary>
/// <remarks>
/// A generated tool is the registry method with the tool's arguments as params, answered with the
/// result as text and as structured content: <c>{ok, version, changedIds}</c> for a command,
/// <c>{ok, version, data}</c> for a query. An error is a result with <c>isError</c>, the command's
/// code, message and path, never an exception through the transport.
/// </remarks>
public sealed class JazzTools
{
    private static readonly JsonSerializerOptions Compact = new(JsonRpc.Options) { WriteIndented = false };

    private readonly EditorLink _link;
    private readonly Dictionary<string, JazzTool> _byName;

    /// <summary>Tools over a link to an editor.</summary>
    public JazzTools(EditorLink link)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _byName = All.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    /// <summary>Every tool, hand-written first, then the registry's in name order.</summary>
    public static ImmutableArray<JazzTool> All { get; } = Build();

    /// <summary>The tool with a name, or null.</summary>
    public static JazzTool? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);

    /// <summary>The MCP definitions.</summary>
    public static IList<Tool> Definitions() =>
    [
        .. All.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = JsonSerializer.SerializeToElement(tool.Schema),
            Annotations = new ToolAnnotations { ReadOnlyHint = tool.ReadOnly, IdempotentHint = tool.ReadOnly ? true : null },
        }),
    ];

    /// <summary>Calls a tool.</summary>
    public async Task<CallToolResult> CallAsync(string name, JsonObject args, ReportProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(progress);

        if (!_byName.TryGetValue(name, out JazzTool? tool))
        {
            return Failure("unknown-tool", $"There is no tool '{name}'. tools/list names them; jazz://docs explains them.");
        }

        try
        {
            return tool.Method is { } method
                ? Answer(await _link.CallAsync(method, args, cancellationToken).ConfigureAwait(false))
                : await CallHandWrittenAsync(tool.Name, args, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error)
        {
            return Failure(error.CommandCode.Length > 0 ? error.CommandCode : Code(error.Code), error.Message, error.Detail?["path"]?.GetValue<string>());
        }
        catch (CommandException error)
        {
            return Failure(error.Code, error.Message, error.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or JsonException or InvalidOperationException)
        {
            return Failure("failed", error.Message);
        }
    }

    private async Task<CallToolResult> CallHandWrittenAsync(string name, JsonObject args, ReportProgress progress, CancellationToken cancellationToken) => name switch
    {
        "describe_timeline" => await DescribeAsync(args, cancellationToken).ConfigureAwait(false),
        "render_frame" => await RenderFrameAsync(args, cancellationToken).ConfigureAwait(false),
        "contact_sheet" => await ContactSheetAsync(args, cancellationToken).ConfigureAwait(false),
        "render_proof" => await RenderProofAsync(args, progress, cancellationToken).ConfigureAwait(false),
        "probe_media" => Answer(await _link.CallAsync("media.probe", Pick(args, "path"), cancellationToken).ConfigureAwait(false)),
        "list_effects" => Answer(await _link.CallAsync("effect.list", [], cancellationToken).ConfigureAwait(false)),
        "list_presets" => Answer(await _link.CallAsync("presets.list", [], cancellationToken).ConfigureAwait(false)),
        "list_fonts" => Answer(await _link.CallAsync("fonts.list", [], cancellationToken).ConfigureAwait(false)),
        "list_title_presets" => Answer(await _link.CallAsync("title.list-presets", [], cancellationToken).ConfigureAwait(false)),
        "history" => Answer(await _link.CallAsync("history.list", Pick(args, "limit"), cancellationToken).ConfigureAwait(false)),
        "session_info" => Answer(await _link.CallAsync("session.info", [], cancellationToken).ConfigureAwait(false)),
        "apply_batch" => await ApplyBatchAsync(args, cancellationToken).ConfigureAwait(false),
        "wait_export" => await WaitExportAsync(Text(args, "jobId"), args["timeoutSeconds"]?.GetValue<double>() ?? 3600, progress, cancellationToken).ConfigureAwait(false),
        _ => Failure("unknown-tool", $"There is no tool '{name}'."),
    };

    private async Task<CallToolResult> DescribeAsync(JsonObject args, CancellationToken cancellationToken)
    {
        JsonNode? result = await _link.CallAsync("describe", Pick(args, "sequenceId", "range", "detail", "budget"), cancellationToken).ConfigureAwait(false);
        string text = result?["data"]?["text"]?.GetValue<string>() ?? string.Empty;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{text.TrimEnd()}\n(project version {result?["version"]})" }],
        };
    }

    private async Task<CallToolResult> RenderFrameAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var call = new JsonObject
        {
            ["at"] = args["at"]?.DeepClone() ?? throw new CommandException("missing-argument", "render_frame needs 'at', a time such as 00:00:12.500."),
            ["width"] = args["width"]?.DeepClone() ?? 960,
        };
        if (args["sequenceId"] is { } sequence)
        {
            call["sequence"] = sequence.DeepClone();
        }

        if (args["node"] is { } node)
        {
            call["node"] = node.DeepClone();
        }

        JsonNode? frame = await _link.CallAsync("render.frame", call, cancellationToken).ConfigureAwait(false);
        byte[] png = Convert.FromBase64String(frame!["data"]!.GetValue<string>());
        long at = frame["at"]!.GetValue<long>();
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock { Text = string.Create(CultureInfo.InvariantCulture, $"Frame at {Timecode.FormatClock(new Flicks(at))}, {frame["width"]}x{frame["height"]}, as the preview draws it.") },
            ],
        };
    }

    private async Task<CallToolResult> ContactSheetAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "jazz-mcp");
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, $"sheet-{Guid.NewGuid():N}.png");
        JsonObject call = Pick(args, "columns", "rows", "width", "sequenceId", "start", "end");
        call["output"] = output;
        try
        {
            await _link.CallAsync("export.contact-sheet", call, cancellationToken).ConfigureAwait(false);
            byte[] png = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            int columns = args["columns"]?.GetValue<int>() ?? 4;
            int rows = args["rows"]?.GetValue<int>() ?? 4;
            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(png, "image/png"),
                    new TextContentBlock { Text = string.Create(CultureInfo.InvariantCulture, $"{columns * rows} frames at even steps, left to right then down, each labelled with its time.") },
                ],
            };
        }
        finally
        {
            File.Delete(output);
        }
    }

    private async Task<CallToolResult> RenderProofAsync(JsonObject args, ReportProgress progress, CancellationToken cancellationToken)
    {
        JsonObject call = Pick(args, "sequenceId", "start", "end");
        call["output"] = Text(args, "out");
        call["preset"] = "proof";
        call["mode"] = "encode";
        JsonNode? queued = await _link.CallAsync("export.enqueue", call, cancellationToken).ConfigureAwait(false);
        string job = (queued?["changedIds"] as JsonArray)?.FirstOrDefault()?.GetValue<string>()
            ?? throw new CommandException("no-job", "The export was queued but no job id came back.");
        return await WaitExportAsync(job, 3600, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> WaitExportAsync(string jobId, double timeoutSeconds, ReportProgress progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 24 * 3600)));
        double last = -1;
        while (true)
        {
            JsonNode? list = await _link.CallAsync("export.list", [], timeout.Token).ConfigureAwait(false);
            JsonObject job = (list?["data"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(candidate => candidate["id"]?.GetValue<string>() == jobId)
                ?? throw new CommandException("job-not-found", $"There is no export job '{jobId}'. export_list shows the queue.");
            string state = job["state"]?.GetValue<string>() ?? string.Empty;
            double fraction = job["progress"]?.GetValue<double>() ?? 0;

            if (state is "done" or "failed" or "cancelled")
            {
                var summary = new JsonObject
                {
                    ["jobId"] = jobId,
                    ["state"] = state,
                    ["output"] = job["outputPath"]?.DeepClone(),
                    ["bytes"] = job["bytes"]?.DeepClone(),
                    ["frames"] = job["totalFrames"]?.DeepClone(),
                    ["encoder"] = job["encoder"]?.DeepClone(),
                    ["error"] = job["error"]?.DeepClone(),
                };
                return state == "done"
                    ? new CallToolResult { Content = [new TextContentBlock { Text = summary.ToJsonString(Compact) }], StructuredContent = JsonSerializer.SerializeToElement(summary) }
                    : Failure($"export-{state}", job["error"]?.GetValue<string>() ?? $"The export was {state}.");
            }

            if (fraction - last >= 0.01)
            {
                last = fraction;
                await progress(Math.Round(fraction * 100, 1), 100, string.Create(CultureInfo.InvariantCulture, $"{state}: frame {job["frame"]} of {job["totalFrames"]}")).ConfigureAwait(false);
            }

            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<CallToolResult> ApplyBatchAsync(JsonObject args, CancellationToken cancellationToken)
    {
        JsonArray steps = args["steps"] as JsonArray ?? throw new CommandException("missing-argument", "apply_batch needs 'steps': [{\"command\": \"clip.split\", \"args\": {...}}].");
        string label = args["label"]?.GetValue<string>() ?? "MCP batch";
        JsonArray normal = [.. steps.Select(step => (JsonNode?)Normalise(step))];

        if (!ScriptRunner.UsesReferences(normal))
        {
            // Nothing refers back: one batch command, one undo step, all or nothing.
            var commands = new JsonArray([.. normal.OfType<JsonObject>().Select(step => (JsonNode?)new JsonObject { ["command"] = step["command"]?.DeepClone(), ["args"] = step["args"]?.DeepClone() ?? new JsonObject() })]);
            JsonNode? result = await _link.CallAsync("batch", new JsonObject { ["commands"] = commands, ["label"] = label }, cancellationToken).ConfigureAwait(false);
            JsonObject answer = result as JsonObject ?? [];
            answer["mode"] = "one undo step";
            return Answer(answer);
        }

        // Steps that use what earlier ones made run one at a time, with the session held so
        // nobody edits in between; each is its own undo step.
        JsonNode? info = await _link.CallAsync("session.info", [], cancellationToken).ConfigureAwait(false);
        Rational rate = Rational.TryParse(info?["fps"]?.GetValue<string>(), out Rational parsed) ? parsed : Rational.Fps30;
        string project = info?["path"]?.GetValue<string>() ?? string.Empty;
        await _link.CallAsync("session.lock", new JsonObject { ["reason"] = label, ["seconds"] = 300 }, cancellationToken).ConfigureAwait(false);
        try
        {
            (List<ScriptStepResult> results, bool failed) = await ScriptRunner.RunAsync(
                normal,
                async (metadata, built) =>
                {
                    JsonNode? result = await _link.CallAsync(metadata.Name, CommandRegistry.ArgsToJson(built), cancellationToken).ConfigureAwait(false);
                    return metadata.IsQuery
                        ? new ScriptOutcome(true, [], result?["data"]?.DeepClone(), null, null)
                        : new ScriptOutcome(true, [.. (result?["changedIds"] as JsonArray ?? []).Select(id => id!.GetValue<string>())], null, null, null);
                },
                () => Task.FromResult(rate),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["projectDir"] = project.Length > 0 ? Path.GetDirectoryName(project) ?? "." : Directory.GetCurrentDirectory() }).ConfigureAwait(false);

            var summary = new JsonObject
            {
                ["ok"] = !failed,
                ["mode"] = "step by step, one undo step each",
                ["steps"] = new JsonArray([.. results.Select(result => (JsonNode?)result.ToJson())]),
            };
            return new CallToolResult
            {
                IsError = failed,
                Content = [new TextContentBlock { Text = summary.ToJsonString(Compact) }],
                StructuredContent = JsonSerializer.SerializeToElement(summary),
            };
        }
        finally
        {
            await _link.CallAsync("session.unlock", [], CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A step naming its command as a tool (<c>clip_split</c>) or as the registry does (<c>clip.split</c>).</summary>
    private static JsonNode? Normalise(JsonNode? step)
    {
        if (step is not JsonObject found || found["command"] is not JsonValue value || !value.TryGetValue(out string? command))
        {
            return step?.DeepClone();
        }

        JsonObject copy = found.DeepClone().AsObject();
        if (CommandRegistry.Find(command) is null && All.FirstOrDefault(tool => tool.Name == command)?.Method is { } method)
        {
            copy["command"] = method;
        }

        return copy;
    }

    private static CallToolResult Answer(JsonNode? result)
    {
        JsonNode shown = result is JsonObject { } answer && answer["data"] is { } data ? data : result ?? new JsonObject { ["ok"] = true };
        var content = new CallToolResult
        {
            Content = [new TextContentBlock { Text = shown.ToJsonString(Compact) }],
        };

        if (result is JsonObject structured)
        {
            content.StructuredContent = JsonSerializer.SerializeToElement(structured);
        }

        return content;
    }

    private static CallToolResult Failure(string code, string message, string? path = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = path is { Length: > 0 } ? $"{code}: {message} (at {path})" : $"{code}: {message}" }],
    };

    private static string Code(int code) => code switch
    {
        JsonRpc.Locked => "locked",
        JsonRpc.RateLimited => "rate-limited",
        JsonRpc.MediaError => "media-error",
        JsonRpc.MethodNotFound => "unknown-command",
        JsonRpc.InvalidParams => "invalid-argument",
        _ => "failed",
    };

    private static JsonObject Pick(JsonObject args, params string[] names)
    {
        var picked = new JsonObject();
        foreach (string name in names)
        {
            if (args[name] is { } value)
            {
                picked[name] = value.DeepClone();
            }
        }

        return picked;
    }

    private static string Text(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0
            ? text
            : throw new CommandException("missing-argument", $"'{name}' is needed.");

    private static ImmutableArray<JazzTool> Build()
    {
        List<JazzTool> hand = HandWritten();
        HashSet<string> covered = [.. hand.Select(tool => tool.Covers).OfType<string>()];
        IEnumerable<JazzTool> generated = CommandRegistry.All
            .Where(metadata => !covered.Contains(metadata.Name))
            .OrderBy(metadata => metadata.Name, StringComparer.Ordinal)
            .Select(metadata => new JazzTool(
                ToolSchema.ToolName(metadata.Name),
                DocArea(metadata.Area),
                ToolSchema.Description(metadata),
                ToolSchema.For(metadata),
                metadata.Name,
                null,
                metadata.IsQuery));
        return [.. hand, .. generated];
    }

    /// <summary>The docs page an area is written up on: undo and redo with history, scopes with colour.</summary>
    public static string DocArea(string area) => area switch
    {
        "undo" or "redo" => "history",
        "scopes" => "color",
        "camera" or "light" or "text3d" or "shape3d" or "model3d" => "3d",
        _ => area,
    };

    private static List<JazzTool> HandWritten()
    {
        static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
        const string Time = "a time: 00:00:02.500, 2.5s or 75f";

        return
        [
            new("describe_timeline", "describe",
                "Read the project as text: settings, media, the sequence's tracks with clips in order, gaps, transitions, markers and problems, in about 2000 tokens. Start here and come back after every few edits.",
                Schema($$$"""{"type":"object","properties":{"sequenceId":{"type":"string","description":"Which sequence; the active one when left out"},"range":{"type":"string","description":"Only this stretch: 00:00:10.000-00:00:25.000"},"detail":{"type":"string","enum":["brief","full"],"default":"brief","description":"full adds ids, sources, effects and every problem"},"budget":{"type":"integer","description":"At brief, about the most tokens to spend; 0 for no limit"}},"additionalProperties":false}"""),
                null, "describe", true),
            new("render_frame", "inspect",
                "Look at one frame exactly as the editor's preview draws it: a PNG. Use after layout, title, colour or transform changes to check the picture.",
                Schema($$$"""{"type":"object","properties":{"at":{"type":["string","integer"],"description":"When: {{{Time}}}"},"sequenceId":{"type":"string","description":"Which sequence; the active one when left out"},"width":{"type":"integer","default":960,"minimum":16,"maximum":7680,"description":"Width in pixels; the height follows the sequence"},"node":{"type":"string","description":"A comp graph node to look at instead of its graph's output"}},"required":["at"],"additionalProperties":false}"""),
                null, null, true),
            new("contact_sheet", "inspect",
                "See a whole edit at once: frames at even steps across a sequence tiled into one labelled PNG. Good for reviewing pacing and shot order.",
                Schema($$$"""{"type":"object","properties":{"columns":{"type":"integer","default":4},"rows":{"type":"integer","default":4},"width":{"type":"integer","default":1920,"description":"The sheet's width in pixels"},"sequenceId":{"type":"string"},"start":{"type":["string","integer"],"description":"From here: {{{Time}}}"},"end":{"type":["string","integer"],"description":"To here: {{{Time}}}"}},"additionalProperties":false}"""),
                null, null, true),
            new("render_proof", "export",
                "Export a quick 480p proof of a sequence (every frame rendered) and wait for it, with progress. Returns the file's path and size. Relative paths are beside the project.",
                Schema($$$"""{"type":"object","properties":{"out":{"type":"string","description":"The file to write: proof.mp4"},"sequenceId":{"type":"string"},"start":{"type":["string","integer"],"description":"From here: {{{Time}}}"},"end":{"type":["string","integer"],"description":"To here: {{{Time}}}"}},"required":["out"],"additionalProperties":false}"""),
                null, null, false),
            new("probe_media", "media",
                "Read a media file and say what is in it (streams, codecs, duration, frame rate, colour) without importing it.",
                Schema("""{"type":"object","properties":{"path":{"type":"string","description":"The file"}},"required":["path"],"additionalProperties":false}"""),
                null, "media.probe", true),
            new("list_effects", "effect", "List the effect types with their parameters, ranges and defaults, for effect_add and effect_set_param.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), null, "effect.list", true),
            new("list_presets", "presets", "List the export presets, built in and your own, for export_enqueue and render_proof.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), null, "presets.list", true),
            new("list_fonts", "fonts", "List the font families titles can use: installed, and the project's own.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), null, "fonts.list", true),
            new("list_title_presets", "title", "List the title presets (title-card, lower-third and the rest) and what each sets, for title_add.", Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), null, "title.list-presets", true),
            new("apply_batch", "batch",
                "Run several commands in one call. Without references between steps it is one undo step, all or nothing; steps may name results (\"as\") and use them ($name.id), and then run in order under the session lock.",
                Schema("""{"type":"object","properties":{"steps":{"type":"array","items":{"type":"object","properties":{"command":{"type":"string","description":"A command: clip.split or clip_split"},"args":{"type":"object","description":"Its arguments, as the tool of that name takes them"},"as":{"type":"string","description":"A name for what this step makes, used later as $name.id or $name.ids[1]"}},"required":["command"]}},"label":{"type":"string","description":"What to call it in the undo menu"}},"required":["steps"],"additionalProperties":false}"""),
                null, "batch", false),
            new("wait_export", "export",
                "Wait for an export job to finish, reporting progress; returns its file, size and encoder, or why it failed.",
                Schema("""{"type":"object","properties":{"jobId":{"type":"string","description":"The job, as export_enqueue's changedIds gave it"},"timeoutSeconds":{"type":"number","default":3600}},"required":["jobId"],"additionalProperties":false}"""),
                null, null, true),
            new("history", "history",
                "List what has been done, oldest first, with who did each: gui for the person at the editor, mcp for you, cli for the jazz command line, rpc:<name> for any other client.",
                Schema("""{"type":"object","properties":{"limit":{"type":"integer","default":50}},"additionalProperties":false}"""),
                null, "history.list", true),
            new("session_info", "inspect",
                "The open project: name, path, version, unsaved changes, frame rate, size, who holds the lock, and the clients connected.",
                Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                null, null, true),
        ];
    }
}
