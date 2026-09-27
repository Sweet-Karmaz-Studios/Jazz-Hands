using System.Windows;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>
/// Where a picture clip's speed curve, its speed lane, sits on its body (Phase 45).
/// </summary>
/// <remarks>
/// Drawn on a clip whose speed is a curve (time remapping), which is the lane's switch: the clip
/// menu's Speed curve turns remapping on and off. The scale is doublings: 800% at the top of the
/// body, 12.5% at the bottom, normal speed across the middle, so a ramp from 100% to 400% climbs
/// as far as one from 25% back to 100%. The control draws it and the view model hit tests it, both
/// from here.
/// </remarks>
public static class SpeedLine
{
    /// <summary>The speed at the top of the body.</summary>
    public const double Top = 8.0;

    /// <summary>The speed at the bottom of the body.</summary>
    public const double Bottom = 0.125;

    /// <summary>How far inside the body's top and bottom the scale runs.</summary>
    public const double Padding = 4.0;

    /// <summary>The shortest body the lane is drawn on and can be grabbed on.</summary>
    public const double MinimumHeight = 24.0;

    /// <summary>How close to a point, in pixels, a press grabs it.</summary>
    public const double HandleZone = 6.0;

    /// <summary>How close to the line, in pixels, a Ctrl+press adds a point.</summary>
    public const double LineZone = 5.0;

    /// <summary>True when a clip has a speed lane: a picture clip with a speed curve, tall enough to hold one.</summary>
    public static bool Shown(ClipView clip, Rect body)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Kind != TrackKind.Audio && clip.Clip.IsRemapped && body.Height >= MinimumHeight;
    }

    /// <summary>Where a speed sits on a body.</summary>
    public static double Y(Rect body, double speed)
    {
        double span = Math.Max(1.0, body.Height - (Padding * 2));
        double doublings = Math.Log2(Math.Clamp(speed, Bottom, Top));
        return body.Top + Padding + ((Math.Log2(Top) - doublings) / (Math.Log2(Top) - Math.Log2(Bottom)) * span);
    }

    /// <summary>The speed at a height on a body, to a whole percent.</summary>
    public static double Speed(Rect body, double y)
    {
        double span = Math.Max(1.0, body.Height - (Padding * 2));
        double fraction = Math.Clamp((y - body.Top - Padding) / span, 0.0, 1.0);
        double doublings = Math.Log2(Top) - (fraction * (Math.Log2(Top) - Math.Log2(Bottom)));
        return Math.Round(Math.Pow(2, doublings) * 100) / 100;
    }

    /// <summary>A clip's speed at a time on the sequence.</summary>
    public static double Level(ClipView clip, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Clip.Remap is { } remap ? TimeRemap.Speed(remap, time - clip.Start) : clip.Clip.EffectiveSpeed.ToDouble();
    }

    /// <summary>Where a clip's speed points are on the sequence, in time order.</summary>
    public static IReadOnlyList<Flicks> Points(ClipView clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Clip.Remap is KeyframedValue keyed
            ? [.. keyed.Keyframes.Select(key => clip.Start + key.Time).Order()]
            : [];
    }
}
