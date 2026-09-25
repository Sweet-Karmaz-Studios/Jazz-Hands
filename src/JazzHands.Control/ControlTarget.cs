using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using JazzHands.Engine.Playback;
using JazzHands.Engine.Selection;

namespace JazzHands.Control;

/// <summary>What a control server drives: one session, and the things around it that clients hear about.</summary>
/// <remarks>
/// The editor gives it everything: its session, its selection, the playhead and the export queue.
/// <c>jazz serve</c> has a session and a foreground export queue and nothing to play. A source
/// that is not there raises no events; a client that subscribes to it hears nothing.
/// </remarks>
public sealed class ControlTarget
{
    /// <summary>The session commands run in.</summary>
    public required Session Session { get; init; }

    /// <summary>What is selected, for <c>selection.changed</c>.</summary>
    public SelectionService? Selection { get; init; }

    /// <summary>The playhead, for <c>playhead.moved</c>.</summary>
    public PlaybackEngine? Playback { get; init; }

    /// <summary>The export queue, for <c>export.progress</c> and <c>export.done</c>.</summary>
    public IExportService? Exports { get; init; }

    /// <summary><c>gui</c> for the editor, <c>serve</c> for <c>jazz serve</c>.</summary>
    public string Kind { get; init; } = "serve";

    /// <summary>
    /// Methods the host answers itself, by name: the editor's <c>app.open</c> and <c>app.activate</c>,
    /// which a second launch uses to hand its project to the one already running. Each gets the
    /// params and returns the result, or throws <see cref="JsonRpcException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, Func<System.Text.Json.Nodes.JsonObject, Task<System.Text.Json.Nodes.JsonNode?>>> HostMethods { get; init; } =
        new Dictionary<string, Func<System.Text.Json.Nodes.JsonObject, Task<System.Text.Json.Nodes.JsonNode?>>>(StringComparer.Ordinal);
}

/// <summary>
/// Commands a client may send, per second: a burst, then a steady rate.
/// </summary>
/// <remarks>
/// A token bucket. The skill's 200 a second is the steady rate, and it protects the editor from a
/// runaway script; the burst of a thousand lets a script send a thousand edits at once, which is
/// the phase's speed criterion. A batch is one command. Queries are not counted.
/// </remarks>
public sealed class RateLimiter
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly double _perSecond;
    private readonly double _burst;
    private double _tokens;
    private long _last;

    /// <summary>A limiter that allows a burst, then a steady rate.</summary>
    public RateLimiter(double perSecond = 200, double burst = 1000, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _clock = clock ?? TimeProvider.System;
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
        _last = _clock.GetTimestamp();
    }

    /// <summary>Takes one token, or says how long until there is one.</summary>
    public bool TryTake(out TimeSpan wait)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + (_clock.GetElapsedTime(_last, now).TotalSeconds * _perSecond));
            _last = now;

            if (_tokens >= 1)
            {
                _tokens -= 1;
                wait = TimeSpan.Zero;
                return true;
            }

            wait = TimeSpan.FromSeconds((1 - _tokens) / _perSecond);
            return false;
        }
    }
}
