using System.Numerics;
using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>Creates the identifiers every model record carries.</summary>
/// <remarks>
/// ULIDs, as 26-character strings: sortable by creation time, safe in a file name, and readable
/// enough that a person editing a .jazz file by hand can tell two of them apart. Never integers,
/// which collide the moment two machines edit the same project, and never GUIDs with braces.
/// </remarks>
public static class Id
{
    /// <summary>A fresh identifier.</summary>
    public static string New() => Ulid.NewUlid().ToString();

    /// <summary>True when a string is a well-formed identifier.</summary>
    public static bool IsValid(string? value) => value is { Length: 26 } && Ulid.TryParse(value, out _);
}

/// <summary>What a track carries.</summary>
public enum TrackKind
{
    /// <summary>Picture.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Captions.</summary>
    Subtitle,

    /// <summary>An effect-only track applied to everything beneath it.</summary>
    Adjustment,
}

/// <summary>How a clip is combined with what is underneath it.</summary>
public enum BlendMode
{
    /// <summary>Straight over, respecting alpha. The default.</summary>
    Normal,

    /// <summary>Adds light. Good for sparks and flares.</summary>
    Add,

    /// <summary>Multiplies, darkening.</summary>
    Multiply,

    /// <summary>Inverse multiply, lightening.</summary>
    Screen,

    /// <summary>Multiply or screen depending on the base.</summary>
    Overlay,

    /// <summary>Keeps the darker of the two.</summary>
    Darken,

    /// <summary>Keeps the lighter of the two.</summary>
    Lighten,

    /// <summary>Absolute difference.</summary>
    Difference,

    /// <summary>A gentle overlay: darkens or lightens depending on the layer.</summary>
    SoftLight,

    /// <summary>Overlay with the roles swapped: the layer decides multiply or screen.</summary>
    HardLight,
}

/// <summary>Which of a source's channels an audio clip plays.</summary>
/// <remarks>
/// A capture card or a recorder often puts a mono microphone on one side of a stereo stream and
/// nothing, or a second microphone, on the other. Played as it is, that voice sits hard left.
/// Picking the one channel turns it into a mono signal the clip's pan then places, centred unless
/// somebody says otherwise.
/// </remarks>
public enum AudioChannelMap
{
    /// <summary>Every channel, as the source has them. The default.</summary>
    Auto,

    /// <summary>The left channel alone, as a mono signal.</summary>
    Left,

    /// <summary>The right channel alone, as a mono signal.</summary>
    Right,

    /// <summary>Every channel summed to one mono signal.</summary>
    Mono,
}

/// <summary>Where a transition sits relative to the cut.</summary>
public enum TransitionAlignment
{
    /// <summary>Half before the cut, half after. The usual choice.</summary>
    Centered,

    /// <summary>Entirely before the cut.</summary>
    EndOfLeft,

    /// <summary>Entirely after the cut.</summary>
    StartOfRight,
}

/// <summary>Position, rotation and scale applied to a clip before compositing.</summary>
/// <param name="Position">Offset from the frame centre in pixels.</param>
/// <param name="Scale">Multiplier per axis; 1,1 is native size.</param>
/// <param name="Rotation">Degrees clockwise.</param>
/// <param name="Anchor">The point the clip rotates and scales about, relative to its centre.</param>
public sealed record Transform(
    AnimatedValue Position,
    AnimatedValue Scale,
    AnimatedValue Rotation,
    AnimatedValue Anchor) : IEquatable<Transform>
{
    /// <summary>No transform at all, which is what a clip gets when it is added.</summary>
    public static Transform Identity { get; } = new(
        AnimatedValue.Constant(new ParamValue.Float2(Vector2.Zero)),
        AnimatedValue.Constant(new ParamValue.Float2(Vector2.One)),
        AnimatedValue.Constant(0.0f),
        AnimatedValue.Constant(new ParamValue.Float2(Vector2.Zero)));
}

/// <summary>How much of each side of a clip's picture is cut away.</summary>
/// <param name="Left">Percent of the width taken off the left, 0 to 100.</param>
/// <param name="Top">Percent of the height taken off the top.</param>
/// <param name="Right">Percent of the width taken off the right.</param>
/// <param name="Bottom">Percent of the height taken off the bottom.</param>
public sealed record Crop(
    AnimatedValue Left,
    AnimatedValue Top,
    AnimatedValue Right,
    AnimatedValue Bottom) : IEquatable<Crop>
{
    /// <summary>Nothing cut away.</summary>
    public static Crop None { get; } = new(
        AnimatedValue.Constant(0.0f),
        AnimatedValue.Constant(0.0f),
        AnimatedValue.Constant(0.0f),
        AnimatedValue.Constant(0.0f));
}

