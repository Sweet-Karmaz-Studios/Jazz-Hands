namespace JazzHands.Core.Model;

/// <summary>
/// The identifiers one command makes, written down as it runs or handed out again when it is
/// replayed: what lets the history log rebuild an edit exactly after a crash (Phase 33).
/// </summary>
/// <remarks>
/// A command that adds a clip makes the clip's identifier itself, and the commands after it name
/// that identifier. Replayed as it was asked for, the add would make a different one and every
/// later command would miss. So while a scope is active on a thread, <see cref="Id.New"/> records
/// each identifier it makes, or, replaying, returns the recorded ones in order and fresh ones
/// only when they run out. Handlers are synchronous and run on the dispatcher's thread, so the
/// scope is per thread.
/// </remarks>
public sealed class IdScope : IDisposable
{
    [ThreadStatic]
    private static IdScope? _current;

    private readonly List<string> _issued = [];
    private readonly Queue<string>? _replay;
    private IdScope? _outer;
    private bool _active;

    private IdScope(IEnumerable<string>? replay)
    {
        _replay = replay is null ? null : new Queue<string>(replay);
    }

    /// <summary>The identifiers made while this scope was active, in order.</summary>
    public IReadOnlyList<string> Issued => _issued;

    /// <summary>True when it hands recorded identifiers out again, rather than writing new ones down.</summary>
    public bool IsReplaying => _replay is not null;

    /// <summary>
    /// Counts identifiers made outside the scope as made in it, at this point. A handler's slow
    /// part runs before its command's turn, on another thread, and what it made there is the
    /// command's all the same: written down where the handler takes it up, which is where a
    /// replay, doing that part in its turn, makes them.
    /// </summary>
    public static void Adopt(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_current is not { } scope)
        {
            return;
        }

        foreach (string id in ids)
        {
            // Replaying, the recorded one for this place is spent, so those after it still line up.
            if (scope._replay is { Count: > 0 } replay)
            {
                replay.Dequeue();
            }

            scope._issued.Add(id);
        }
    }

    /// <summary>A scope that writes down every identifier made while it is active.</summary>
    public static IdScope Recording() => new(null);

    /// <summary>A scope that hands out <paramref name="ids"/> in order, then fresh ones.</summary>
    public static IdScope Replaying(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return new IdScope(ids);
    }

    /// <summary>Makes this the scope <see cref="Id.New"/> uses on this thread until it is disposed.</summary>
    public IdScope Enter()
    {
        if (!_active)
        {
            _outer = _current;
            _current = this;
            _active = true;
        }

        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_active && ReferenceEquals(_current, this))
        {
            _current = _outer;
        }

        _active = false;
    }

    /// <summary>The next identifier from the active scope, or null when none is active or it has run out.</summary>
    internal static string? Next()
    {
        if (_current is not { } scope)
        {
            return null;
        }

        string? id = scope._replay is { Count: > 0 } replay ? replay.Dequeue() : null;
        id ??= Ulid.NewUlid().ToString();
        scope._issued.Add(id);
        return id;
    }
}
