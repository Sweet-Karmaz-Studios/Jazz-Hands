using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.ViewModels.Media;

/// <summary>One node of the bin's folder tree.</summary>
/// <remarks>
/// The tree is derived from the folder strings on the media items rather than stored: a folder
/// exists because something is in it. That keeps the project file free of empty folders somebody
/// made once and forgot, and means a folder rename is a change to the items, which is undoable
/// like everything else.
/// </remarks>
public sealed partial class MediaFolderViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private int _count;

    /// <summary>Creates a node.</summary>
    /// <param name="path">The full slash separated path, empty for the root.</param>
    /// <param name="name">What to show.</param>
    public MediaFolderViewModel(string path, string name)
    {
        Path = path;
        Name = name;
    }

    /// <summary>The full slash separated path. Empty means everything.</summary>
    public string Path { get; }

    /// <summary>The last segment, or "All media" at the root.</summary>
    public string Name { get; }

    /// <summary>Sub-folders, in name order.</summary>
    public ObservableCollection<MediaFolderViewModel> Children { get; } = [];

    /// <summary>The name with how many items are in it and below it.</summary>
    public string Label => $"{Name} ({Count})";

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e?.PropertyName == nameof(Count))
        {
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Label)));
        }
    }
}
