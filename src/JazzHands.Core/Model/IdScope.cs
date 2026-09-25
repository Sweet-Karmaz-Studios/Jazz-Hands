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
