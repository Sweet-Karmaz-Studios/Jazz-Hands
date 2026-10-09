using System.Windows;

namespace JazzHands.App.Views.Effects;

/// <summary>
/// When a press in a list becomes a drag, and of what: the item pressed, not whichever item the
/// pointer is over by the time it has moved far enough. A quick move down a list used to start
/// dragging the row below (2026-10-09: "3D camera" dragged as "3D text").
/// </summary>
internal sealed class DragStart
{
    private Point? _pressed;
    private object? _item;

    /// <summary>A press on an item, at a point.</summary>
    public void Press(Point at, object? item)
    {
        _pressed = at;
        _item = item;
    }

    /// <summary>
    /// The item pressed, once the pointer has moved past the system's drag distance with the
    /// button still down; null before then, and after, until the next press.
    /// </summary>
    public object? Take(Point now, bool buttonDown, Size threshold)
    {
        if (!buttonDown || _pressed is not { } pressed)
        {
            return null;
        }

        Vector moved = now - pressed;
        if (Math.Abs(moved.X) < threshold.Width && Math.Abs(moved.Y) < threshold.Height)
        {
            return null;
        }

        object? item = _item;
        _pressed = null;
        _item = null;
        return item;
    }
}
