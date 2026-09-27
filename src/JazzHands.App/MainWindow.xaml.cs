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
public partial class MainWindow : Window, Shell.IAppWindow, Shell.ITourHost
{
    private readonly MainViewModel _model;
    private bool _closing;
    private readonly System.Windows.Threading.DispatcherTimer? _statusTimer;

    /// <summary>Creates the window.</summary>
    /// <param name="model">The window's viewmodel, from the host.</param>
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        ThemeDockTabs();
        _model = model;
        DataContext = model;
        model.PanelsRequested += (_, ids) => BringForward(ids);

        // What the panels ask of the window: the empty timeline's way into the sample and the tour.
        CommandBindings.Add(new CommandBinding(Shell.ShellCommands.OpenSample, (_, _) => _ = model.OpenSampleCommand.ExecuteAsync(null)));
        CommandBindings.Add(new CommandBinding(Shell.ShellCommands.Tour, (_, _) => model.ShowTourCommand.Execute(null)));

        // Where it was left, moved back on screen if its monitor has gone.
        SourceInitialized += (_, _) => Restore();

        // The layout can be saved and restored only once the docking manager holds the panels.
        // Safe mode starts from the built-in layouts in a folder of its own, whatever was saved.
        string? layouts = Shell.SafeMode.Folder is { } safe ? System.IO.Path.Combine(safe, "layouts") : null;
        Loaded += (_, _) =>
        {
            FitProgramPane();
            model.AttachWorkspaces(new Shell.LayoutService(Dock, ContentFor, layouts, notify: model.Notify));
            Dock.SizeChanged += (_, e) => KeepProgramPaneShare(e);
        };

