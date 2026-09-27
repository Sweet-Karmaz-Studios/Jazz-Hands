using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace JazzHands.App.Shell;

/// <summary>
/// Puts the notification area icon on screen and connects it: its tooltip follows
/// <see cref="DesktopStatus"/>, a click goes to <see cref="AppLifetime.TrayClicked"/>, and a right
/// click opens <see cref="TrayMenu"/> where the pointer is.
/// </summary>
/// <remarks>
/// The menu is an ordinary WPF context menu. With the main window hidden nothing of ours is in the
/// foreground, and a menu opened then would not close when the person clicks elsewhere; making the
/// menu's own window the foreground one, as Windows asks of notification area menus, fixes that.
/// </remarks>
public sealed partial class TrayHost : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly DesktopStatus _status;
    private readonly TrayMenu _menu;
    private readonly ContextMenu _context;

    /// <summary>Creates the icon from the .ico files beside the executable, and shows it.</summary>
    public TrayHost(DesktopStatus status, TrayMenu menu, AppLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(lifetime);
        _status = status;
        _menu = menu;

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        _icon = new TrayIcon(Path.Combine(assets, "tray-white.ico"), Path.Combine(assets, "tray-black.ico"), status.ToolTip);
        _icon.Clicked += (_, _) => lifetime.TrayClicked();
        _icon.MenuRequested += (_, point) => ShowMenu(point);
        _status.PropertyChanged += OnStatusChanged;

        _context = new ContextMenu
        {
            ItemContainerStyle = (Style)Application.Current.FindResource("MenuItem.FromViewModel"),
            Placement = PlacementMode.AbsolutePoint,
        };
        _context.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_context) is HwndSource source)
            {
                _ = SetForegroundWindow(source.Handle);
            }
        };

        // A run in an isolated home (the UI suite, the audit) puts no icon in the notification area:
        // Windows keeps a row in Settings, Taskbar for every program file that ever showed one.
        if (!JazzHands.Core.JazzFolders.IsIsolated)
        {
            _icon.Show();
        }
    }

    /// <summary>A notification from the icon, for when Windows notifications are off or not set up.</summary>
    public void Notify(string title, string text) => _icon.Balloon(title, text);

    /// <inheritdoc />
    public void Dispose()
    {
        _status.PropertyChanged -= OnStatusChanged;
        _context.IsOpen = false;
        _icon.Dispose();
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopStatus.ToolTip))
        {
            _icon.ToolTip = _status.ToolTip;
        }
    }

    private void ShowMenu(Point pixels)
    {
        // The anchor is in screen pixels; the menu is placed in device independent units.
        DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_context);
        _context.ItemsSource = _menu.Build();
        _context.HorizontalOffset = pixels.X / dpi.DpiScaleX;
        _context.VerticalOffset = pixels.Y / dpi.DpiScaleY;
        _context.IsOpen = true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