/// <summary>The outline a mask follows.</summary>
public enum MaskShape
{
    /// <summary>An axis-aligned rectangle given by its bounds.</summary>
    Rectangle,

    /// <summary>The ellipse inscribed in its bounds.</summary>
    Ellipse,

    /// <summary>Straight edges through a list of points.</summary>
    Polygon,

    /// <summary>A path of straight and cubic bezier segments.</summary>
    Bezier,
}

/// <summary>How a mask combines with the masks before it on the same clip.</summary>
public enum MaskMode
{
    /// <summary>Adds its area to what is shown.</summary>
    Add,

    /// <summary>Takes its area away from what is shown.</summary>
    Subtract,

    /// <summary>Shows only where it overlaps what is already shown.</summary>
    Intersect,
}

/// <summary>
/// A shape that limits what of a clip, or of one effect on it, is seen.
/// </summary>
/// <remarks>
/// Coordinates are in the clip's own source pixels, so a mask stays on the same part of the
/// picture when the clip is moved, scaled or rotated. A rectangle or ellipse is its
/// <see cref="Bounds"/>; a polygon or bezier is its <see cref="PathData"/>, in SVG path syntax
/// (<c>M 10 10 L 200 10 C 250 50 250 150 200 190 Z</c>). On a clip, masks limit its picture; on an
/// effect, they limit where the effect applies, the rest of the picture passing through as it was.
/// </remarks>
/// <param name="Id">The mask identifier.</param>
/// <param name="Shape">The outline.</param>
/// <param name="Bounds">x, y, width and height, for a rectangle or ellipse.</param>
/// <param name="PathData">The outline of a polygon or bezier path.</param>
/// <param name="Feather">Softening of the edge, in sequence pixels.</param>
/// <param name="Opacity">How strongly it applies, 0 to 1.</param>
/// <param name="Mode">How it combines with the masks before it.</param>
/// <param name="Invert">Keep the outside instead of the inside.</param>
/// <param name="Enabled">Masks can be switched off without being removed.</param>
/// <param name="Expansion">Grows the shape outwards by this many sequence pixels, or shrinks it when negative.</param>
public sealed record Mask(
    string Id,
    MaskShape Shape,
    AnimatedValue? Bounds = null,
    AnimatedValue? PathData = null,
    AnimatedValue? Feather = null,
    AnimatedValue? Opacity = null,
    MaskMode Mode = MaskMode.Add,
    bool Invert = false,
    bool Enabled = true,
    AnimatedValue? Expansion = null) : IEquatable<Mask>;

/// <summary>A fade at one end of a clip.</summary>
/// <param name="Duration">How long the fade lasts. Zero means none.</param>
/// <param name="Curve">The shape of the fade.</param>
public sealed record Fade(Flicks Duration, Interp Curve = Interp.Linear) : IEquatable<Fade>
{
    /// <summary>No fade.</summary>
    public static Fade None { get; } = new(Flicks.Zero);

    /// <summary>True when this fade does nothing.</summary>
    public bool IsNone => Duration.IsZero;
}

/// <summary>An effect instance on a clip or a track.</summary>
/// <param name="Id">The instance identifier.</param>
/// <param name="TypeId">Which effect, for example video.blur.gaussian.</param>
/// <param name="Enabled">Effects can be bypassed without removing them.</param>
/// <param name="Parameters">Parameter values by name, each of which may be animated.</param>
/// <param name="Masks">Shapes limiting where the effect applies, in the order they combine; everywhere when none.</param>
public sealed record Effect(
    string Id,
    string TypeId,
    bool Enabled,
    EquatableArray<EffectParameter> Parameters,
    EquatableArray<Mask> Masks = default) : IEquatable<Effect>
{
    /// <summary>Creates an effect with no parameters set, so every parameter takes its default.</summary>
    public static Effect Create(string typeId) =>
        new(Model.Id.New(), typeId, Enabled: true, EquatableArray<EffectParameter>.Empty);

    /// <summary>The value of a parameter, or null when it is not set and the default applies.</summary>
    public AnimatedValue? Parameter(string name)
    {
        foreach (EffectParameter parameter in Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
            {
                return parameter.Value;
            }
        }

        return null;
    }

    /// <summary>A copy with one parameter set, replacing any existing value.</summary>
    public Effect WithParameter(string name, AnimatedValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        int index = Parameters.IndexOf(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));
        EquatableArray<EffectParameter> updated = index < 0
            ? Parameters.Add(new EffectParameter(name, value))
            : Parameters.SetItem(index, new EffectParameter(name, value));

        return this with { Parameters = updated };
    }
}

