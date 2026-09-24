using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using JazzHands.App.ViewModels;

namespace JazzHands.App;

/// <summary>
/// The shell window. Phase 27 fills it out with every panel, the workspaces and the layout
/// service; today it holds the docking manager, the media panel, the meters and the preview.
/// </summary>
/// <remarks>
/// The playback keys work wherever the focus is, the way they do in every editor, except while
/// typing: a text box keeps its letters and its space bar. Inside a list the arrows, Home and End
/// move the selection as a list's keys should, and the rest still drive playback. Phase 27's
/// keymap service takes this over and makes it configurable.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;

    /// <summary>Creates the window.</summary>
    /// <param name="model">The window's viewmodel, from the host.</param>
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.PanelsRequested += (_, ids) => BringForward(ids);
    }

    /// <summary>Shows panels and puts each in front of its pane: a workspace, until Phase 27's layouts.</summary>
    private void BringForward(IReadOnlyList<string> ids)
    {
        foreach (LayoutAnchorable panel in Dock.Layout.Descendents().OfType<LayoutAnchorable>())
        {
            if (ids.Contains(panel.ContentId))
            {
                panel.Show();
                panel.IsSelected = true;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool swallowed = Swallowed(e.OriginalSource as DependencyObject, key);

        // The timeline in front first, for a number being typed or an edit point being trimmed;
        // then the keymap (editing: Delete, Ctrl+Z, Ctrl+K and the rest); then the preview's
        // transport keys, which are not in the keymap because JKL needs key-up as well.
        if (_model.Timelines?.ActiveTimeline is { } timeline && !swallowed && timeline.KeyDown(key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (_model.Keys is { } keys && !swallowed && keys.TryHandle(key, Keyboard.Modifiers, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        if (_model.Preview is { } preview && !swallowed
            && preview.KeyDown(key, Keyboard.Modifiers, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // K's release is noted but not swallowed: a text box that took the key down should see
        // it come up.
        _model.Preview?.KeyUp(e.Key == Key.System ? e.SystemKey : e.Key);

        base.OnPreviewKeyUp(e);
    }

    /// <summary>True when the focused element should keep the key for itself.</summary>
    private static bool Swallowed(DependencyObject? source, Key key)
    {
        for (DependencyObject? node = source; node is not null; node = ParentOf(node))
        {
            if (node is TextBoxBase or PasswordBox || node is ComboBox { IsEditable: true })
            {
                return true;
            }

            if (node is Selector or TreeView && key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End)
            {
                return true;
            }

            if (node is Menu or MenuItem)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
