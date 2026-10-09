using System.Windows.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Engine.Selection;
using Serilog;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.Input;

/// <summary>
/// Turns key presses, and menu items that stand for them, into commands through the <see cref="Keymap"/>.
/// </summary>
/// <remarks>
/// The window offers every key here first, after text boxes have had theirs; what is not bound
/// goes on to the preview panel's transport keys. A key that resolves to nothing (Delete with
/// nothing selected) is still taken, and says why through <see cref="Message"/>. A menu item for
/// a binding calls <see cref="Invoke"/>, so the menu and the key do exactly the same thing.
/// </remarks>
public sealed class KeymapService
{
    private readonly ILogger _log = Log.ForContext<KeymapService>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly Func<Flicks> _playhead;
    private Keymap _keymap;

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
        _keymap = keymap;
    }

    /// <summary>Raised with a sentence when a key did nothing or its command was refused.</summary>
    public event EventHandler<string>? Message;

    /// <summary>Raised when the bindings change, so menus can show the new keys.</summary>
    public event EventHandler? Changed;

    /// <summary>The bindings in use.</summary>
    public Keymap Keymap => _keymap;

    /// <summary>
    /// Where the timeline's <c>ui.</c> actions go, the timeline in front; true when it did something with one.
    /// </summary>
    public Func<string, bool>? Actions { get; set; }

    /// <summary>Where the window's <c>ui.</c> actions go (new, open, save, settings); true when handled.</summary>
    public Func<string, bool>? WindowActions { get; set; }

    /// <summary>Puts new bindings in use, as the keymap editor saves them.</summary>
    public void Replace(Keymap keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        _keymap = keymap;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The keys bound to a command with these arguments, as written, for a menu to show.</summary>
    public IReadOnlyList<string> KeysFor(string command, System.Text.Json.Nodes.JsonObject? args = null) =>
        [
            .. _keymap.Bindings
                .Where(binding => string.Equals(binding.Command, command, StringComparison.Ordinal)
                    && (args is null || System.Text.Json.Nodes.JsonNode.DeepEquals(binding.Args, args)))
                .Select(binding => binding.Keys)
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>Handles a key if it is bound.</summary>
    /// <returns>True when the key was bound, and so was used up.</returns>
    public bool TryHandle(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (_keymap.Find(key, modifiers) is not { } binding)
        {
            return false;
        }

        if (isRepeat && !binding.Repeat)
        {
            return true;
        }

        Invoke(binding);
        return true;
    }

    /// <summary>Does what a binding does: a key press, or the menu item standing for one.</summary>
    public void Invoke(KeymapBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (UiActions.IsAction(binding.Command))
        {
            bool handled = UiActions.Window.ContainsKey(binding.Command)
                ? WindowActions?.Invoke(binding.Command) == true
                : Actions?.Invoke(binding.Command) == true;
            if (!handled)
            {
                Message?.Invoke(this, UiActions.Window.ContainsKey(binding.Command) ? "That is not available here." : "Open a timeline for that.");
            }

            return;
        }

        ICommand? command;
        try
        {
            command = Keymap.Resolve(binding, new KeymapContext(_session.Project, _playhead(), _selection.Ids), out string? nothing);
            if (command is null)
            {
                Message?.Invoke(this, nothing ?? "Nothing to do.");
                return;
            }
        }
        catch (Exception exception) when (exception is CommandException or FormatException)
        {
            _log.Warning(exception, "The binding for {Keys} could not be used", binding.Keys);
            Message?.Invoke(this, $"{binding.Keys}: {exception.Message}");
            return;
        }

        _ = RunAsync(command, Keymap.PlayheadAfter(command, _session.Project));
    }

    private async Task RunAsync(ICommand command, Flicks? then)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Message?.Invoke(this, result.Error ?? result.Code ?? "That did not work.");
            return;
        }

        if (then is { } at)
        {
            await _session.ExecuteAsync(new SeekCommand(at)).ConfigureAwait(true);
        }
    }
}
