using System.Windows;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>
/// Where an audio clip's volume line, the rubber band, sits on its body.
/// </summary>
/// <remarks>
/// The control draws the line and the view model decides what a press on it does, so both read
/// its place from here and cannot drift apart. The scale is a fader's: +12 dB at the top of the
/// body, -60 at the bottom, linear in decibels between, so 0 dB sits a sixth of the way down and
/// the line reads the way the Mixer's fader does. Below -60 is silence.
/// </remarks>
public static class VolumeLine
{
    /// <summary>The gap between a track row's edge and a clip's body.</summary>
    public const double ClipInset = 2.0;

    /// <summary>The level at the top of the body.</summary>
    public const double TopDb = 12.0;

    /// <summary>The level at the bottom of the body; anything lower is silence.</summary>
    public const double BottomDb = -60.0;

    /// <summary>What a drag to the bottom sets: the quietest gain a command takes.</summary>
    public const double SilenceDb = -144.0;

    /// <summary>How far inside the body's top and bottom the scale runs, so the line never hides under an edge.</summary>
    public const double Padding = 4.0;

    /// <summary>The shortest body the line is drawn on and can be grabbed on.</summary>
    public const double MinimumHeight = 24.0;

    /// <summary>How close to the line, in pixels, a press grabs it.</summary>
    public const double LineZone = 4.0;

    /// <summary>How close to a keyframe, in pixels, a press grabs the keyframe rather than the line.</summary>
    public const double HandleZone = 6.0;

    /// <summary>A clip's body on the timeline, as the control draws it.</summary>
    public static Rect Body(TimelineGeometry geometry, TrackRow row, ClipView clip)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(clip);

        double left = geometry.XOf(clip.Start);
        double right = geometry.XOf(clip.End);
        return new Rect(left, geometry.TopOf(row) + ClipInset, Math.Max(1.0, right - left - 1.0), Math.Max(1.0, row.Height - (ClipInset * 2)));
    }

    /// <summary>True when a clip has a volume line: a sound clip tall enough to hold one.</summary>
    public static bool Shown(ClipView clip, Rect body) =>
        clip is { Kind: TrackKind.Audio } && body.Height >= MinimumHeight;

    /// <summary>Where a level sits on a body.</summary>
    public static double Y(Rect body, double db)
    {
        double span = Math.Max(1.0, body.Height - (Padding * 2));
        double clamped = Math.Clamp(db, BottomDb, TopDb);
        return body.Top + Padding + ((TopDb - clamped) / (TopDb - BottomDb) * span);
    }

    /// <summary>The level at a height on a body, to a tenth of a decibel, and silence at the bottom.</summary>
    public static double Db(Rect body, double y)
    {
        double span = Math.Max(1.0, body.Height - (Padding * 2));
        double db = TopDb - (Math.Clamp((y - body.Top - Padding) / span, 0.0, 1.0) * (TopDb - BottomDb));
        return db <= BottomDb + 0.05 ? SilenceDb : Math.Round(db, 1);
    }

    /// <summary>A clip's volume at a time on the sequence.</summary>
    public static double Level(ClipView clip, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return ParamEval.Eval(clip.Clip.Volume, ParamTargets.Audio.Params[0], time - clip.Start) is ParamValue.Float level ? level.Value : 0.0;
    }

    /// <summary>Where a clip's volume keyframes are on the sequence, none when it is not animated.</summary>
    public static IEnumerable<Flicks> Keyframes(ClipView clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Clip.Volume is KeyframedValue { IsAnimated: true } keyed
            ? keyed.Keyframes.Select(key => clip.Start + key.Time)
            : [];
    }
}
