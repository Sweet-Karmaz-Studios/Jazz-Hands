using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using JazzHands.App.ViewModels;

namespace JazzHands.App;

/// <summary>
/// The shell window: the menu, the docking manager with every panel and the timelines, the
/// status bar and the notifications. Workspaces and its place on screen come back as they were left.
/// </summary>
/// <remarks>
/// The playback keys work wherever the focus is, the way they do in every editor, except while
/// typing: a text box keeps its letters and its space bar. Inside a list the arrows, Home and End
/// move the selection as a list's keys should, and the rest still drive playback. The keymap
/// (Settings, Keymap) decides what each key does; the transport keys stay the preview's.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _closing;

    /// <summary>Creates the window.</summary>
    /// <param name="model">The window's viewmodel, from the host.</param>
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.PanelsRequested += (_, ids) => BringForward(ids);

        // Where it was left, moved back on screen if its monitor has gone.
        SourceInitialized += (_, _) => Restore();

        // The layout can be saved and restored only once the docking manager holds the panels.
        Loaded += (_, _) => model.AttachWorkspaces(new Shell.LayoutService(Dock, ContentFor, notify: model.Notify));

        // The status bar reads the playhead and the queue four times a second while the window is up.
        if (model.StatusBar is { } status)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => status.Refresh();
            Loaded += (_, _) => timer.Start();
            Closed += (_, _) => timer.Stop();
        }
    }

    private void OnBellOpened(object sender, RoutedEventArgs e) => _model.StatusBar?.Notifications.MarkRead();

    /// <summary>The panel or timeline a layout's content id names, or null for one there is not now.</summary>
    private object? ContentFor(string id) =>
        (object?)_model.Panels.FirstOrDefault(panel => panel.ContentId == id)
        ?? _model.Timelines?.Documents.FirstOrDefault(document => document.ContentId == id);

    private void Restore()
    {
        if (_model.WindowBounds is not { } bounds)
        {
            return;
        }

        Rect screens = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Rect fitted = Shell.ScreenFit.Fit(bounds, screens, SystemParameters.WorkArea);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = fitted.Left;
        Top = fitted.Top;
        Width = fitted.Width;
        Height = fitted.Height;
        if (_model.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Shows the window in front of everything, restored if it was minimised: a second launch's handoff.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();

        // Windows lets a window take the foreground only in certain cases; topmost for a moment
        // gets it in front of the launch that asked, which AllowSetForegroundWindow let through.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <inheritdoc />
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_closing)
        {
            _model.RememberWindow(WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds, WindowState == WindowState.Maximized);
            base.OnClosing(e);
            return;
        }

        // Unsaved work is asked about first. The question needs the dispatcher, so the close is
        // put off and made again once the answer is in.
        e.Cancel = true;
        if (await _model.ReadyToCloseAsync().ConfigureAwait(true))
        {
            _closing = true;
            Close();
        }
    }

    /// <summary>Reads the ticks of a menu as it opens: loop, the panels showing, the display.</summary>
    private void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: Shell.MenuItemViewModel item })
        {
            item.Refresh();
        }
    }

    /// <summary>Shows panels and puts each in front of its pane.</summary>
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
