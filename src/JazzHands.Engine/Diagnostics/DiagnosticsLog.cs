using System.Collections.Immutable;
using JazzHands.Core.Diagnostics;
using Serilog;

namespace JazzHands.Engine.Diagnostics;

/// <summary>
/// What a session has noticed that a person should know: fallbacks, missing files, formats that
/// will not play as well as they should.
/// </summary>
/// <remarks>
/// Things go wrong quietly in a media pipeline. A file that falls back to software decode still
/// plays, and a clip whose media moved still draws black, and neither raises anything the way an
/// exception would. Collecting them means the UI can badge a clip and <c>jazz</c> can print a
/// list, from the same place, rather than each surface inventing its own.
///
/// Deduplicated by media and code: a fallback that happens on every frame is one entry with a
/// count, not sixty a second.
///
/// Locked rather than thread affine, because these arrive from whichever thread noticed and are
/// read from whichever thread is drawing.
/// </remarks>
public sealed class DiagnosticsLog
{
    private readonly ILogger _log = Log.ForContext<DiagnosticsLog>();
    private readonly Dictionary<(string MediaId, string Code), Diagnostic> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Raised the first time a diagnostic with a given media and code is reported.</summary>
    public event EventHandler<Diagnostic>? Raised;

    /// <summary>How many distinct things have been noticed.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Everything noticed, oldest first.</summary>
    public ImmutableArray<Diagnostic> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values.OrderBy(entry => entry.At)];
            }
        }
    }

    /// <summary>
    /// Records something, or counts it again if it has been seen before.
    /// </summary>
    /// <returns>True the first time a given media and code is seen, which is when it is raised.</returns>
    public bool Report(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        lock (_gate)
        {
            (string MediaId, string Code) key = (diagnostic.MediaId, diagnostic.Code);

            if (_entries.TryGetValue(key, out Diagnostic? existing))
            {
                _entries[key] = existing with { Occurrences = existing.Occurrences + 1 };
                return false;
            }

            _entries[key] = diagnostic;
        }

        _log.Information(
            "{Level} about {Media}: {Code}: {Message}",
            diagnostic.Level,
            diagnostic.Name.Length > 0 ? diagnostic.Name : diagnostic.MediaId,
            diagnostic.Code,
            diagnostic.Message);

        Raised?.Invoke(this, diagnostic);
        return true;
    }

    /// <summary>Records something, building the diagnostic from its parts.</summary>
    public bool Report(
        string mediaId,
        string name,
        string code,
        string message,
        DiagnosticLevel level = DiagnosticLevel.Warning,
        TimeProvider? clock = null) =>
        Report(new Diagnostic(
            mediaId,
            name,
            code,
            message,
            level,
            (clock ?? TimeProvider.System).GetUtcNow()));

    /// <summary>How many times something has happened, which a once-per-frame fallback needs.</summary>
    public long Occurrences(string mediaId, string code)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((mediaId, code), out Diagnostic? entry) ? entry.Occurrences : 0;
        }
    }

    /// <summary>Everything noticed about one media item.</summary>
    public ImmutableArray<Diagnostic> For(string mediaId)
    {
        ArgumentNullException.ThrowIfNull(mediaId);

        lock (_gate)
        {
            return
            [
                .. _entries.Values
                    .Where(entry => string.Equals(entry.MediaId, mediaId, StringComparison.Ordinal))
                    .OrderBy(entry => entry.At),
            ];
        }
    }

    /// <summary>Forgets everything about one media item, which a reprobe or a relink needs.</summary>
    public void Forget(string mediaId)
    {
        ArgumentNullException.ThrowIfNull(mediaId);

        lock (_gate)
        {
            foreach ((string MediaId, string Code) key in _entries.Keys
                .Where(key => string.Equals(key.MediaId, mediaId, StringComparison.Ordinal))
                .ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
