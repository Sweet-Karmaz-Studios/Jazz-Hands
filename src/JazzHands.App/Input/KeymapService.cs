using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Engine.Selection;
using Serilog;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.Input;

/// <summary>
/// Turns key presses into commands through the <see cref="Keymap"/>.
/// </summary>
/// <remarks>
/// The window offers every key here first, after text boxes have had theirs; what is not bound
/// goes on to the preview panel's transport keys. A key that resolves to nothing (Delete with
/// nothing selected) is still taken, and says why through <see cref="Message"/>.
/// </remarks>
public sealed class KeymapService
{
    private readonly ILogger _log = Log.ForContext<KeymapService>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly Func<Flicks> _playhead;

    /// <summary>Creates the service.</summary>
    /// <param name="session">Where commands go.</param>
    /// <param name="selection">What <c>$selection</c> means.</param>
    /// <param name="playhead">What <c>$playhead</c> means.</param>
    /// <param name="keymap">The bindings.</param>
    public KeymapService(ISession session, SelectionService selection, Func<Flicks> playhead, Keymap keymap)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(playhead);
        ArgumentNullException.ThrowIfNull(keymap);

        _session = session;
        _selection = selection;
        _playhead = playhead;
        Keymap = keymap;
    }

    /// <summary>Raised with a sentence when a key did nothing or its command was refused.</summary>
    public event EventHandler<string>? Message;

    /// <summary>The bindings in use.</summary>
    public Keymap Keymap { get; }

    /// <summary>Handles a key if it is bound.</summary>
    /// <returns>True when the key was bound, and so was used up.</returns>
    public bool TryHandle(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (Keymap.Find(key, modifiers) is not { } binding)
        {
            return false;
        }

        if (isRepeat && !binding.Repeat)
        {
            return true;
        }

        ICommand? command;
        try
        {
            command = Keymap.Resolve(binding, new KeymapContext(_session.Project, _playhead(), _selection.Ids), out string? nothing);
            if (command is null)
            {
                Message?.Invoke(this, nothing ?? "Nothing to do.");
                return true;
            }
        }
        catch (Exception exception) when (exception is CommandException or FormatException)
        {
            _log.Warning(exception, "The binding for {Keys} could not be used", binding.Keys);
            Message?.Invoke(this, $"{binding.Keys}: {exception.Message}");
            return true;
        }

        _ = RunAsync(command);
        return true;
    }

    private async Task RunAsync(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Message?.Invoke(this, result.Error ?? result.Code ?? "That did not work.");
        }
    }
}
