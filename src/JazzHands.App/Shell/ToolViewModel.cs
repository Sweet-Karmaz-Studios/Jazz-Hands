using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.Shell;

/// <summary>
/// A dockable panel.
/// </summary>
/// <remarks>
/// <see cref="ContentId"/> is the name the layout file stores and the name the layout service
/// resolves back to a viewmodel, so it is a stable string and not the type name. Phase 27 adds
/// the layout service, the workspaces and the rest of the panels; this is the contract they will
/// all meet.
/// </remarks>
/// <param name="contentId">The stable id used in saved layouts.</param>
/// <param name="title">The tab caption.</param>
public abstract partial class ToolViewModel(string contentId, string title) : ObservableObject
{
    [ObservableProperty]
    private string _title = title;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>The stable id this panel is known by in a saved layout.</summary>
    public string ContentId { get; } = contentId;

    /// <summary>Whether the tab offers to close. Panels do; Phase 27's layout service brings them back.</summary>
    public virtual bool CanClose => true;
}
