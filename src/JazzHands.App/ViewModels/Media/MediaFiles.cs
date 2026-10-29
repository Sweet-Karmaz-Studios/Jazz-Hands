using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Media;

/// <summary>Whether media files are on disk, for the media panel's offline rows and the timeline's offline clips.</summary>
internal static class MediaFiles
{
    /// <summary>Whether a media item's file (or an image sequence's folder) is where the project says.</summary>
    public static bool Present(string projectPath, MediaItem item)
    {
        try
        {
            string path = projectPath.Length == 0 ? System.IO.Path.GetFullPath(item.RelativePath) : Core.Serialization.ProjectPaths.Resolve(projectPath, item.RelativePath);
            return item.Kind == MediaKind.ImageSequence ? System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)) : System.IO.File.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return false;
        }
    }
}
