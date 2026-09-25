using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Serilog;

namespace JazzHands.App.Shell;

/// <summary>
/// The icon in the notification area, the right-hand end of the taskbar: <c>Shell_NotifyIcon</c>
/// over a hidden window of its own, without WinForms.
/// </summary>
/// <remarks>
/// <para>
/// The icon follows the taskbar: the white mark on a dark taskbar, the black one on a light
/// taskbar, swapped when the theme changes (<c>WM_SETTINGCHANGE</c>, "ImmersiveColorSet"). It is
/// loaded at the notification area's size for the window's DPI. When Explorer restarts, the
/// taskbar forgets every icon and broadcasts <c>TaskbarCreated</c>; the icon adds itself again.
/// The window is a hidden top-level one rather than a message-only window, because message-only
/// windows do not receive those broadcasts.
/// </para>
/// <para>
/// A left click (or Enter on the icon) raises <see cref="Clicked"/>; a right click (or the menu
/// key) raises <see cref="MenuRequested"/> with where, in screen pixels. The icon is version 4,
/// so both arrive as <c>NIN_SELECT</c> and <c>WM_CONTEXTMENU</c> with the anchor point.
/// </para>
/// </remarks>
public sealed unsafe partial class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 0x31;
    private const int WmSettingChange = 0x001A;
    private const int WmContextMenu = 0x007B;
    private const int WmDpiChanged = 0x02E0;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint NiifInfo = 0x1;
    private const uint NiifNoSound = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    private readonly ILogger _log = Log.ForContext<TrayIcon>();
    private readonly string _whiteIcon;
    private readonly string _blackIcon;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _toolTip;
    private bool _added;
    private bool _disposed;

    /// <summary>Creates the icon's window. The icon shows when <see cref="Show"/> is called.</summary>
    /// <param name="whiteIcon">The .ico for a dark taskbar.</param>
    /// <param name="blackIcon">The .ico for a light taskbar.</param>
    /// <param name="toolTip">What hovering says.</param>
    public TrayIcon(string whiteIcon, string blackIcon, string toolTip)
    {
        _whiteIcon = whiteIcon;
        _blackIcon = blackIcon;
        _toolTip = toolTip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new HwndSource(new HwndSourceParameters("Jazz Hands notification area")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WindowProc);
    }

    /// <summary>A left click on the icon, or Enter with it focused.</summary>
    public event EventHandler? Clicked;

    /// <summary>A right click on the icon, or the menu key: where to put the menu, in screen pixels.</summary>
    public event EventHandler<Point>? MenuRequested;

    /// <summary>The icon's own hidden window, which a menu must bring to the front to close properly.</summary>
    public IntPtr Handle => _window.Handle;

    /// <summary>True when the taskbar is light and the black mark shows.</summary>
    public static bool LightTaskbar
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
    }

    /// <summary>What hovering says; the notification area keeps 127 characters.</summary>
    public string ToolTip
    {
        get => _toolTip;
        set
        {
            if (value == _toolTip)
            {
                return;
            }

            _toolTip = value;
            if (_added)
            {
                NotifyIconData data = Data(NifTip | NifShowTip);
                _ = ShellNotifyIcon(NimModify, ref data);
            }
        }
    }

    /// <summary>Adds the icon to the notification area.</summary>
    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LoadIcon();
        Add();
    }

    /// <summary>A notification from the icon: Windows shows it as a toast, and keeps it in the action centre.</summary>
    public void Balloon(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        NotifyIconData data = Data(NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = NiifInfo | NiifNoSound;
        _ = ShellNotifyIcon(NimModify, ref data);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            NotifyIconData data = Data(0);
            _ = ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _window.RemoveHook(WindowProc);
        _window.Dispose();
    }

    private void Add()
    {
        NotifyIconData data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = ShellNotifyIcon(NimAdd, ref data);
        if (!_added)
        {
            _log.Warning("The notification area refused the icon; it will be added when the taskbar restarts");
            return;
        }

        data.Version = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, ref data);
    }

    private void LoadIcon()
    {
        uint dpi = GetDpiForWindow(_window.Handle);
        int width = GetSystemMetricsForDpi(SmCxSmIcon, dpi == 0 ? 96 : dpi);
        int height = GetSystemMetricsForDpi(SmCySmIcon, dpi == 0 ? 96 : dpi);
        IntPtr loaded = LoadImage(IntPtr.Zero, LightTaskbar ? _blackIcon : _whiteIcon, 1, width, height, 0x10);
        if (loaded == IntPtr.Zero)
        {
            _log.Warning("The notification area icon could not be loaded from {Path}", LightTaskbar ? _blackIcon : _whiteIcon);
            return;
        }

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
        }

        _icon = loaded;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            int kind = (int)(lParam.ToInt64() & 0xFFFF);
            switch (kind)
            {
                case NinSelect or NinKeySelect:
                    Clicked?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;

                case WmContextMenu:
                    long anchor = wParam.ToInt64();
                    MenuRequested?.Invoke(this, new Point((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF)));
                    handled = true;
                    break;
            }
        }
        else if (message == (int)_taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: it has forgotten the icon.
            _added = false;
            LoadIcon();
            Add();
        }
        else if ((message == WmSettingChange && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet") || message == WmDpiChanged)
        {
            LoadIcon();
            if (_added)
            {
                NotifyIconData data = Data(NifIcon);
                _ = ShellNotifyIcon(NimModify, ref data);
            }
        }

        return IntPtr.Zero;
    }

    private NotifyIconData Data(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _window.Handle,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        Copy(_toolTip, data.Tip, 128);
        return data;
    }

    private static unsafe void Copy(string text, char* target, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        for (int index = 0; index < length; index++)
        {
            target[index] = text[index];
        }

        target[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
