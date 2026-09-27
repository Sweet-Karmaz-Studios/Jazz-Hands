using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>One camera of a multicam sequence: its picture track and the sound tracks that came with it.</summary>
/// <param name="Name">What it is called: the recording's name.</param>
/// <param name="PictureTrackId">Its picture track, or null for a recording with no picture (a microphone).</param>
/// <param name="SoundTrackIds">Its sound tracks.</param>
public sealed record MulticamAngle(string Name, string? PictureTrackId, EquatableArray<string> SoundTrackIds) : IEquatable<MulticamAngle>;

/// <summary>A cut to another angle at a time, of the picture, the sound or both.</summary>
/// <param name="At">When, in the multicam sequence's time.</param>
/// <param name="Picture">The angle whose picture shows from then, or null to keep the one showing.</param>
/// <param name="Sound">The angle whose sound is heard from then, or null to keep the one heard.</param>
public sealed record AngleSwitch(Flicks At, int? Picture, int? Sound) : IEquatable<AngleSwitch>;

/// <summary>
/// What makes a sequence a multicam clip's source (Phase 41): recordings of the same moment
/// synced on tracks of their own, an angle each, and the switches that say which angle is seen
/// and heard when.
/// </summary>
/// <remarks>
/// The sequence is an ordinary nested sequence, so everything that reads one (the timeline, the
/// inspector, export) still works; the picture draws only the active angle's track and the mix hears
/// only the active sound angle's, from <see cref="ActiveAt"/>. Switches are kept in time order, one
/// at a time at most.
/// </remarks>
/// <param name="Angles">The angles, in the order the 1 to 9 keys and the grid number them.</param>
/// <param name="Switches">The cuts between angles, in time order.</param>
/// <param name="FixedSound">An angle whose sound is heard throughout, whatever the picture; null for sound that follows the switches.</param>
public sealed record Multicam(
    EquatableArray<MulticamAngle> Angles,
    EquatableArray<AngleSwitch> Switches = default,
    int? FixedSound = null) : IEquatable<Multicam>
{
    /// <summary>The picture and sound angles at a time: before the first switch, the first angle for both.</summary>
    public (int Picture, int Sound) ActiveAt(Flicks time)
    {
        int picture = 0;
        int sound = 0;
        foreach (AngleSwitch cut in Switches)
        {
            if (cut.At > time)
            {
                break;
            }

            picture = cut.Picture ?? picture;
            sound = cut.Sound ?? sound;
        }

        return (Clamp(picture), Clamp(FixedSound ?? sound));
    }

    /// <summary>True when a track's picture is drawn at a time: it is the picture of the angle showing, or no angle's.</summary>
    public bool Shows(string trackId, Flicks time)
    {
        int owner = Angles.IndexOf(angle => string.Equals(angle.PictureTrackId, trackId, StringComparison.Ordinal));
        return owner < 0 || owner == ActiveAt(time).Picture;
    }

    /// <summary>True when a track's sound is heard at a time: it is a sound track of the angle heard, or no angle's.</summary>
    public bool Hears(string trackId, Flicks time)
    {
        int owner = Angles.IndexOf(angle => angle.SoundTrackIds.Contains(trackId));
        return owner < 0 || owner == ActiveAt(time).Sound;
    }

    /// <summary>
    /// The multicam with a switch at a time, replacing one already there, keeping the list in
    /// order; a switch that changes nothing from what is active just before is dropped.
    /// </summary>
    public Multicam WithSwitch(AngleSwitch cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        List<AngleSwitch> switches = [.. Switches.Where(existing => existing.At != cut.At)];
        switches.Add(cut);
        switches.Sort((a, b) => a.At.CompareTo(b.At));
        return this with { Switches = [.. switches] };
    }

    /// <summary>The times the picture or sound actually changes, and to what, from the start: the cuts flattening makes.</summary>
    public IReadOnlyList<(Flicks At, int Picture, int Sound)> Changes()
    {
        var changes = new List<(Flicks, int, int)>();
        (int picture, int sound) = ActiveAt(Flicks.MinValue);
        changes.Add((Flicks.Zero, picture, sound));
        foreach (AngleSwitch cut in Switches)
        {
            (int nowPicture, int nowSound) = ActiveAt(cut.At);
            if (nowPicture != picture || nowSound != sound)
            {
                changes.Add((cut.At, nowPicture, nowSound));
                (picture, sound) = (nowPicture, nowSound);
            }
        }

        return changes;
    }

    private int Clamp(int angle) => Angles.IsEmpty ? 0 : Math.Clamp(angle, 0, Angles.Length - 1);
}