        // The status bar reads the playhead and the queue four times a second while the window is up.
        if (model.StatusBar is { } status)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => status.Refresh();
            Loaded += (_, _) => timer.Start();
            Closed += (_, _) => timer.Stop();
            _statusTimer = timer;
        }
    }

    /// <summary>
    /// Gives the preview about 55% of the middle column and the timeline the rest, for the window's
    /// size now, before the built-in layout is captured. A fixed height (AvalonDock needs the
    /// preview to have one) left the timeline a sliver in a 1366x768 window.
    /// </summary>
    private void FitProgramPane()
    {
        if (Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(pane => pane.Name == Shell.PanelPlacement.ProgramPane) is { } program
            && Dock.ActualHeight > 0)
        {
            program.DockHeight = new GridLength(Math.Max(program.DockMinHeight, Math.Round(Dock.ActualHeight * 0.55)));
        }
    }

    /// <summary>
    /// Keeps the preview's share of the column as the window changes height, so shrinking the
    /// window shrinks the preview and the timeline alike instead of the timeline alone, and always
    /// leaves the timeline room for a couple of tracks.
    /// </summary>
    private void KeepProgramPaneShare(SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || e.PreviousSize.Height <= 0
            || Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(pane => pane.Name == Shell.PanelPlacement.ProgramPane) is not { DockHeight.IsAbsolute: true } program)
        {
            return;
        }

        double share = program.DockHeight.Value / e.PreviousSize.Height;
        double height = Math.Clamp(Math.Round(e.NewSize.Height * share), program.DockMinHeight, Math.Max(program.DockMinHeight, e.NewSize.Height - 240));
        program.DockHeight = new GridLength(height);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Style, Style> DockTabs = new();
    private static Style? _dockTab;

    /// <summary>
    /// Gives AvalonDock's panel and document tabs the dark theme's look (<c>Dock.Tab</c>): its pane
    /// controls set their own tab style, which takes the light Windows template, and the theme's
    /// text on it was white on white. AvalonDock's styles live in its own theme, out of reach of a
    /// resource lookup, so each pane control is given ours as it loads: AvalonDock's tab setters
    /// (visibility, tooltip) kept, the template ours.
    /// </summary>
    private void ThemeDockTabs()
    {
        if (_dockTab is not null)
        {
            return;
        }

        _dockTab = (Style)Dock.FindResource("Dock.Tab");
        foreach (Type pane in new[] { typeof(AvalonDock.Controls.LayoutAnchorablePaneControl), typeof(AvalonDock.Controls.LayoutDocumentPaneControl) })
        {
            EventManager.RegisterClassHandler(pane, LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (sender is ItemsControl { ItemContainerStyle: { } own } control && _dockTab is { } tab && !Ours.TryGetValue(own, out _))
                {
                    control.ItemContainerStyle = DockTabs.GetValue(own, style => Themed(style, tab));
                }
            }));
        }
    }

    /// <summary>A tab style that is <paramref name="tab"/> with AvalonDock's own tab setters, all but its template.</summary>
    private static Style Themed(Style own, Style tab)
    {
        var item = new Style(typeof(TabItem), tab);
        foreach (Setter setter in own.Setters.OfType<Setter>().Where(setter => setter.Property != TemplateProperty))
        {
            item.Setters.Add(new Setter(setter.Property, setter.Value));
        }

        // Its triggers too: one of them hides the tab of a pane that holds a single panel.
        foreach (TriggerBase trigger in own.Triggers)
        {
            item.Triggers.Add(trigger);
        }

        item.Seal();
        Ours.Add(item, item);
        return item;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Style, Style> Ours = new();

    private void OnBellOpened(object sender, RoutedEventArgs e) => _model.StatusBar?.Notifications.MarkRead();

    /// <inheritdoc />
    public (Rect Target, Rect Window)? Highlight(string? target)
    {
        if (target is null || TourTarget(target) is not { } element)
        {
            TourOutline.Visibility = Visibility.Collapsed;
            return null;
        }

        const double Pad = 3;
        Rect bounds = element.TransformToVisual(TourLayer).TransformBounds(new Rect(element.RenderSize));
        Canvas.SetLeft(TourOutline, bounds.Left - Pad);
        Canvas.SetTop(TourOutline, bounds.Top - Pad);
        TourOutline.Width = bounds.Width + (Pad * 2);
        TourOutline.Height = bounds.Height + (Pad * 2);
        TourOutline.Visibility = Visibility.Visible;
        return (OnScreen(element), OnScreen(TourLayer));
    }

    /// <summary>What a tour card outlines: the menu, the timeline in front, or a panel brought forward.</summary>
    private FrameworkElement? TourTarget(string target)
    {
        if (target == "menu")
        {
            return MainMenu;
        }

        object? model = target == "timeline"
            ? _model.Timelines?.ActiveTimeline
            : _model.Panels.FirstOrDefault(panel => panel.ContentId == target);
        if (model is null)
        {
            return null;
        }

        if (target != "timeline")
        {
            BringForward([target]);
        }

        UpdateLayout();
        return Views(this).FirstOrDefault(view => ReferenceEquals(view.DataContext, model) && view.IsVisible);
    }

    private static IEnumerable<UserControl> Views(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is UserControl view)
            {
                yield return view;
            }

            foreach (UserControl deeper in Views(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>An element's area on the screen, in DIPs, as window positions are given.</summary>
    private static Rect OnScreen(FrameworkElement element)
    {
        Point corner = element.PointToScreen(new Point(0, 0));
        Matrix toDips = PresentationSource.FromVisual(element)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(toDips.Transform(corner), new Size(element.ActualWidth, element.ActualHeight));
    }

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
    public bool IsInFront => IsVisible && WindowState != WindowState.Minimized && IsActive;

    /// <inheritdoc />
    public void ShowInFront()
    {
        _statusTimer?.Start();
        BringToFront();
    }

    /// <inheritdoc />
    public void HideToTray()
    {
        _statusTimer?.Stop();
        Hide();
    }

    /// <inheritdoc />
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // With a lifetime the close button hides the window (or starts a quit that asks first);
        // the window only really closes as the application shuts down.
        if (_model.Lifetime is { } lifetime && !lifetime.IsQuitting)
        {
            e.Cancel = lifetime.CloseRequested();
            if (e.Cancel)
            {
                return;
            }
        }

        if (_closing || _model.Lifetime?.IsQuitting == true)
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
        // A list first: showing a hidden panel moves it out of the layout's hidden set, and
        // enumerating the layout while it changes threw (Window, a hidden panel crashed the editor).
        foreach (LayoutAnchorable panel in Dock.Layout.Descendents().OfType<LayoutAnchorable>().ToList())
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
