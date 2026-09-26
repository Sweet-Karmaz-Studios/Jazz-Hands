using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace JazzHands.App.Shell;

/// <summary>The current user's registry, as the shell registration writes it; tests use a dictionary.</summary>
public interface IUserRegistry
{
    /// <summary>A value under HKEY_CURRENT_USER, or null. A null name is the key's default value.</summary>
    string? Get(string key, string? name);

    /// <summary>Writes a string value, making the key when it is not there.</summary>
    void Set(string key, string? name, string value);

    /// <summary>Removes a value, if it is there.</summary>
    void Delete(string key, string name);

    /// <summary>Removes a key and everything under it, if it is there.</summary>
    void DeleteTree(string key);
}

/// <summary>HKEY_CURRENT_USER itself.</summary>
public sealed class CurrentUserRegistry : IUserRegistry
{
    /// <inheritdoc />
    public string? Get(string key, string? name)
    {
        using RegistryKey? opened = Registry.CurrentUser.OpenSubKey(key);
        return opened?.GetValue(name) as string;
    }

    /// <inheritdoc />
    public void Set(string key, string? name, string value)
    {
        using RegistryKey created = Registry.CurrentUser.CreateSubKey(key);
        created.SetValue(name, value, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void Delete(string key, string name)
    {
        using RegistryKey? opened = Registry.CurrentUser.OpenSubKey(key, writable: true);
        opened?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <inheritdoc />
    public void DeleteTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
}

/// <summary>
/// What Jazz Hands tells Windows about itself, per user, so nothing needs an administrator: the
/// <c>.jazz</c> file type with its icon, the <c>jazzhands:</c> links notifications and the jump
/// list use, the identity Windows notifications carry, the two verbs on video files in Explorer,
/// and the entry that starts it with Windows.
/// </summary>
/// <remarks>
/// <para>
/// Written as the editor starts, and only what differs is written, so a moved install corrects
/// itself and an unchanged one touches nothing. Explorer is told when the file types changed.
/// </para>
/// <para>
/// The verbs are under <c>SystemFileAssociations</c>, which puts them on the classic menu (Show
/// more options on Windows 11). The first-level Windows 11 menu needs an <c>IExplorerCommand</c>
/// handler in a package with an identity; see Docs/SPIKES.md, S5.
/// </para>
/// </remarks>
public sealed partial class ShellRegistration
{
    /// <summary>The AppUserModelID: taskbar grouping, the jump list and notifications all key on it.</summary>
    public const string AppId = "SweetKarmazStudios.JazzHands";

    /// <summary>The <c>.jazz</c> file type's programmatic id.</summary>
    public const string ProgId = "JazzHands.Project";

    /// <summary>The value under Run that starts Jazz Hands with Windows.</summary>
    public const string RunValue = "Jazz Hands";

    /// <summary>The video files that get Quick Trim and Add to the open project in Explorer.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi", ".mxf", ".ts", ".mts"];

    private const string Classes = @"Software\Classes";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IUserRegistry _registry;

    /// <summary>A registration over a registry.</summary>
    public ShellRegistration(IUserRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Registers the editor at <paramref name="exe"/>, with its icons in <paramref name="assets"/>.
    /// </summary>
    /// <returns>True when anything was written.</returns>
    public bool Register(string exe, string assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentException.ThrowIfNullOrWhiteSpace(assets);

        string open = $"\"{exe}\" \"%1\"";
        string appIcon = Path.Combine(assets, "jazz.ico");
        var changed = false;

        void Write(string key, string? name, string value)
        {
            if (_registry.Get(key, name) != value)
            {
                _registry.Set(key, name, value);
                changed = true;
            }
        }

        // Notifications from an app that is not packaged carry this identity.
        Write($@"{Classes}\AppUserModelId\{AppId}", "DisplayName", "Jazz Hands");
        Write($@"{Classes}\AppUserModelId\{AppId}", "IconUri", appIcon);

        // jazzhands: links, from notification buttons and the jump list.
        Write($@"{Classes}\{LaunchRequest.Scheme}", null, "URL:Jazz Hands");
        Write($@"{Classes}\{LaunchRequest.Scheme}", "URL Protocol", string.Empty);
        Write($@"{Classes}\{LaunchRequest.Scheme}\DefaultIcon", null, $"\"{exe}\",0");
        Write($@"{Classes}\{LaunchRequest.Scheme}\shell\open\command", null, open);

        // .jazz files: their own icon, and a double click opens them.
        Write($@"{Classes}\.jazz", null, ProgId);
        Write($@"{Classes}\.jazz\OpenWithProgids", ProgId, string.Empty);
        Write($@"{Classes}\{ProgId}", null, "Jazz Hands project");
        Write($@"{Classes}\{ProgId}", "AppUserModelID", AppId);
        Write($@"{Classes}\{ProgId}\DefaultIcon", null, Path.Combine(assets, "jazz-document.ico"));
        Write($@"{Classes}\{ProgId}\shell\open\command", null, open);

        // Video files: Quick Trim with Jazz Hands, and Add to the open project.
        foreach (string extension in VideoExtensions)
        {
            string shell = $@"{Classes}\SystemFileAssociations\{extension}\shell";
            Write($@"{shell}\JazzHands.QuickTrim", null, "Quick Trim with Jazz Hands");
            Write($@"{shell}\JazzHands.QuickTrim", "Icon", $"\"{exe}\",0");
            Write($@"{shell}\JazzHands.QuickTrim\command", null, $"\"{exe}\" --quick-trim \"%1\"");
            Write($@"{shell}\JazzHands.AddMedia", null, "Add to the open project");
            Write($@"{shell}\JazzHands.AddMedia", "Icon", $"\"{exe}\",0");
            Write($@"{shell}\JazzHands.AddMedia\command", null, $"\"{exe}\" --add-media \"%1\"");
        }

        return changed;
    }

    /// <summary>Removes everything <see cref="Register"/> wrote, and the start with Windows entry.</summary>
    public void Unregister()
    {
        _registry.DeleteTree($@"{Classes}\AppUserModelId\{AppId}");
        _registry.DeleteTree($@"{Classes}\{LaunchRequest.Scheme}");
        _registry.DeleteTree($@"{Classes}\{ProgId}");
        _registry.Delete($@"{Classes}\.jazz\OpenWithProgids", ProgId);
        if (_registry.Get($@"{Classes}\.jazz", null) == ProgId)
        {
            _registry.DeleteTree($@"{Classes}\.jazz");
        }

        foreach (string extension in VideoExtensions)
        {
            _registry.DeleteTree($@"{Classes}\SystemFileAssociations\{extension}\shell\JazzHands.QuickTrim");
            _registry.DeleteTree($@"{Classes}\SystemFileAssociations\{extension}\shell\JazzHands.AddMedia");
        }

        _registry.Delete(Run, RunValue);
    }

    /// <summary>
    /// Removes the registration from this user's own registry and tells Explorer: what Settings'
    /// Remove from Windows, <c>app.unregister</c> and <c>JazzHands.exe --unregister</c> do.
    /// </summary>
    public static void UnregisterCurrentUser()
    {
        new ShellRegistration(new CurrentUserRegistry()).Unregister();
        NotifyExplorer();
    }

    /// <summary>True when Jazz Hands starts with Windows.</summary>
    public bool StartsWithWindows => _registry.Get(Run, RunValue) is not null;

    /// <summary>Starts Jazz Hands hidden in the notification area when Windows starts, or stops it.</summary>
    public void SetStartWithWindows(bool start, string exe)
    {
        if (start)
        {
            _registry.Set(Run, RunValue, $"\"{exe}\" --background");
        }
        else
        {
            _registry.Delete(Run, RunValue);
        }
    }

    /// <summary>Tells Explorer the file types changed, so icons and menus refresh.</summary>
    public static void NotifyExplorer() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Gives this process the AppUserModelID before any window exists, so the taskbar groups its
    /// windows, the jump list is its own and notifications come from it.
    /// </summary>
    public static void SetProcessAppId() => _ = SetCurrentProcessExplicitAppUserModelID(AppId);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
