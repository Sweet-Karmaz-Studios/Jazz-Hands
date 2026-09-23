using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.Shell;

/// <summary>
/// A document tab: one per open sequence, in the document pane under the preview.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ToolViewModel"/> for documents. <see cref="ContentId"/> names the
/// thing the document shows, so a saved layout can reopen the same sequence.
/// </remarks>
/// <param name="contentId">The stable id used in saved layouts.</param>
/// <param name="title">The tab caption.</param>
public abstract partial class DocumentViewModel(string contentId, string title) : ObservableObject
{
    [ObservableProperty]
    private string _title = title;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>The stable id this document is known by in a saved layout.</summary>
    public string ContentId { get; } = contentId;

    /// <summary>
    /// False: a timeline is closed by removing its sequence, not by its tab, or the tabs and the
    /// project would disagree about what exists.
    /// </summary>
    public virtual bool CanClose => false;
}
