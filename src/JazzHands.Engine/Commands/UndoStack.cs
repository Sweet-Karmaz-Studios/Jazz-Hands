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
/// <param name="Issuer">Who asked for it, or empty when nobody said.</param>
public sealed record UndoEntry(
    ICommand Command,
    Project Before,
    Project After,
    ImmutableArray<string> ChangedIds,
    DateTimeOffset At,
    string Issuer = "");

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
///
/// The dispatcher's queue is the only writer, but the history is read from anywhere: the undo
/// menu, <c>history.list</c> over the control server, the Command Console. A lock keeps a reader
/// from seeing the list half way through a push.
/// </remarks>
public sealed class UndoStack
{
    private readonly Lock _gate = new();
    private readonly List<UndoEntry> _entries = [];
    private readonly int _limit;
    private int _undoCount;

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

    /// <summary>How soon after a step a command that continues it has to arrive to join it.</summary>
    public static TimeSpan MergeWindow { get; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How many commands could be taken back.</summary>
    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undoCount;
            }
        }
    }

    /// <summary>How many commands could be put back.</summary>
    public int RedoCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count - _undoCount;
            }
        }
    }

    /// <summary>True when there is something to take back.</summary>
    public bool CanUndo => UndoCount > 0;

    /// <summary>True when there is something to put back.</summary>
    public bool CanRedo => RedoCount > 0;

    /// <summary>Everything remembered, oldest first, including what has been undone.</summary>
    public ImmutableArray<UndoEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The command that undo would take back, or null.</summary>
    public UndoEntry? NextUndo
    {
        get
        {
            lock (_gate)
            {
                return _undoCount > 0 ? _entries[_undoCount - 1] : null;
            }
        }
    }

    /// <summary>The command that redo would put back, or null.</summary>
    public UndoEntry? NextRedo
    {
        get
        {
            lock (_gate)
            {
                return _undoCount < _entries.Count ? _entries[_undoCount] : null;
            }
        }
    }

    /// <summary>Records a command that has just run.</summary>
    public void Push(UndoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            // A slider drag is many commands and one edit: a command that continues the last one,
            // straight after it, becomes part of its step. Its Before stays the first command's.
            if (_undoCount == _entries.Count
                && _undoCount > 0
                && _entries[_undoCount - 1] is { } top
                && entry.Command is IMergeableCommand merging
                && merging.Continues(top.Command)
                && ReferenceEquals(top.After, entry.Before)
                && entry.At - top.At <= MergeWindow)
            {
                _entries[_undoCount - 1] = entry with { Before = top.Before, ChangedIds = [.. top.ChangedIds.Union(entry.ChangedIds, StringComparer.Ordinal)] };
                return;
            }

            // Doing something new after undoing abandons the branch that was undone.
            if (_undoCount < _entries.Count)
            {
                _entries.RemoveRange(_undoCount, _entries.Count - _undoCount);
            }

            _entries.Add(entry);
            _undoCount = _entries.Count;

            if (_entries.Count > _limit)
            {
                int excess = _entries.Count - _limit;
                _entries.RemoveRange(0, excess);
                _undoCount -= excess;
            }
        }
    }

    /// <summary>Takes back the last command and returns the project as it was.</summary>
    public UndoEntry Undo()
    {
        lock (_gate)
        {
            return _undoCount > 0
                ? _entries[--_undoCount]
                : throw new CommandException("nothing-to-undo", "There is nothing to undo.");
        }
    }

    /// <summary>Puts back the last undone command and returns the project it produced.</summary>
    public UndoEntry Redo()
    {
        lock (_gate)
        {
            return _undoCount < _entries.Count
                ? _entries[_undoCount++]
                : throw new CommandException("nothing-to-redo", "There is nothing to redo.");
        }
    }

    /// <summary>Forgets everything, which is what opening another project does.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _undoCount = 0;
        }
    }

    /// <summary>The history as a query returns it, oldest first.</summary>
    public ImmutableArray<HistoryInfo> History(int limit)
    {
        UndoEntry[] entries;
        int undoCount;
        lock (_gate)
        {
            entries = [.. _entries];
            undoCount = _undoCount;
        }

        int take = Math.Clamp(limit, 0, entries.Length);
        int from = entries.Length - take;

        var history = ImmutableArray.CreateBuilder<HistoryInfo>(take);

        for (int index = from; index < entries.Length; index++)
        {
            UndoEntry entry = entries[index];
            CommandMetadata metadata = CommandRegistry.Describe(entry.Command);

            history.Add(new HistoryInfo(
                index,
                metadata.Name,
                CommandRegistry.ArgsToJson(entry.Command, metadata),
                metadata.Description.Length > 0 ? metadata.Description : metadata.Name,
                index >= undoCount,
                entry.At,
                entry.Issuer));
        }

        return history.ToImmutable();
    }
}
