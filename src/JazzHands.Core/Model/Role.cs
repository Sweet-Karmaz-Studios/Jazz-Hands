namespace JazzHands.Core.Model;

/// <summary>
/// What a track is for (Phase 40): dialogue, music, effects, game sound, picture, titles, or a
/// name of the project's own. Export can write a file per role (stems), and a role can be muted or
/// soloed as a whole while editing.
/// </summary>
/// <remarks>
/// A track with no role set has the one <see cref="Of"/> reads from its kind and name: an OBS
/// capture's Game and Mic tracks are Game and Dialogue, a Music track is Music. So a project from
/// before roles has sensible ones without a migration, and a role set by hand is kept as it is.
/// </remarks>
/// <param name="Name">Its name, which tracks name it by; unique, compared without case.</param>
/// <param name="Color">A colour for the track headers and the mixer, as an sRGB hex string.</param>
/// <param name="Muted">True to silence every track of the role.</param>
/// <param name="Solo">True to hear only the tracks of soloed roles (and soloed tracks).</param>
public sealed record Role(string Name, string Color, bool Muted = false, bool Solo = false) : IEquatable<Role>
{
    /// <summary>Speech.</summary>
    public const string Dialogue = "Dialogue";

    /// <summary>Music.</summary>
    public const string Music = "Music";

    /// <summary>Sound effects.</summary>
    public const string Effects = "Effects";

    /// <summary>A game's own sound.</summary>
    public const string Game = "Game";

    /// <summary>Picture.</summary>
    public const string Video = "Video";

    /// <summary>Titles and subtitles.</summary>
    public const string Titles = "Titles";

    /// <summary>The roles a project has until it says otherwise.</summary>
    public static EquatableArray<Role> BuiltIn { get; } =
    [
        new(Dialogue, "#4C8DFF"),
        new(Music, "#3DBE7C"),
        new(Effects, "#E0A93A"),
        new(Game, "#B46CE0"),
        new(Video, "#3D5A80"),
        new(Titles, "#7A5C99"),
    ];

    /// <summary>The roles a project has: its own, or the built-in six.</summary>
    public static EquatableArray<Role> All(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Roles.IsEmpty ? BuiltIn : project.Roles;
    }

    /// <summary>The role of a project by name, without case, or null.</summary>
    public static Role? Find(Project project, string name) =>
        All(project).FirstOrDefault(role => string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The role a track has: its own, or the one its kind and name suggest.</summary>
    public static string Of(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Role is { Length: > 0 } role)
        {
            return role;
        }

        return track.Kind switch
        {
            TrackKind.Subtitle => Titles,
            TrackKind.Video or TrackKind.Adjustment => Video,
            _ => NameSuggests(track.Name),
        };
    }

    private static string NameSuggests(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("game", StringComparison.Ordinal) || lower.Contains("desktop", StringComparison.Ordinal) => Game,
            _ when lower.Contains("music", StringComparison.Ordinal) || lower.Contains("song", StringComparison.Ordinal) || lower.Contains("score", StringComparison.Ordinal) => Music,
            _ when lower.Contains("sfx", StringComparison.Ordinal) || lower.Contains("effect", StringComparison.Ordinal) || lower.Contains("foley", StringComparison.Ordinal) => Effects,
            _ => Dialogue,
        };
    }

    /// <summary>
    /// Whether a sound track is heard, taking the track's own mute and solo and its role's: a muted
    /// track or role is silent; when anything is soloed, only soloed tracks and the tracks of soloed
    /// roles are heard.
    /// </summary>
    public static bool Heard(Project project, Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        EquatableArray<Role> roles = All(project);
        Role? own = roles.FirstOrDefault(role => string.Equals(role.Name, Of(track), StringComparison.OrdinalIgnoreCase));
        if (track.Muted || own?.Muted == true)
        {
            return false;
        }

        bool anySolo = sequence.Tracks.Any(candidate => candidate.Solo)
            || roles.Any(role => role.Solo && sequence.Tracks.Any(candidate => string.Equals(Of(candidate), role.Name, StringComparison.OrdinalIgnoreCase)));
        return !anySolo || track.Solo || own?.Solo == true;
    }
}
