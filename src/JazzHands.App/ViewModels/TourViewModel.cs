using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JazzHands.App.ViewModels;

/// <summary>One card of the tour: what it is about, and a few sentences on it.</summary>
/// <param name="Title">The card's heading.</param>
/// <param name="Text">What to know, in a few sentences.</param>
/// <param name="Target">What the window outlines while the card is up: a panel's id, <c>timeline</c> or <c>menu</c>.</param>
public sealed record TourStep(string Title, string Text, string Target);

/// <summary>
/// The tour: eight short cards on the editor's tools, read in about a minute, from Help or the
/// empty timeline. Text rather than video, so it is always as current as the editor.
/// </summary>
public sealed partial class TourViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step), nameof(Counter), nameof(IsLast), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private int _index;

    /// <summary>The cards, in order.</summary>
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("The window",
            "The preview is at the top, the timeline under it, your media on the left and the Inspector on the right. "
            + "Window, Workspace lays the panels out for a job: Edit, Colour, Audio, Trim or Export.", "menu"),
        new("Bring media in",
            "Press Ctrl+I, or drop files on the Media panel, then drag a clip onto a track. "
            + "A recording with game and microphone sound brings each onto a track of its own, linked to the picture.", "media"),
        new("Play and mark",
            "Space plays and stops; J, K and L shuttle backwards, stop and forwards. "
            + "I and O mark in and out. In Quick Trim, Enter keeps the marked stretch and Backspace cuts it.", "preview"),
        new("Cut and trim",
            "Ctrl+K splits the clips under the playhead and C is the razor. Drag a clip's edge to trim it: "
            + "B ripples, N rolls the cut, Y slips and U slides. The toolbar has every tool, its key in the tooltip.", "timeline"),
        new("Make it look right",
            "Select a clip and the Inspector shows everything about it: transform, opacity, crop and its effects. "
            + "Drag an effect from the Effects panel onto a clip. The stopwatch beside a value animates it.", "inspector"),
        new("Sound",
            "The line across a sound clip is its volume: drag it. The small squares at its top corners are its fades. "
            + "The Mixer has a strip for each track, and the master measures loudness as YouTube does.", "timeline"),
        new("Export",
            "File, Export plans the file before it writes it: where it can copy your recording's own frames it does, and says why. "
            + "Exports queue, and carry on with the window closed.", "menu"),
        new("Claude Code",
            "Everything here is also a command: jazz on the command line, or Claude Code over MCP driving the editor while you watch. "
            + "The Command Console shows each command as it happens. Help, Open the sample project has something to try it all on.", "console"),
    ];

    /// <summary>The card showing.</summary>
    public TourStep Step => Steps[Index];

    /// <summary>Where the card is in the tour: "3 of 8".</summary>
    public string Counter => $"{Index + 1} of {Steps.Count}";

    /// <summary>True on the last card.</summary>
    public bool IsLast => Index == Steps.Count - 1;

    /// <summary>What the forward button says: Next, or Done on the last card.</summary>
    public string NextLabel => IsLast ? "Done" : "Next";

    /// <summary>Raised when the tour is done.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>What the window should outline now: the card's target, or null when the tour is over.</summary>
    public event EventHandler<string?>? Highlighted;

    /// <summary>Outlines the card's target: when the tour opens, and on every card after.</summary>
    public void ShowTarget() => Highlighted?.Invoke(this, Step.Target);

    /// <summary>Takes the outline away, when the tour closes.</summary>
    public void ClearTarget() => Highlighted?.Invoke(this, null);

    partial void OnIndexChanged(int value) => ShowTarget();

    /// <summary>The next card, or the end of the tour.</summary>
    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Index++;
    }

    /// <summary>The card before.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Index--;

    private bool CanGoBack() => Index > 0;
}