/// <summary>One named parameter value on an effect.</summary>
/// <param name="Name">The parameter name, as the effect declares it.</param>
/// <param name="Value">Its value, static or keyframed.</param>
public sealed record EffectParameter(string Name, AnimatedValue Value) : IEquatable<EffectParameter>;

/// <summary>A transition between two adjacent clips on a track.</summary>
/// <param name="Id">The transition identifier.</param>
/// <param name="TypeId">Which transition, for example transition.dissolve.</param>
/// <param name="LeftClipId">The outgoing clip.</param>
/// <param name="RightClipId">The incoming clip.</param>
/// <param name="Duration">How long the transition runs.</param>
/// <param name="Alignment">Where it sits relative to the cut.</param>
/// <param name="Parameters">Parameter values by name.</param>
public sealed record Transition(
    string Id,
    string TypeId,
    string LeftClipId,
    string RightClipId,
    Flicks Duration,
    TransitionAlignment Alignment,
    EquatableArray<EffectParameter> Parameters) : IEquatable<Transition>
{
    /// <summary>The value of a parameter, or null when it is not set and the default applies.</summary>
    public AnimatedValue? Parameter(string name) => AsEffect().Parameter(name);

    /// <summary>
    /// The transition's type and parameters as an effect instance, which is what parameter
    /// evaluation reads. Nothing stores it.
    /// </summary>
    public Effect AsEffect() => new(Id, TypeId, Enabled: true, Parameters);

    /// <summary>True when it joins this clip to another, on either side.</summary>
    public bool Touches(string clipId) =>
        string.Equals(LeftClipId, clipId, StringComparison.Ordinal) || string.Equals(RightClipId, clipId, StringComparison.Ordinal);
}

/// <summary>A point or range of interest on a sequence, a clip or a piece of media.</summary>
/// <param name="Id">The marker identifier.</param>
/// <param name="Time">Where it sits.</param>
/// <param name="Duration">Zero for a point marker, non-zero for a range.</param>
/// <param name="Name">Its label.</param>
/// <param name="Color">A colour for the timeline, as an sRGB hex string.</param>
/// <param name="Note">Longer text, shown on hover and in the marker list.</param>
/// <param name="IsChapter">True when this marker should be exported as a chapter.</param>
public sealed record Marker(
    string Id,
    Flicks Time,
    Flicks Duration,
    string Name,
    string Color = "#FFCC00",
    string Note = "",
    bool IsChapter = false) : IEquatable<Marker>
{
    /// <summary>True when the marker covers a span rather than an instant.</summary>
    public bool IsRange => !Duration.IsZero;

    /// <summary>The span this marker covers.</summary>
    public TimeRange Range => new(Time, Duration);
}

