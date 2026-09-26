using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace JazzHands.Mcp;

/// <summary>
/// The MCP prompts: <c>build_trailer</c>, a plan for cutting a trailer with the tools, and
/// <c>review_cut</c>, the edit as it stands (its description and a contact sheet) with a request
/// for a critique that ends in commands.
/// </summary>
public static class McpPrompts
{
    /// <summary>The prompt definitions.</summary>
    public static IList<Prompt> Definitions() =>
    [
        new Prompt
        {
            Name = "build_trailer",
            Title = "Build a trailer",
            Description = "Cut a trailer from clips: bring in media, lay out beats, add titles and music, check frames, render a proof",
            Arguments =
            [
                new PromptArgument { Name = "clips", Description = "The footage: a folder, a glob or files", Required = true },
                new PromptArgument { Name = "length", Description = "How long, in seconds; 30 when left out" },
                new PromptArgument { Name = "music", Description = "A music file to cut to" },
                new PromptArgument { Name = "title", Description = "The end card's text, for example the game's name and 'Wishlist now'" },
            ],
        },
        new Prompt
        {
            Name = "review_cut",
            Title = "Review the cut",
            Description = "Look at the edit as it stands, as text and a contact sheet, and critique it with concrete commands",
            Arguments =
            [
                new PromptArgument { Name = "sequenceId", Description = "Which sequence; the active one when left out" },
                new PromptArgument { Name = "focus", Description = "What to look at hardest: pacing, titles, sound, colour" },
            ],
        },
    ];

    /// <summary>Fills in a prompt.</summary>
    public static async Task<GetPromptResult> GetAsync(JazzTools tools, string name, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(args);

        return name switch
        {
            "build_trailer" => BuildTrailer(args),
            "review_cut" => await ReviewCutAsync(tools, args, cancellationToken).ConfigureAwait(false),
            _ => throw new McpProtocolException($"There is no prompt '{name}'. prompts/list names them.", McpErrorCode.InvalidParams),
        };
    }

    private static GetPromptResult BuildTrailer(IReadOnlyDictionary<string, string> args)
    {
        string clips = args.TryGetValue("clips", out string? given) && given.Length > 0 ? given : "the footage in the project";
        string length = args.TryGetValue("length", out string? seconds) && seconds.Length > 0 ? seconds : "30";
        string music = args.TryGetValue("music", out string? track) && track.Length > 0 ? $"Cut to the music in {track}: bring it in, put it on an audio track from 0s, mark its beats with audio_beats, and lay the build's shots along them with edit_cut_to_beats." : "There is no music; keep the game's own sound and let it breathe.";
        string title = args.TryGetValue("title", out string? card) && card.Length > 0 ? card : "the game's name and \"Wishlist now\"";

        string text = string.Create(CultureInfo.InvariantCulture, $"""
            Build a {length} second trailer in Jazz Hands from {clips}. The person is watching the timeline, so work in visible steps.

            1. describe_timeline to see the project. If it has no media, media_add the footage (a folder or glob is fine), then media_list.
            2. Find the moments worth showing: probe_media and render_frame at candidate times; prefer action, faces and clear readable moments.
            3. Lay out the beats with apply_batch: an opening hook of 2 to 3 seconds, a build of short shots (1 to 2 s each) getting shorter, a peak, then the end card. clip_add takes the track, the time on the timeline (at), the media, where in the source to start (sourceIn) and how long (duration).
            4. {music}
            5. Titles: template_list, then template_apply: a hero-intro or feature-callout where they help, and the end-card template saying {title} for the last 3 to 4 seconds. title_add with the lower-third preset for a plain callout.
            6. Impact and polish, sparingly: vfx_apply_preset (a hit or heavy hit) at the peak; vfx_hide_static when the footage shows a game HUD; cuts on beats, and a transition.crossfade or transition.dip into the end card (transition_add).
            7. Check: describe_timeline, then render_frame at each title and at the cuts you care about; contact_sheet for the whole shape. Fix what looks wrong.
            8. render_proof to proof.mp4 and report its path, length and what you would change next.

            Keep each change undoable and say what you did after each step. Times are strings like 00:00:04.000 or 4s.
            """);

        return new GetPromptResult
        {
            Description = "A plan for cutting a trailer with the Jazz Hands tools",
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        };
    }

    private static async Task<GetPromptResult> ReviewCutAsync(JazzTools tools, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        var describe = new JsonObject { ["detail"] = "brief" };
        var sheet = new JsonObject { ["columns"] = 4, ["rows"] = 4, ["width"] = 1440 };
        if (args.TryGetValue("sequenceId", out string? sequence) && sequence.Length > 0)
        {
            describe["sequenceId"] = sequence;
            sheet["sequenceId"] = sequence;
        }

        CallToolResult described = await tools.CallAsync("describe_timeline", describe, static (_, _, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        CallToolResult pictured = await tools.CallAsync("contact_sheet", sheet, static (_, _, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);

        string focus = args.TryGetValue("focus", out string? wanted) && wanted.Length > 0 ? $" Look hardest at {wanted}." : string.Empty;
        var messages = new List<PromptMessage>
        {
            new() { Role = Role.User, Content = new TextContentBlock { Text = $"Review this cut as an editor would.{focus} Say what works, then what does not, and for each problem the exact tool calls that would fix it (with ids from the description). Do not make the changes until asked." } },
        };
        messages.AddRange(described.Content.Concat(pictured.Content).Select(block => new PromptMessage { Role = Role.User, Content = block }));

        return new GetPromptResult
        {
            Description = "The edit as it stands, with a request for a critique",
            Messages = messages,
        };
    }
}
