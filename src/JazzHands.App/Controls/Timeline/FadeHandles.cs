using System.Windows;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>
/// Where a sound clip's fade handles sit on its body, and what a drag of one makes the fade.
/// </summary>
/// <remarks>
/// A handle sits at the top of the body where its fade ends: the fade in's at the clip's start
/// plus its length, the fade out's at the clip's end less its length. With no fade, or one too
/// short to reach past it, a handle rests a little inside the edge, clear of the trim zone.
/// The control draws from here and the view model hit tests from here, so they cannot drift
/// apart. A drag moves the handle by the pointer's travel, on frames, and a fade never runs
/// into the other one.
/// </remarks>
public static class FadeHandles
{
    /// <summary>How far inside the clip's edge a handle rests with no fade, and how far below the top.</summary>
    public const double Inset = 10.0;

    /// <summary>The handle's side, in pixels.</summary>
    public const double Size = 7.0;

    /// <summary>How close to a handle, in pixels, a press grabs it.</summary>
    public const double Zone = 6.0;

    /// <summary>True when a clip has fade handles: a sound clip tall and wide enough for them.</summary>
    public static bool Shown(ClipView clip, Rect body) =>
        VolumeLine.Shown(clip, body) && body.Width >= Inset * 4;

    /// <summary>How long a clip's fade in is.</summary>
    public static Flicks In(ClipView clip) => clip.Clip.FadeIn?.Duration ?? Flicks.Zero;

    /// <summary>How long a clip's fade out is.</summary>
    public static Flicks Out(ClipView clip) => clip.Clip.FadeOut?.Duration ?? Flicks.Zero;

    /// <summary>Where a fade's handle is drawn.</summary>
    public static Point Handle(TimelineGeometry geometry, ClipView clip, Rect body, bool fadeIn)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(clip);

        double x = fadeIn
            ? Math.Max(geometry.XOf(clip.Start + In(clip)), body.Left + Inset)
            : Math.Min(geometry.XOf(clip.End - Out(clip)), body.Right - Inset);
        return new Point(x, body.Top + (Inset / 2));
    }

    /// <summary>True when a point is on a fade's handle.</summary>
    public static bool On(Point handle, Point point) =>
        Math.Abs(point.X - handle.X) <= Zone && Math.Abs(point.Y - handle.Y) <= Zone;

    /// <summary>
    /// The fade's length after its handle has moved <paramref name="travel"/> pixels: on a frame,
    /// from none to what the other fade leaves of the clip.
    /// </summary>
    public static Flicks Length(TimelineGeometry geometry, ClipView clip, bool fadeIn, double travel)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(clip);

        Flicks now = fadeIn ? In(clip) : Out(clip);
        Flicks other = fadeIn ? Out(clip) : In(clip);
        Flicks end = fadeIn ? clip.Start + now : clip.End - now;
        Flicks moved = geometry.TimeAt(geometry.XOf(end) + travel);
        Flicks length = fadeIn ? moved - clip.Start : clip.End - moved;
        return Flicks.Max(Flicks.Zero, Flicks.Min((clip.End - clip.Start) - other, length));
    }
}
