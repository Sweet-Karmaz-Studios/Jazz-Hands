using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;

namespace JazzHands.App.ViewModels.History;

/// <summary>One step of the undo history, as the History panel lists it.</summary>
/// <param name="Index">Its place, from the oldest; -1 for the project as it was opened.</param>
/// <param name="Label">What it did.</param>
/// <param name="Command">The command name.</param>
/// <param name="Origin">Who did it: gui, console, rpc:mcp and so on.</param>
/// <param name="At">When.</param>
/// <param name="IsUndone">True when it has been taken back and can be redone.</param>
/// <param name="IsCurrent">True for the step the project is at now.</param>
public sealed record HistoryRow(int Index, string Label, string Command, string Origin, DateTimeOffset At, bool IsUndone, bool IsCurrent)
{
    /// <summary>The time, as the list shows it.</summary>
    public string Time => Index < 0 ? string.Empty : At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The origin, as the list shows it: "by hand" for the person at the window.</summary>
    public string Who => Origin switch
    {
        "gui" => "by hand",
        "" or "local" => string.Empty,
        _ => Origin,
    };
}

/// <summary>
/// The History panel: every undoable step, oldest first, with who made it, and a click to go
/// back to any point (or forward again through what was undone).
/// </summary>
/// <remarks>
/// Going to a point is <c>undo</c> or <c>redo</c> with a number of steps, the commands the CLI
/// and Claude Code send, so it is one entry in the log like any other. The list is read again
/// after every command, from any origin, gathered into one refresh per burst.
/// </remarks>
public sealed partial class HistoryPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "history";

    private readonly ISession _session;
    private readonly IUiDispatcher _ui;
    private int _refreshQueued;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the panel.</summary>
    public HistoryPanelViewModel(ISession session, IUiDispatcher ui)
        : base(PanelId, "History")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);
        _session = session;
        _ui = ui;
        _session.ProjectChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            {
                _ui.Post(() =>
                {
                    Volatile.Write(ref _refreshQueued, 0);
                    Refresh();
                });
            }
        };
        Refresh();
    }

    /// <summary>The steps, oldest first, after a first row for the project as opened.</summary>
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    /// <summary>Reads the history again.</summary>
    public void Refresh()
    {
        HistoryInfo[] history = _session.Query(new ListHistoryQuery(Limit: 1000));
        int current = history.Where(entry => !entry.IsUndone).Select(entry => entry.Index).DefaultIfEmpty(-1).Max();
        Rows.Clear();
        Rows.Add(new HistoryRow(-1, "Opened", string.Empty, string.Empty, default, false, current < 0));
        foreach (HistoryInfo entry in history)
        {
            Rows.Add(new HistoryRow(entry.Index, entry.Label.Length > 0 ? entry.Label : entry.Command, entry.Command, entry.Origin, entry.At, entry.IsUndone, entry.Index == current));
        }

        int undone = history.Count(entry => entry.IsUndone);
        Status = string.Create(CultureInfo.InvariantCulture, $"{Services.Words.Count(history.Length - undone, "step")}{(undone > 0 ? $", {undone} undone" : string.Empty)}");
    }

    /// <summary>Takes the project to just after a step: undoes what came after, or redoes up to it.</summary>
    [RelayCommand]
    public async Task GoToAsync(HistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        HistoryRow? now = Rows.FirstOrDefault(candidate => candidate.IsCurrent);
        int current = now?.Index ?? -1;
        int steps = row.Index - current;
        if (steps == 0)
        {
            return;
        }

        // Rows count every step, done and undone, in order; the distance between two is the
        // number of undos or redos between them.
        int distance = Rows.Count(candidate => candidate.Index > Math.Min(row.Index, current) && candidate.Index <= Math.Max(row.Index, current));
        CommandResult result = await _session.ExecuteAsync(steps < 0 ? new UndoCommand(distance) : new RedoCommand(distance)).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "That step cannot be reached.";
        }
    }
}
