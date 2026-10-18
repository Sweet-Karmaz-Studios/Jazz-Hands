using System.Windows;
using System.Windows.Input;

namespace JazzHands.App.Controls;

/// <summary>
/// A field done with, on Enter or Escape, hands the keyboard to its window, so the editor's keys
/// (Space, Ctrl+Z and the rest) work again at once. Not to nothing: with nothing focused no key
/// reaches the window, and Space or Ctrl+Z after Enter in a number or a track name did nothing
/// (seen on screen, 2026-10-18).
/// </summary>
internal static class LetGo
{
    /// <summary>Moves the keyboard from inside <paramref name="field"/> to its window, if it is there.</summary>
    public static void Keyboard(DependencyObject field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field is not UIElement { IsKeyboardFocusWithin: true })
        {
            return;
        }

        // The window is a focus scope, and focused it hands the keyboard back to the field it remembers.
        Window? window = Window.GetWindow(field);
        if (window is not null)
        {
            FocusManager.SetFocusedElement(window, null);
        }

        if (window is null || !ReferenceEquals(System.Windows.Input.Keyboard.Focus(window), window))
        {
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }
}
