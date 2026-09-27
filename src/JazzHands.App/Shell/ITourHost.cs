using System.Windows;

namespace JazzHands.App.Shell;

/// <summary>What the tour asks of the main window: outline the part a card is about.</summary>
public interface ITourHost
{
    /// <summary>
    /// Outlines a panel (by its id), <c>timeline</c> or <c>menu</c>, bringing a panel forward
    /// first; null takes the outline away.
    /// </summary>
    /// <returns>The outlined area and the window's own, in screen DIPs, so the tour can stand clear of it; null when nothing is outlined.</returns>
    (Rect Target, Rect Window)? Highlight(string? target);
}