/// <summary>The multicam viewer's grid (Phase 41): 2x2 up to four angles with a picture, 3x3 up to nine.</summary>
public static class MulticamGrid
{
    /// <summary>How many columns and rows a grid of this many pictures has.</summary>
    public static int Size(int pictures) => pictures <= 1 ? 1 : pictures <= 4 ? 2 : 3;

    /// <summary>The cell of the k-th picture, as fractions of the frame: left, top, width, height.</summary>
    public static (double Left, double Top, double Width, double Height) Cell(int index, int pictures)
    {
        int size = Size(pictures);
        double side = 1.0 / size;
        return ((index % size) * side, (index / size) * side, side, side);
    }

    /// <summary>
    /// The angle under a point of the frame, given as fractions across and down, or null past the
    /// last one: what a click on the grid means.
    /// </summary>
    public static int? AngleAt(Multicam multicam, double across, double down)
    {
        ArgumentNullException.ThrowIfNull(multicam);
        int[] pictures = [.. Pictures(multicam)];
        int size = Size(pictures.Length);
        int cell = ((int)Math.Clamp(down * size, 0, size - 1) * size) + (int)Math.Clamp(across * size, 0, size - 1);
        return cell < pictures.Length ? pictures[cell] : null;
    }

    /// <summary>The angles with a picture, in order: the grid's cells.</summary>
    public static IEnumerable<int> Pictures(Multicam multicam)
    {
        ArgumentNullException.ThrowIfNull(multicam);
        for (int angle = 0; angle < multicam.Angles.Length; angle++)
        {
            if (multicam.Angles[angle].PictureTrackId is not null)
            {
                yield return angle;
            }
        }
    }

    /// <summary>
    /// The multicam sequence laid out as its grid: every angle's picture track shown, each clip
    /// scaled into its cell. A copy for drawing, never put in the project.
    /// </summary>
    public static Sequence Layout(Sequence sequence, Multicam multicam, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(multicam);
        int[] pictures = [.. Pictures(multicam)];
        int size = Size(pictures.Length);
        var tracks = new List<Track>();
        foreach (Track track in sequence.Tracks)
        {
            int cell = Array.FindIndex(pictures, angle => string.Equals(multicam.Angles[angle].PictureTrackId, track.Id, StringComparison.Ordinal));
            if (cell < 0)
            {
                tracks.Add(track);
                continue;
            }

            (double left, double top, double w, double h) = Cell(cell, pictures.Length);
            var position = new System.Numerics.Vector2((float)((left + (w / 2) - 0.5) * width), (float)((top + (h / 2) - 0.5) * height));
            var transform = new Transform(
                AnimatedValue.Constant(new ParamValue.Float2(position)),
                AnimatedValue.Constant(new ParamValue.Float2(new System.Numerics.Vector2(1.0f / size))),
                AnimatedValue.Constant(0.0f),
                AnimatedValue.Constant(new ParamValue.Float2(System.Numerics.Vector2.Zero)));
            tracks.Add(track with { Clips = [.. track.Clips.Select(clip => clip with { Transform = transform })] });
        }

        return sequence with { Tracks = [.. tracks] };
    }
}
