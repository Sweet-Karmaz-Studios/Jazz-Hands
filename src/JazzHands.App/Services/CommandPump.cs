using JazzHands.Core.Commands;
using JazzHands.Engine.Commands;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// Sends commands from a control being dragged: one in flight per control, and only the newest
/// value waits behind it.
/// </summary>
/// <remarks>
/// A fader or a rubber band moves faster than the engine takes commands. Sending every step would
/// queue a backlog that plays out long after the mouse stopped; sending only the newest keeps the
/// sound on the pointer. The command is made when it is sent rather than when it is queued, so it
/// is built from the project as it is then. Consecutive steps of one drag are mergeable commands,
/// which the engine folds into one undo step. Used on the UI thread.
/// </remarks>
/// <param name="session">Where commands go.</param>
/// <param name="ui">How to report back on the UI thread.</param>
public sealed class CommandPump(ISession session, IUiDispatcher ui)
{
    private readonly Dictionary<string, Func<ICommand>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sending = new(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread with a refusal's message.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>Raised on the UI thread when a control's last value has been sent.</summary>
    public event EventHandler<string>? Settled;

    /// <summary>Sends a command for a control, or replaces the one waiting for it.</summary>
    /// <param name="key">The control: one command in flight per key.</param>
    /// <param name="make">Makes the command when it is its turn.</param>
    public void Send(string key, Func<ICommand> make)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(make);

        _pending[key] = make;
        if (_sending.Add(key))
        {
            _ = PumpAsync(key);
        }
    }

    /// <summary>True while a control's value is on its way, when the model is behind the control.</summary>
    public bool IsSending(string key) => _sending.Contains(key);

    private async Task PumpAsync(string key)
    {
        try
        {
            while (_pending.Remove(key, out Func<ICommand>? make))
            {
                ICommand command = make();
                try
                {
                    CommandResult result = await session.ExecuteAsync(command).ConfigureAwait(true);
                    if (!result.Ok)
                    {
                        string message = result.Error ?? result.Code ?? "That did not work.";
                        ui.Post(() => Refused?.Invoke(this, message));
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Log.ForContext<CommandPump>().Error(error, "Sending {Command} failed", CommandRegistry.NameOf(command));
                    ui.Post(() => Refused?.Invoke(this, error.Message));
                }
            }
        }
        finally
        {
            _sending.Remove(key);
            ui.Post(() => Settled?.Invoke(this, key));
        }
    }
}