/// <summary>
/// One piece of content on a track.
/// </summary>
/// <remarks>
/// A clip references exactly one source: a media item, a generator (titles, colour, shapes) or
/// another sequence (a compound clip). <see cref="SourceIn"/> is where playback starts inside
/// that source, and <see cref="Range"/> is where it lands on the timeline; the two are
/// independent, which is what makes trimming and slipping different operations.
/// </remarks>
/// <param name="Id">The clip identifier.</param>
/// <param name="Range">Where the clip sits on the timeline.</param>
/// <param name="SourceIn">Where playback starts inside the source.</param>
/// <param name="MediaId">The media item, when this clip plays a file.</param>
/// <param name="GeneratorId">The generator type, when this clip is synthetic.</param>
/// <param name="SequenceId">The nested sequence, when this clip is a compound.</param>
/// <param name="SourceStreamIndex">Which stream of the media to play.</param>
/// <param name="Speed">Playback rate as an exact rational; 1/1 is normal.</param>
/// <param name="Reverse">Play the source backwards.</param>
/// <param name="Enabled">Disabled clips stay on the timeline but do not render.</param>
/// <param name="Transform">Position, scale and rotation.</param>
/// <param name="Opacity">Opacity, which may be animated.</param>
/// <param name="BlendMode">How this clip combines with what is underneath.</param>
/// <param name="Volume">Gain in decibels, which may be animated.</param>
/// <param name="Pan">Stereo position from -1 to 1, which may be animated.</param>
/// <param name="FadeIn">Fade at the start.</param>
/// <param name="FadeOut">Fade at the end.</param>
/// <param name="Effects">Effects in application order.</param>
/// <param name="Markers">Markers relative to the clip start.</param>
/// <param name="LinkGroupId">Clips sharing this identifier move together, as a camera and its sound do.</param>
/// <param name="GroupId">Clips sharing this identifier are selected together.</param>
/// <param name="Name">A display name, usually taken from the media on insert.</param>
/// <param name="ChannelMap">Which of the source's channels an audio clip plays, when not all of them as they are. Null for all.</param>
/// <param name="Crop">How much of each side of the picture is cut away. Null for none.</param>
/// <param name="Masks">Shapes limiting what of the picture is seen, in the order they combine.</param>
/// <param name="Hold">True for a freeze frame: the clip shows the frame at <c>SourceIn</c> for its whole length, and is silent.</param>
/// <param name="ToneMap">How an HDR source is tone mapped for this clip, over the project's default. Null for the project's.</param>
public sealed record Clip(
    string Id,
    TimeRange Range,
    Flicks SourceIn,
    string? MediaId = null,
    string? GeneratorId = null,
    string? SequenceId = null,
    int SourceStreamIndex = 0,
    Rational? Speed = null,
    bool Reverse = false,
    bool Enabled = true,
    Transform? Transform = null,
    AnimatedValue? Opacity = null,
    BlendMode BlendMode = BlendMode.Normal,
    AnimatedValue? Volume = null,
    AnimatedValue? Pan = null,
    Fade? FadeIn = null,
    Fade? FadeOut = null,
    EquatableArray<Effect> Effects = default,
    EquatableArray<Marker> Markers = default,
    string? LinkGroupId = null,
    string? GroupId = null,
    string Name = "",
    AudioChannelMap? ChannelMap = null,
    Crop? Crop = null,
    EquatableArray<Mask> Masks = default,
    bool? Hold = null,
    ToneMapping? ToneMap = null) : IEquatable<Clip>
{
    /// <summary>Playback rate, defaulting to normal speed.</summary>
    public Rational EffectiveSpeed => Speed ?? Rational.One;

    /// <summary>Where the clip starts on the timeline.</summary>
    public Flicks Start => Range.Start;

    /// <summary>The first position after the clip, exclusive.</summary>
    public Flicks End => Range.End;

    /// <summary>How long the clip occupies the timeline.</summary>
    public Flicks Duration => Range.Duration;

    /// <summary>
    /// How much source material the clip consumes, which is its timeline duration scaled by
    /// speed. A clip at 2x uses twice the source it occupies on the timeline.
    /// </summary>
    public Flicks SourceDuration => ScaleBySpeed(Range.Duration, EffectiveSpeed);

    /// <summary>The first source position after the clip.</summary>
    public Flicks SourceOut => SourceIn + SourceDuration;

    /// <summary>The source range this clip plays.</summary>
    public TimeRange SourceRange => new(SourceIn, SourceDuration);

    /// <summary>True when this clip plays a media file.</summary>
    public bool IsMedia => MediaId is not null;

    /// <summary>True when this clip is a generator such as a title or a colour.</summary>
    public bool IsGenerator => GeneratorId is not null;

    /// <summary>True when this clip nests another sequence.</summary>
    public bool IsCompound => SequenceId is not null;

    /// <summary>True for a freeze frame, which shows one source frame for its whole length.</summary>
    public bool IsHold => Hold == true;

    /// <summary>
    /// The source position shown at a timeline position, before it is snapped to the source's
    /// frame grid. Outside the clip this extrapolates, which is what trimming previews need. A
    /// freeze frame shows its <c>SourceIn</c> throughout.
    /// </summary>
    public Flicks SourceTimeAt(Flicks timelineTime)
    {
        if (IsHold)
        {
            return SourceIn;
        }

        Flicks offset = timelineTime - Range.Start;
        Flicks scaled = ScaleBySpeed(offset, EffectiveSpeed);
        return Reverse ? SourceOut - scaled : SourceIn + scaled;
    }

    /// <summary>
    /// How long this clip would occupy the timeline at another rate, showing the same source.
    /// </summary>
    /// <remarks>
    /// Halving the speed doubles the duration. Exact, because the arithmetic is done on the
    /// rational rather than on a double: a clip at 1001/1000 speed stays at 1001/1000 speed.
    /// </remarks>
    public Flicks DurationForSpeed(Rational speed) => ScaleBySpeed(SourceDuration, speed.Inverse);

    /// <summary>Scales a duration by a playback rate, exactly.</summary>
    internal static Flicks ScaleBySpeed(Flicks duration, Rational speed)
    {
        if (speed.IsZero)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "A clip cannot play at zero speed.");
        }

        Int128 scaled = (Int128)duration.Value * speed.Num / speed.Den;
        return new Flicks((long)scaled);
    }
}

