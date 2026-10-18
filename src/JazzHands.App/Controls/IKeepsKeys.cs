using System.Windows.Input;

namespace JazzHands.App.Controls;

/// <summary>
/// A control that answers some keys itself while it has the keyboard, which the editor's keymap
/// leaves to it, as it leaves typing to a text box. The window looks at keys first; without this
/// Delete in the Curves editor removed the selected clip rather than resetting the curve, and
/// Backspace on a colour wheel was the timeline's (seen in QA, 2026-10-18).
/// </summary>
internal interface IKeepsKeys
{
    /// <summary>True for a key the control acts on, with the modifiers held now.</summary>
    bool Keeps(Key key);
}
