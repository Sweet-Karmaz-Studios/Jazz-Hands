using System.Windows;

namespace JazzHands.App.Services;

/// <summary>
/// What a drag out of the media panel carries.
/// </summary>
/// <remarks>
/// Media ids and nothing else. A drop target resolves them against the project rather than
/// holding on to view models, so a drag survives the list being refiltered underneath it, works
/// between panels that know nothing about each other, and turns into a command naming the same
/// ids the CLI would name.
///
/// File paths are also offered, so dragging into another application does something sensible.
/// </remarks>
public static class MediaDragData
{
    /// <summary>The clipboard format name. Versioned, because a drop target has to be able to refuse an old one.</summary>
    public const string Format = "JazzHands.MediaIds.v1";

    /// <summary>Builds the payload for a drag.</summary>
    /// <param name="ids">The media ids being dragged, in the order they were selected.</param>
    /// <param name="paths">Their files, for dropping outside the application. Optional.</param>
    public static DataObject Create(IReadOnlyList<string> ids, IReadOnlyList<string>? paths = null)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            throw new ArgumentException("A drag has to carry at least one media id.", nameof(ids));
        }

        var data = new DataObject();
        data.SetData(Format, string.Join('\n', ids));

        if (paths is { Count: > 0 })
        {
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, paths));
        }

        return data;
    }

    /// <summary>Reads the media ids out of a drop, or an empty list when it is not ours.</summary>
    public static IReadOnlyList<string> Ids(IDataObject? data)
    {
        if (data?.GetDataPresent(Format) != true)
        {
            return [];
        }

        return data.GetData(Format) is string joined
            ? joined.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];
    }
}
