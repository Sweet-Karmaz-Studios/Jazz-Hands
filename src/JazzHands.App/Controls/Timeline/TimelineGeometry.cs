using System.Collections.Immutable;
using System.Windows;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>One track's band on the timeline, in content coordinates (before vertical scroll).</summary>
/// <param name="TrackId">The track.</param>
/// <param name="Kind">Video or audio, which decides what may be dropped on it.</param>
/// <param name="Top">Distance from the top of the first track.</param>
/// <param name="Height">How tall it is drawn.</param>
public readonly record struct TrackRow(string TrackId, TrackKind Kind, double Top, double Height)
{
    /// <summary>Distance from the top of the first track to the bottom of this one.</summary>
    public double Bottom => Top + Height;
}

/// <summary>
/// Where things are on the timeline: the only place time becomes pixels and pixels become time.
/// </summary>
/// <remarks>
/// Horizontally, <see cref="PixelsPerSecond"/> is the zoom and <see cref="Scroll"/> the time at the
/// left edge. Vertically the control has a ruler, then a lane for markers, then the tracks,
/// which scroll by <see cref="VerticalOffset"/> while the ruler and marker lane stay put.
/// Everything that draws or hit tests goes through <see cref="XOf"/> and <see cref="TimeAt"/>, so
/// zoom, scroll and the frame grid are applied the same way everywhere.
/// </remarks>
/// <param name="PixelsPerSecond">Zoom: device independent pixels per second of timeline.</param>
/// <param name="Scroll">The time at the left edge.</param>
/// <param name="FrameRate">The sequence's frame rate, which times snap to.</param>
/// <param name="VerticalOffset">How far the tracks are scrolled up.</param>
/// <param name="Rows">Every track, in display order.</param>
public sealed record TimelineGeometry(
    double PixelsPerSecond,
    Flicks Scroll,
    Rational FrameRate,
    double VerticalOffset,
    ImmutableArray<TrackRow> Rows)
{
    /// <summary>The furthest out the timeline zooms: a hundred seconds a pixel.</summary>
    public const double MinPixelsPerSecond = 0.01;

    /// <summary>The furthest in: two thousand pixels a second, several per frame at 60 fps.</summary>
    public const double MaxPixelsPerSecond = 2000.0;

    /// <summary>Height of the timecode ruler along the top.</summary>
    public const double RulerHeight = 28.0;

    /// <summary>Height of the marker lane under the ruler.</summary>
    public const double MarkerLaneHeight = 16.0;

    /// <summary>Where the first track starts, below the ruler and the marker lane.</summary>
    public const double TracksTop = RulerHeight + MarkerLaneHeight;

    /// <summary>A timeline with nothing on it, at a comfortable zoom.</summary>
    public static TimelineGeometry Empty { get; } = new(50.0, Flicks.Zero, Rational.Fps30, 0.0, []);

    /// <summary>All the tracks stacked, top of the first to bottom of the last.</summary>
    public double ContentHeight => Rows.IsEmpty ? 0.0 : Rows[^1].Bottom;

    /// <summary>The x of a time, relative to the control's left edge.</summary>
    public double XOf(Flicks time) => (time - Scroll).ToSeconds() * PixelsPerSecond;

    /// <summary>The width a duration covers.</summary>
    public double WidthOf(Flicks duration) => duration.ToSeconds() * PixelsPerSecond;

    /// <summary>The time at an x, snapped to the frame grid and never before zero.</summary>
    /// <param name="x">Relative to the control's left edge.</param>
    /// <param name="snapToFrame">False for sub-frame positions, such as a playhead drawn between frames.</param>
    public Flicks TimeAt(double x, bool snapToFrame = true)
    {
        Flicks raw = Scroll + Flicks.FromSeconds(x / PixelsPerSecond);
        if (raw.IsNegative)
        {
            return Flicks.Zero;
        }

        return snapToFrame ? Snap(raw) : raw;
    }

    /// <summary>A time moved to the nearest frame boundary.</summary>
    public Flicks Snap(Flicks time) => Flicks.FromFrames(time.ToFrames(FrameRate, RoundingMode.Nearest), FrameRate);

    /// <summary>A duration of this many pixels, snapped to whole frames.</summary>
    public Flicks DurationOf(double pixels) => Snap(Flicks.FromSeconds(pixels / PixelsPerSecond));

    /// <summary>The y on the control of a row's top edge.</summary>
    public double TopOf(TrackRow row) => TracksTop + row.Top - VerticalOffset;

    /// <summary>
    /// The band a transition's bar is drawn in on a row: along the bottom of the clips, clear of
    /// their names, and where the pointer picks the bar rather than a clip.
    /// </summary>
    public Rect TransitionBand(TrackRow row, Flicks start, Flicks end)
    {
        double height = Math.Clamp(row.Height * 0.4, 10.0, 26.0);
        double top = TopOf(row) + row.Height - height - 4.0;
        double left = XOf(start);
        double width = Math.Max(6.0, XOf(end) - left);
        return new Rect(left, top, width, height);
    }

    /// <summary>The track under a y on the control, or null over the ruler, markers or empty space.</summary>
    public TrackRow? RowAt(double y)
    {
        if (y < TracksTop)
        {
            return null;
        }

        double content = y - TracksTop + VerticalOffset;
        foreach (TrackRow row in Rows)
        {
            if (content >= row.Top && content < row.Bottom)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The row of a track, or null when it is not on this timeline.</summary>
    public TrackRow? Row(string trackId)
    {
        foreach (TrackRow row in Rows)
        {
            if (string.Equals(row.TrackId, trackId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The span of time a control this wide shows.</summary>
    public (Flicks Start, Flicks End) Visible(double width) => (Scroll, Scroll + Flicks.FromSeconds(width / PixelsPerSecond));

    /// <summary>
    /// Zooms by a factor while keeping the time under <paramref name="x"/> where it is, which is
    /// what Ctrl+wheel over a point should do.
    /// </summary>
    public TimelineGeometry ZoomAbout(double x, double factor)
    {
        double zoom = Math.Clamp(PixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
        Flicks anchor = TimeAt(x, snapToFrame: false);
        Flicks scroll = anchor - Flicks.FromSeconds(x / zoom);

        return this with { PixelsPerSecond = zoom, Scroll = scroll.IsNegative ? Flicks.Zero : scroll };
    }

    /// <summary>Scrolls by a number of pixels, never before zero.</summary>
    public TimelineGeometry ScrollBy(double pixels)
    {
        Flicks scroll = Scroll + Flicks.FromSeconds(pixels / PixelsPerSecond);
        return this with { Scroll = scroll.IsNegative ? Flicks.Zero : scroll };
    }

    /// <summary>
    /// The zoom and scroll that fit a duration into a width, with a little room at the end so
    /// the last clip does not touch the edge.
    /// </summary>
    public TimelineGeometry Fit(Flicks duration, double width)
    {
        double seconds = Math.Max(duration.ToSeconds(), 1.0) * 1.05;
        double zoom = Math.Clamp(Math.Max(width, 1.0) / seconds, MinPixelsPerSecond, MaxPixelsPerSecond);
        return this with { PixelsPerSecond = zoom, Scroll = Flicks.Zero };
    }
}
