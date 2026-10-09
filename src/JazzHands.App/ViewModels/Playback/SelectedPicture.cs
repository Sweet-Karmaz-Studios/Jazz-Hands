using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>
/// The one picture clip a selection is about, for the handles drawn on the preview and the curves:
/// a clip on a picture track, alone or with its own linked sound. Clicking a capture selects its
/// sound with it, and the handles went missing for every clip with sound (seen on screen, 2026-10-09).
/// </summary>
public static class SelectedPicture
{
    /// <summary>The picture clip, or null when the selection is not one picture clip and its sound.</summary>
    public static ClipLocation? Of(Project project, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ids);

        ClipLocation[] found = [.. ids.Select(project.FindClip).OfType<ClipLocation>()];
        ClipLocation[] pictures = [.. found.Where(location => location.Track.Kind is TrackKind.Video or TrackKind.Adjustment)];
        if (found.Length == 1)
        {
            return found[0];
        }

        if (pictures.Length != 1)
        {
            return null;
        }

        ClipLocation picture = pictures[0];
        return found.All(location => location.Clip.Id == picture.Clip.Id
            || (picture.Clip.LinkGroupId is { } group && location.Clip.LinkGroupId == group))
            ? picture
            : null;
    }
}
