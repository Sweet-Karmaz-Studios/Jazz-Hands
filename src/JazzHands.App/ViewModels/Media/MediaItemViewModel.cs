using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Media;

/// <summary>One row in the media panel.</summary>
/// <remarks>
/// A projection of a <see cref="MediaItem"/> into strings the list can draw without formatting
/// anything per frame, plus the lowercased haystack the search box scans. Rebuilt only when the
/// underlying item actually changes, which the panel works out by reference.
/// </remarks>
public sealed partial class MediaItemViewModel : ObservableObject
{
    [ObservableProperty]
    private MediaItem _item;

    [ObservableProperty]
    private bool _isStreamListOpen;

    /// <summary>Wraps a media item.</summary>
    public MediaItemViewModel(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        Streams = [.. Describe(item)];
        Haystack = BuildHaystack(item, Streams);
    }

    /// <summary>The item's id, which is what a drag carries and what a command names.</summary>
    public string Id => Item.Id;

    /// <summary>The name shown in the list.</summary>
    public string Name => Item.Name;

    /// <summary>The bin folder, as a slash separated path. Empty for the root.</summary>
    public string Folder => Item.Folder;

    /// <summary>The colour label's name, or empty.</summary>
    public string Color => Item.Color;

    /// <summary>The duration column.</summary>
    public string Duration => Timecode.Format(Item.Duration, Rate);

    /// <summary>The frame rate column, blank for something with no picture.</summary>
    public string FrameRate => Picture?.FrameRate is { } rate && !rate.IsZero
        ? rate.ToDisplayString()
        : string.Empty;

    /// <summary>The size column, in whichever unit reads best.</summary>
    public string Size => FormatBytes(Item.Info?.SizeBytes ?? 0);

    /// <summary>The codec column.</summary>
    public string Codec => Picture?.Codec ?? Item.Info?.PrimaryStream?.Codec ?? string.Empty;

    /// <summary>The resolution column, blank for something with no picture.</summary>
    public string Resolution => Picture is { Width: > 0 } picture
        ? $"{picture.Width}x{picture.Height}"
        : string.Empty;

    /// <summary>How many audio streams there are, as a column.</summary>
    public string AudioStreams => Item.Info is null
        ? string.Empty
        : Item.Info.AudioStreams.Count() switch
        {
            0 => "none",
            1 => "1",
            int many => many.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>The tags column.</summary>
    public string Tags => string.Join(", ", Item.Tags);

    /// <summary>What kind of media this is, for the icon and the grouping.</summary>
    public MediaKind Kind => Item.Kind;

    /// <summary>A short line under the name: what the file is, in one phrase.</summary>
    public string Summary => Item.Kind switch
    {
        MediaKind.ImageSequence when Item.Sequence is { } sequence =>
            $"{sequence.Count} frames at {sequence.FrameRate.ToDisplayString()}",
        MediaKind.Still => Resolution.Length > 0 ? $"still {Resolution}" : "still",
        _ => string.Join("  ", new[] { Resolution, FrameRate, Codec }.Where(part => part.Length > 0)),
    };

    /// <summary>True when this file needs conforming, so the list can mark it.</summary>
    public bool IsConformed => Item.ShouldConformFrameRate || Item.ShouldDeinterlace;

    /// <summary>Why it is marked, for the tooltip.</summary>
    public string ConformSummary => (Item.ShouldDeinterlace, Item.ShouldConformFrameRate) switch
    {
        (true, true) => "Deinterlaced and conformed to the project frame rate on decode.",
        (true, false) => "Deinterlaced on decode.",
        (false, true) => "Conformed to the project frame rate on decode.",
        _ => string.Empty,
    };

    /// <summary>The streams, for the expandable list under the row.</summary>
    public IReadOnlyList<string> Streams { get; }

    /// <summary>Everything searchable about this item, lowercased and joined.</summary>
    internal string Haystack { get; }

    /// <summary>True when every term in <paramref name="terms"/> appears somewhere in this item.</summary>
    internal bool Matches(string[] terms)
    {
        foreach (string term in terms)
        {
            if (!Haystack.Contains(term, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Human readable bytes. Internal so the formatting is testable on its own.</summary>
    internal static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private MediaStream? Picture => Item.Info?.VideoStreams.FirstOrDefault();

    /// <summary>A frame rate to read the duration against. Anything is better than dividing by zero.</summary>
    private Rational Rate => Picture?.FrameRate is { } rate && !rate.IsZero
        ? rate
        : Item.Sequence?.FrameRate ?? Rational.Fps30;

    private static IEnumerable<string> Describe(MediaItem item)
    {
        if (item.Info is null)
        {
            return [];
        }

        return item.Info.Streams.Select(stream => $"{stream.Index}. {Label(stream)}{stream.Describe()}");
    }

    private static string Label(MediaStream stream) =>
        stream.Title is { Length: > 0 } title ? $"{title}: " : string.Empty;

    private static string BuildHaystack(MediaItem item, IReadOnlyList<string> streams)
    {
        // Built once so a keystroke is a substring scan rather than a walk of the model. Includes
        // the things somebody would plausibly type: the name, where it is, what it is made of.
        var parts = new List<string>
        {
            item.Name,
            item.Folder,
            item.Color,
            item.Kind.ToString(),
        };

        parts.AddRange(item.Tags);
        parts.AddRange(streams);

        if (item.Info is not null)
        {
            parts.Add(item.Info.FormatName);
        }

        return string.Join('\u0001', parts).ToLowerInvariant();
    }
}