/// <summary>
/// A lane of clips. Tracks stack, with later ones drawn over earlier ones for video and summed
/// for audio.
/// </summary>
/// <param name="Id">The track identifier.</param>
/// <param name="Kind">What the track carries.</param>
/// <param name="Name">Its display name, for example V1 or Mic.</param>
/// <param name="Order">Stacking order, low to high.</param>
/// <param name="Clips">Clips, kept sorted by start time.</param>
/// <param name="Transitions">Transitions between adjacent clips.</param>
/// <param name="Effects">Effects applied to the whole track.</param>
/// <param name="Locked">Locked tracks reject edits.</param>
/// <param name="Muted">Muted tracks do not contribute.</param>
/// <param name="Solo">When any track is soloed, only soloed tracks contribute.</param>
/// <param name="Height">Display height in device-independent pixels.</param>
/// <param name="Color">A colour for the timeline, as an sRGB hex string.</param>
/// <param name="Volume">Track gain in decibels.</param>
/// <param name="Pan">Track stereo position.</param>
/// <param name="SyncLock">
/// Whether ripple edits on other tracks move this one too, so it stays in sync with them. Null is
/// the default, which is on, as it is in Premiere and Resolve; false turns it off.
/// </param>
public sealed record Track(
    string Id,
    TrackKind Kind,
    string Name,
    int Order,
    EquatableArray<Clip> Clips = default,
    EquatableArray<Transition> Transitions = default,
    EquatableArray<Effect> Effects = default,
    bool Locked = false,
    bool Muted = false,
    bool Solo = false,
    double Height = 72.0,
    string Color = "#3A6EA5",
    AnimatedValue? Volume = null,
    AnimatedValue? Pan = null,
    bool? SyncLock = null) : IEquatable<Track>
{
    /// <summary>The first position after the last clip, or zero for an empty track.</summary>
    public Flicks Duration => Clips.IsEmpty ? Flicks.Zero : Clips[^1].End;

    /// <summary>True for a track that carries sound.</summary>
    public bool IsAudio => Kind == TrackKind.Audio;

    /// <summary>The clip with the given identifier, or null.</summary>
    public Clip? Clip(string clipId)
    {
        foreach (Clip clip in Clips)
        {
            if (string.Equals(clip.Id, clipId, StringComparison.Ordinal))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>The index of a clip, or -1.</summary>
    public int IndexOf(string clipId) =>
        Clips.IndexOf(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal));

    /// <summary>True when ripple edits elsewhere move this track too. On unless turned off.</summary>
    public bool IsSyncLocked => SyncLock ?? true;
}

/// <summary>
/// Frame rate, resolution and audio format. A sequence may override the project's settings.
/// </summary>
/// <param name="FrameRate">The frame grid everything snaps to.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="SampleRate">Audio sample rate.</param>
/// <param name="ChannelCount">Audio channel count; 2 for stereo, 6 for 5.1.</param>
/// <param name="ColorSpace">Working colour space name, for example bt709.</param>
/// <param name="ToneMap">How HDR sources are tone mapped unless a clip says otherwise. Null for BT.2390 at half desaturation.</param>
/// <param name="Transitions">What the default transition shortcuts add. Null for a one second crossfade and an equal power sound crossfade.</param>
public sealed record ProjectSettings(
    Rational FrameRate,
    int Width,
    int Height,
    int SampleRate = 48000,
    int ChannelCount = 2,
    string ColorSpace = "bt709",
    ToneMapping? ToneMap = null,
    TransitionDefaults? Transitions = null) : IEquatable<ProjectSettings>
{
    /// <summary>The default transitions, with nothing set meaning the built-in ones.</summary>
    public TransitionDefaults EffectiveTransitions => Transitions ?? TransitionDefaults.Standard;

    /// <summary>1080p at 30 fps, which is what a project gets when nothing else is said.</summary>
    public static ProjectSettings Default { get; } = new(Rational.Fps30, 1920, 1080);

    /// <summary>How long one frame lasts.</summary>
    public Flicks FrameDuration => Flicks.FromFrames(1, FrameRate);

    /// <summary>The frame size as a string, for display and the CLI.</summary>
    public string Size => $"{Width}x{Height}";
}

/// <summary>What the default transition shortcuts add, and how long it lasts.</summary>
/// <param name="Video">The picture transition type.</param>
/// <param name="Audio">The sound transition type.</param>
/// <param name="Duration">How long both last.</param>
public sealed record TransitionDefaults(string Video, string Audio, Flicks Duration) : IEquatable<TransitionDefaults>
{
    /// <summary>The picture crossfade.</summary>
    public const string Crossfade = "transition.crossfade";

    /// <summary>The equal power sound crossfade.</summary>
    public const string EqualPower = "transition.audio.equal-power";

    /// <summary>A one second crossfade of picture and sound, as Premiere and Resolve start with.</summary>
    public static TransitionDefaults Standard { get; } = new(Crossfade, EqualPower, Flicks.OneSecond);
}

/// <summary>
/// A file the project uses, with everything the probe found out about it.
/// </summary>
/// <remarks>
/// The probe result is kept here rather than looked up, so that opening a project does not have
/// to touch every file it references: a project whose drive is unplugged still shows its bin,
/// its durations and its stream lists, and says which files are missing rather than refusing to
/// open. <see cref="Hash"/> is what notices a file that has been replaced.
/// </remarks>
/// <param name="Id">The media identifier.</param>
/// <param name="RelativePath">Path relative to the project file, with forward slashes. A numbered pattern for an image sequence.</param>
/// <param name="Name">Display name, usually the file name without its extension.</param>
/// <param name="Duration">How long the file runs.</param>
/// <param name="Hash">A content hash, used as the cache key and to notice a replaced file.</param>
/// <param name="ProxyPath">A proxy file, when one has been made.</param>
/// <param name="Tags">Free-form tags for the media bin.</param>
/// <param name="Kind">A movie, a still, or a numbered image sequence.</param>
/// <param name="Folder">Where it sits in the bin, as a slash-separated path. Empty for the root.</param>
/// <param name="Color">A colour label for the bin, as an sRGB hex string. Empty for none.</param>
/// <param name="Conform">How its picture is fitted to a frame of a different shape.</param>
/// <param name="Deinterlace">Whether to deinterlace on decode.</param>
/// <param name="VfrConform">Whether to remap variable frame timing onto the project's grid.</param>
/// <param name="Info">What the probe found, cached.</param>
/// <param name="Sequence">The numbering, when this is an image sequence.</param>
public sealed record MediaItem(
    string Id,
    string RelativePath,
    string Name,
    Flicks Duration,
    string Hash = "",
    string? ProxyPath = null,
    EquatableArray<string> Tags = default,
    MediaKind Kind = MediaKind.Movie,
    string Folder = "",
    string Color = "",
    ConformPolicy Conform = ConformPolicy.Fit,
    AutoSetting Deinterlace = AutoSetting.Auto,
    AutoSetting VfrConform = AutoSetting.Auto,
    MediaInfo? Info = null,
    ImageSequenceInfo? Sequence = null) : IEquatable<MediaItem>
{
    /// <summary>
    /// True when this item's frames should be remapped onto the project's grid.
    /// </summary>
    /// <remarks>
    /// Auto means "if the file needs it", which is the setting almost everything keeps. A phone
    /// recording whose frames drift is conformed; a camera file on a fixed grid is not, because
    /// conforming one would duplicate and drop frames for nothing.
    /// </remarks>
    public bool ShouldConformFrameRate => VfrConform switch
    {
        AutoSetting.On => true,
        AutoSetting.Off => false,
        _ => Info?.IsVariableFrameRate == true,
    };

    /// <summary>True when this item should be deinterlaced on decode.</summary>
    public bool ShouldDeinterlace => Deinterlace switch
    {
        AutoSetting.On => true,
        AutoSetting.Off => false,
        _ => Info?.IsInterlaced == true,
    };

    /// <summary>True when this item is a still or a run of numbered images.</summary>
    public bool IsImages => Kind is MediaKind.Still or MediaKind.ImageSequence;
}

/// <summary>
/// Marks a sequence as a Quick Trim of one file.
/// </summary>
/// <remarks>
/// A Quick Trim lays the file out where it sits in the source: V1 carries the picture and one
/// audio track per stream carries each sound, every clip at the timeline time equal to its source
/// time. The clips are the stretches to keep and the gaps are what was cut away, so the file
/// stays readable on the timeline and a cut is only ever a gap. Export plays the kept stretches
/// back to back rather than the gaps as black.
/// </remarks>
/// <param name="MediaId">The file being trimmed.</param>
public sealed record QuickTrim(string MediaId) : IEquatable<QuickTrim>;

/// <summary>
/// A timeline: tracks of clips with a shared frame grid.
/// </summary>
/// <param name="Id">The sequence identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Tracks">Tracks, kept sorted by order.</param>
/// <param name="Markers">Sequence markers and chapters.</param>
/// <param name="Settings">Overrides for the project settings, when this sequence differs.</param>
/// <param name="InOut">The in and out points, when a range is set.</param>
/// <param name="QuickTrim">Set when this sequence is a Quick Trim of one file: its clips are the kept stretches.</param>
/// <param name="Magnetic">
/// True when the primary picture track (the lowest video track) never has gaps: an edit that would
/// leave one closes it, rippling the sync-locked tracks. Null is off.
/// </param>
/// <param name="Master">The master bus: its volume and the limiter that ends the mix. Null is unity with the limiter on at -1 dBTP.</param>
public sealed record Sequence(
    string Id,
    string Name,
    EquatableArray<Track> Tracks = default,
    EquatableArray<Marker> Markers = default,
    ProjectSettings? Settings = null,
    TimeRange? InOut = null,
    QuickTrim? QuickTrim = null,
    bool? Magnetic = null,
    MasterBus? Master = null) : IEquatable<Sequence>
{
    /// <summary>The first position after the last clip on any track.</summary>
    public Flicks Duration
    {
        get
        {
            Flicks end = Flicks.Zero;
            foreach (Track track in Tracks)
            {
                end = Flicks.Max(end, track.Duration);
            }

            return end;
        }
    }

    /// <summary>True when the primary picture track closes its gaps.</summary>
    public bool IsMagnetic => Magnetic == true;

    /// <summary>The primary picture track: the lowest video track, or null when there is none.</summary>
    public Track? PrimaryTrack
    {
        get
        {
            Track? lowest = null;
            foreach (Track track in Tracks)
            {
                if (track.Kind == TrackKind.Video && (lowest is null || track.Order < lowest.Order))
                {
                    lowest = track;
                }
            }

            return lowest;
        }
    }

    /// <summary>The track with the given identifier, or null.</summary>
    public Track? Track(string trackId)
    {
        foreach (Track track in Tracks)
        {
            if (string.Equals(track.Id, trackId, StringComparison.Ordinal))
            {
                return track;
            }
        }

        return null;
    }
}

/// <summary>
/// A saved effect chain, applied to a clip or a track in one go.
/// </summary>
/// <remarks>
/// The effects are stored whole, keyframes included, with keyframe times relative to whatever
/// they were saved from. Applying one gives every effect a new identifier.
/// </remarks>
/// <param name="Id">The preset identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Effects">The chain, in application order.</param>
public sealed record EffectPreset(
    string Id,
    string Name,
    EquatableArray<Effect> Effects = default) : IEquatable<EffectPreset>;

/// <summary>An export preset. Filled in by Phase 22; carried here so projects can store their own.</summary>
/// <param name="Id">The preset identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Settings">Encoder settings by name, kept opaque until the export engine exists.</param>
public sealed record ExportPreset(
    string Id,
    string Name,
    EquatableArray<EffectParameter> Settings = default) : IEquatable<ExportPreset>;

/// <summary>
/// Everything in a .jazz file.
/// </summary>
/// <remarks>
/// Immutable, and replaced wholesale by every command. Structural sharing makes that cheap: a
/// project with five hundred clips replaces one clip, one track, one sequence and the root, and
/// shares the rest. The undo stack holds the old roots, which is why undo needs no inverse logic.
/// </remarks>
/// <param name="Id">The project identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="SchemaVersion">The file format version, for migrations.</param>
/// <param name="Settings">Default frame rate, resolution and audio format.</param>
/// <param name="Media">Every file the project references.</param>
/// <param name="Sequences">Every timeline in the project.</param>
/// <param name="ActiveSequenceId">Which sequence the editor is showing.</param>
/// <param name="Presets">Export presets stored with the project.</param>
/// <param name="Created">When the project was made.</param>
/// <param name="Modified">When it was last changed.</param>
/// <param name="EffectPresets">Effect chains saved with the project.</param>
public sealed record Project(
    string Id,
    string Name,
    int SchemaVersion,
    ProjectSettings Settings,
    EquatableArray<MediaItem> Media = default,
    EquatableArray<Sequence> Sequences = default,
    string? ActiveSequenceId = null,
    EquatableArray<ExportPreset> Presets = default,
    DateTimeOffset Created = default,
    DateTimeOffset Modified = default,
    EquatableArray<EffectPreset> EffectPresets = default) : IEquatable<Project>
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates an empty project with one video and one audio track.</summary>
    public static Project CreateNew(string name, ProjectSettings? settings = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();
        var sequence = new Sequence(
            Model.Id.New(),
            name,
            EquatableArray.Create(
                new Track(Model.Id.New(), TrackKind.Video, "V1", 0),
                new Track(Model.Id.New(), TrackKind.Audio, "A1", 1)));

        return new Project(
            Model.Id.New(),
            name,
            CurrentSchemaVersion,
            settings ?? ProjectSettings.Default,
            EquatableArray<MediaItem>.Empty,
            EquatableArray.Create(sequence),
            sequence.Id,
            EquatableArray<ExportPreset>.Empty,
            now,
            now);
    }

    /// <summary>The sequence the editor is showing, or the first one.</summary>
    public Sequence? ActiveSequence =>
        (ActiveSequenceId is null ? null : Sequence(ActiveSequenceId)) ??
        (Sequences.IsEmpty ? null : Sequences[0]);

    /// <summary>The sequence with the given identifier, or null.</summary>
    public Sequence? Sequence(string sequenceId)
    {
        foreach (Sequence sequence in Sequences)
        {
            if (string.Equals(sequence.Id, sequenceId, StringComparison.Ordinal))
            {
                return sequence;
            }
        }

        return null;
    }

    /// <summary>The media item with the given identifier, or null.</summary>
    public MediaItem? MediaItem(string mediaId)
    {
        foreach (MediaItem item in Media)
        {
            if (string.Equals(item.Id, mediaId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>The settings a sequence renders with: its own overrides, or the project's.</summary>
    public ProjectSettings SettingsFor(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Settings ?? Settings;
    }
}

/// <summary>
/// A sequence's master bus: what every track is summed into before it leaves.
/// </summary>
/// <remarks>
/// The mix ends in a true peak limiter, on unless turned off, so an export never clips a
/// codec; its ceiling is in dBTP. Every field is optional so a sequence nobody has mixed stores
/// nothing.
/// </remarks>
/// <param name="Volume">Gain in decibels over sequence time, which may be animated. Null for 0 dB.</param>
/// <param name="Limiter">False to turn the limiter off. Null is on.</param>
/// <param name="Ceiling">The limiter's ceiling in dBTP, -24 to 0. Null for -1.</param>
public sealed record MasterBus(
    AnimatedValue? Volume = null,
    bool? Limiter = null,
    double? Ceiling = null) : IEquatable<MasterBus>
{
    /// <summary>The ceiling a limiter has when none is set.</summary>
    public const double DefaultCeiling = -1.0;

    /// <summary>True when the limiter is on.</summary>
    public bool LimiterEnabled => Limiter != false;

    /// <summary>The ceiling in dBTP.</summary>
    public double CeilingDb => Ceiling ?? DefaultCeiling;

    /// <summary>True when this is the same as having no master bus settings at all.</summary>
    public bool IsDefault => Volume is null && Limiter is null && Ceiling is null;
}
