using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>The timeline's editing tools, with the keys Premiere and Resolve users expect.</summary>
public enum TimelineTool
{
    /// <summary>V: select, move, trim.</summary>
    Select,

    /// <summary>C: cut clips where clicked.</summary>
    Razor,

    /// <summary>B: trim an edge and ripple everything after it.</summary>
    Ripple,

    /// <summary>N: move the cut between two touching clips.</summary>
    Roll,

    /// <summary>Y: change which part of the source a clip shows, without moving it.</summary>
    Slip,

    /// <summary>U: move a clip, taking the time out of its neighbours.</summary>
    Slide,

    /// <summary>R: drag a clip's end to change its speed.</summary>
    RateStretch,

    /// <summary>H: drag to scroll.</summary>
    Hand,
}

/// <summary>
/// Which tool is in hand and whether snapping is on, shared by every timeline tab.
/// </summary>
/// <remarks>
/// View state, not project state: a tool is how the person's mouse behaves, so it is not a
/// command and not undoable. Every edit a tool makes is still a command.
/// </remarks>
public sealed partial class TimelineTools : ObservableObject
{
    /// <summary>The keymap actions that pick a tool, in toolbar order.</summary>
    public static readonly IReadOnlyList<(TimelineTool Tool, string Action, string Key, string Label)> All =
    [
        (TimelineTool.Select, "ui.tool.select", "V", "Select"),
        (TimelineTool.Razor, "ui.tool.razor", "C", "Razor"),
        (TimelineTool.Ripple, "ui.tool.ripple", "B", "Ripple trim"),
        (TimelineTool.Roll, "ui.tool.roll", "N", "Roll"),
        (TimelineTool.Slip, "ui.tool.slip", "Y", "Slip"),
        (TimelineTool.Slide, "ui.tool.slide", "U", "Slide"),
        (TimelineTool.RateStretch, "ui.tool.rate-stretch", "R", "Rate stretch"),
        (TimelineTool.Hand, "ui.tool.hand", "H", "Hand"),
    ];

    [ObservableProperty]
    private TimelineTool _tool = TimelineTool.Select;

    [ObservableProperty]
    private bool _snapping = true;

    /// <summary>What the tool is called, with its key, for the status line and tool tips.</summary>
    public static string Describe(TimelineTool tool)
    {
        (TimelineTool _, string _, string key, string label) = All.First(entry => entry.Tool == tool);
        return $"{label} ({key})";
    }

    /// <summary>The tool a keymap action picks, or null when it is not a tool action.</summary>
    public static TimelineTool? FromAction(string action) =>
        All.FirstOrDefault(entry => string.Equals(entry.Action, action, StringComparison.Ordinal)) is { Action: not null } found
            ? found.Tool
            : null;
}
