using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.Engine.Commands;

/// <summary>One thing that was done, and the project on either side of it.</summary>
/// <param name="Command">What was run.</param>
/// <param name="Before">The project before it ran.</param>
/// <param name="After">The project after it ran.</param>
/// <param name="ChangedIds">What it touched.</param>
/// <param name="At">When it ran.</param>
public sealed record UndoEntry(
    ICommand Command,
    Project Before,
    Project After,
    ImmutableArray<string> ChangedIds,
    DateTimeOffset At);

/// <summary>
/// Linear undo, by keeping the project on both sides of every command.
/// </summary>
/// <remarks>
/// Handlers never write inverse logic. Undo restores the snapshot taken before the command, and
/// that is the whole mechanism. It works because the project is immutable with structural
/// sharing: a command on a five hundred clip project replaces one clip, one track, one sequence
/// and the root, and the two snapshots share everything else. Holding a hundred of them costs a
/// hundred root objects, not a hundred projects.
///
/// Doing something after undoing throws away what was undone, as every editor does. The
/// alternative, a tree of histories, is a feature nobody has ever been able to explain in a UI.
/// </remarks>
public sealed class UndoStack
{
    private readonly List<UndoEntry> _entries = [];
    private readonly int _limit;

    /// <summary>Creates a stack.</summary>
    /// <param name="limit">
    /// How many commands to remember. The oldest are forgotten past this. A thousand is far more
    /// than anyone undoes through, and bounds what a long session holds.
    /// </param>
    public UndoStack(int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
    }

    /// <summary>How many commands could be taken back.</summary>
    public int UndoCount { get; private set; }

    /// <summary>How many commands could be put back.</summary>
    public int RedoCount => _entries.Count - UndoCount;

    /// <summary>True when there is something to take back.</summary>
    public bool CanUndo => UndoCount > 0;

    /// <summary>True when there is something to put back.</summary>
    public bool CanRedo => RedoCount > 0;

    /// <summary>Everything remembered, oldest first, including what has been undone.</summary>
    public ImmutableArray<UndoEntry> Entries => [.. _entries];

    /// <summary>The command that undo would take back, or null.</summary>
    public UndoEntry? NextUndo => CanUndo ? _entries[UndoCount - 1] : null;

    /// <summary>The command that redo would put back, or null.</summary>
    public UndoEntry? NextRedo => CanRedo ? _entries[UndoCount] : null;

    /// <summary>Records a command that has just run.</summary>
    public void Push(UndoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Doing something new after undoing abandons the branch that was undone.
        if (RedoCount > 0)
        {
            _entries.RemoveRange(UndoCount, RedoCount);
        }

        _entries.Add(entry);
        UndoCount = _entries.Count;

        if (_entries.Count > _limit)
        {
            int excess = _entries.Count - _limit;
            _entries.RemoveRange(0, excess);
            UndoCount -= excess;
        }
    }

    /// <summary>Takes back the last command and returns the project as it was.</summary>
    public UndoEntry Undo() => CanUndo
        ? _entries[--UndoCount]
        : throw new CommandException("nothing-to-undo", "There is nothing to undo.");

    /// <summary>Puts back the last undone command and returns the project it produced.</summary>
    public UndoEntry Redo() => CanRedo
        ? _entries[UndoCount++]
        : throw new CommandException("nothing-to-redo", "There is nothing to redo.");

    /// <summary>Forgets everything, which is what opening another project does.</summary>
    public void Clear()
    {
        _entries.Clear();
        UndoCount = 0;
    }

    /// <summary>The history as a query returns it, oldest first.</summary>
    public ImmutableArray<HistoryInfo> History(int limit)
    {
        int take = Math.Clamp(limit, 0, _entries.Count);
        int from = _entries.Count - take;

        var history = ImmutableArray.CreateBuilder<HistoryInfo>(take);

        for (int index = from; index < _entries.Count; index++)
        {
            UndoEntry entry = _entries[index];
            CommandMetadata metadata = CommandRegistry.Describe(entry.Command);

            history.Add(new HistoryInfo(
                index,
                metadata.Name,
                CommandRegistry.ArgsToJson(entry.Command, metadata),
                metadata.Description.Length > 0 ? metadata.Description : metadata.Name,
                index >= UndoCount,
                entry.At));
        }

        return history.ToImmutable();
    }
}
