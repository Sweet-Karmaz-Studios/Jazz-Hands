using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// Sends parameter values from a control being dragged: latest wins, one command in flight per
/// parameter, and a keyframe at the playhead when the parameter is animated.
/// </summary>
/// <remarks>
/// The inspector's rule, for panels that edit parameters their own way (the colour wheels, the
/// curve editor). While a value is on its way only the newest waits behind it, so a drag sends as
/// fast as the engine takes commands and never builds a backlog; consecutive <c>param.set</c>s
/// merge into one undo step in the engine.
/// </remarks>
public sealed class ParamSender(ISession session, IUiDispatcher ui, Func<Flicks> playhead)
{
    private readonly Dictionary<(string Owner, string Name), string> _pending = [];
    private readonly HashSet<(string Owner, string Name)> _sending = [];

    /// <summary>Raised on the UI thread with a refusal's message.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>Sends a value, as the text the commands read.</summary>
    public void Send(string ownerId, string name, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);

        (string, string) key = (ownerId, name);
        _pending[key] = text;
        if (_sending.Add(key))
        {
            _ = PumpAsync(key);
        }
    }

    private async Task PumpAsync((string Owner, string Name) key)
    {
        try
        {
            while (_pending.Remove(key, out string? text))
            {
                Flicks? at = ParamTargets.Find(session.Project, key.Owner) is { } owner
                    && ParamTargets.Get(owner, key.Name) is KeyframedValue { IsAnimated: true }
                    ? playhead()
                    : null;

                try
                {
                    CommandResult result = await session.ExecuteAsync(new SetParamCommand(key.Owner, key.Name, text, at)).ConfigureAwait(true);
                    if (!result.Ok)
                    {
                        string message = result.Error ?? result.Code ?? "That did not work.";
                        ui.Post(() => Refused?.Invoke(this, message));
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.ForContext<ParamSender>().Error(error, "Setting {Param} on {Owner} failed", key.Name, key.Owner);
                    ui.Post(() => Refused?.Invoke(this, error.Message));
                }
            }
        }
        finally
        {
            _sending.Remove(key);
        }
    }
}
