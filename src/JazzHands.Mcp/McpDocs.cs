using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;

namespace JazzHands.Mcp;

/// <summary>
/// The model's manual: <c>jazz://docs</c> and a page per area, <c>jazz://docs/clip</c> and the rest.
/// </summary>
/// <remarks>
/// A page is the area's own prose, written by hand in <c>Docs/mcp/&lt;area&gt;.md</c> and built
/// into the assembly, followed by every tool of the area: what it does, its arguments, and an
/// example call (<see cref="ToolExamples"/>). <c>jazz://docs/workflow</c> is the way of working.
/// </remarks>
public static class McpDocs
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The areas with a page, in name order.</summary>
    public static IReadOnlyList<string> Areas { get; } = [.. JazzTools.All.Select(tool => tool.Area).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The index page: the way of working, then every area and its tools.</summary>
    public static string Index()
    {
        var text = new StringBuilder();
        text.AppendLine("# Jazz Hands for models");
        text.AppendLine();
        text.AppendLine("Start with jazz://docs/workflow. Each area below has a page, jazz://docs/<area>, with every tool's arguments and an example call.");
        text.AppendLine();
        foreach (string area in Areas)
        {
            string[] tools = [.. JazzTools.All.Where(tool => tool.Area == area).Select(tool => tool.Name)];
            text.Append("- jazz://docs/").Append(area).Append(": ").AppendLine(string.Join(", ", tools));
        }

        return text.ToString();
    }

    /// <summary>The way of working, by hand.</summary>
    public static string Workflow() => Resource("workflow") ?? "# Workflow\n";

    /// <summary>An area's page, or null for no such area.</summary>
    public static string? Page(string area)
    {
        JazzTool[] tools = [.. JazzTools.All.Where(tool => tool.Area == area)];
        if (tools.Length == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        string? intro = Resource(area);
        text.AppendLine(intro?.TrimEnd() ?? $"# {area}");
        text.AppendLine();
        text.AppendLine("## Tools");
        foreach (JazzTool tool in tools)
        {
            text.AppendLine();
            text.Append("### ").AppendLine(tool.Name);
            text.AppendLine();
            text.AppendLine(tool.Description);
            text.AppendLine();
            if (tool.Schema["properties"] is JsonObject { Count: > 0 } properties)
            {
                HashSet<string> required = [.. (tool.Schema["required"] as JsonArray ?? []).Select(name => name!.GetValue<string>())];
                foreach ((string name, JsonNode? property) in properties)
                {
                    text.Append("- `").Append(name).Append("` (").Append(TypeOf(property)).Append(required.Contains(name) ? ", required" : string.Empty).Append(')');
                    if (property?["description"]?.GetValue<string>() is { Length: > 0 } description)
                    {
                        text.Append(": ").Append(description);
                    }

                    if (property?["default"] is { } fallback)
                    {
                        text.Append(" Default ").Append(fallback.ToJsonString()).Append('.');
                    }

                    text.AppendLine();
                }

                text.AppendLine();
            }

            text.AppendLine("Example:");
            text.AppendLine();
            text.Append("```json tool=").AppendLine(tool.Name);
            text.AppendLine(Example(tool).ToJsonString(Indented));
            text.AppendLine("```");
        }

        return text.ToString();
    }

    /// <summary>A tool's example arguments.</summary>
    public static JsonObject Example(JazzTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.Method is { } method)
        {
            return ToolExamples.For(CommandRegistry.Require(method));
        }

        return ToolExamples.Curated.TryGetValue(tool.Name, out string? written)
            ? JsonNode.Parse(written)!.AsObject()
            : [];
    }

    /// <summary>Every example on every page, as <c>(tool, arguments)</c>, read back out of the text.</summary>
    public static IEnumerable<(string Tool, JsonObject Args)> ExamplesInPages()
    {
        foreach (string area in Areas)
        {
            string page = Page(area)!;
            int at = 0;
            while ((at = page.IndexOf("```json tool=", at, StringComparison.Ordinal)) >= 0)
            {
                int nameEnd = page.IndexOf('\n', at);
                string name = page[(at + "```json tool=".Length)..nameEnd].Trim();
                int close = page.IndexOf("```", nameEnd, StringComparison.Ordinal);
                yield return (name, JsonNode.Parse(page[nameEnd..close])!.AsObject());
                at = close + 3;
            }
        }
    }

    private static string TypeOf(JsonNode? property) => property?["type"] switch
    {
        JsonArray kinds => string.Join(" or ", kinds.Select(kind => kind!.GetValue<string>())),
        JsonValue kind => property["enum"] is JsonArray values ? $"one of {string.Join(", ", values.Select(value => value!.GetValue<string>()))}" : kind.GetValue<string>(),
        _ => "object",
    };

    private static string? Resource(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"mcpdoc:{name}");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
