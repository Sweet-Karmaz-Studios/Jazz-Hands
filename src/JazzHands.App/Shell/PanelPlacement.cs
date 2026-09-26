using AvalonDock.Layout;

namespace JazzHands.App.Shell;

/// <summary>
/// Decides which pane a panel opens in when there is no saved layout to say.
/// </summary>
/// <remarks>
/// AvalonDock otherwise puts every anchorable in the first pane it finds, which would stack the
/// program monitor as a tab behind the media bin. The workspaces (Phase 27) start from what this
/// places, and <see cref="Panes"/> keeps each pane to a few tabs so their names are not cut short.
/// </remarks>
public sealed class PanelPlacement : ILayoutUpdateStrategy
{
    /// <summary>The pane the program monitor opens in.</summary>
    public const string ProgramPane = "ProgramPane";

    /// <summary>The pane every other panel opens in.</summary>
    public const string ToolsPane = "ToolsPane";

    /// <summary>The pane on the right, where the inspector goes, with the curves and the history.</summary>
    public const string InspectorPane = "InspectorPane";

    /// <summary>The pane under the inspector, for the panels that watch: scopes, meters, export queue, console, log.</summary>
    public const string UtilityPane = "UtilityPane";

    /// <summary>Which pane each panel opens in when no saved layout says; the rest go to <see cref="ToolsPane"/>.</summary>
    public static IReadOnlyDictionary<string, string> Panes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["preview"] = ProgramPane,
        ["inspector"] = InspectorPane,
        ["curves"] = InspectorPane,
        ["history"] = InspectorPane,
        ["mixer"] = ProgramPane,
        ["scopes"] = UtilityPane,
        ["meters"] = UtilityPane,
        ["exportQueue"] = UtilityPane,
        ["console"] = UtilityPane,
        ["log"] = UtilityPane,
    };

    /// <inheritdoc />
    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(anchorableToShow);

        string name = anchorableToShow.Content is ToolViewModel { ContentId: { } id } && Panes.TryGetValue(id, out string? named) ? named : ToolsPane;
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
