using AvalonDock.Layout;
using JazzHands.App.ViewModels.Playback;

namespace JazzHands.App.Shell;

/// <summary>
/// Decides which pane a panel opens in when there is no saved layout to say.
/// </summary>
/// <remarks>
/// AvalonDock otherwise puts every anchorable in the first pane it finds, which would stack the
/// program monitor as a tab behind the media bin. Phase 27's layout service replaces this with
/// workspaces; until then the preview gets the big pane and everything else the side one.
/// </remarks>
public sealed class PanelPlacement : ILayoutUpdateStrategy
{
    /// <summary>The pane the program monitor opens in.</summary>
    public const string ProgramPane = "ProgramPane";

    /// <summary>The pane every other panel opens in.</summary>
    public const string ToolsPane = "ToolsPane";

    /// <summary>The pane on the right, where the inspector goes.</summary>
    public const string InspectorPane = "InspectorPane";

    /// <inheritdoc />
    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(anchorableToShow);

        string name = anchorableToShow.Content switch
        {
            PreviewPanelViewModel => ProgramPane,
            ViewModels.Inspector.InspectorPanelViewModel => InspectorPane,
            _ => ToolsPane,
        };
        LayoutAnchorablePane? pane = layout.Descendents().OfType<LayoutAnchorablePane>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        if (pane is null)
        {
            return false;
        }

        pane.Children.Add(anchorableToShow);
        return true;
    }

    /// <inheritdoc />
    public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
    {
    }

    /// <inheritdoc />
    public bool BeforeInsertDocument(LayoutRoot layout, LayoutDocument documentToShow, ILayoutContainer destinationContainer) => false;

    /// <inheritdoc />
    public void AfterInsertDocument(LayoutRoot layout, LayoutDocument documentShown)
    {
    }
}
