using System.IO;
using System.Text;
using System.Windows;
using AvalonDock;
using AvalonDock.Core;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Serilog;

namespace JazzHands.App.Shell;

/// <summary>
/// Saves and restores the docking layout per workspace, and switches between workspaces.
/// </summary>
/// <remarks>
/// <para>
/// A workspace's layout is AvalonDock's XML in
/// <c>%LOCALAPPDATA%\JazzHands\layouts\&lt;name&gt;.xml</c>, after a first line naming the
/// version of the format. The layout that is current is saved when another is chosen and when the
/// editor closes, so it comes back as it was left. A file from another version, or one that will
/// not read, is set aside for the workspace's default and the person is told, never a crash.
/// </para>
/// <para>
/// A built-in workspace's default is the window's own layout as it first opened, with only the
/// workspace's panels showing and its chosen ones in front. A person's own workspace is whatever
/// they saved. Panels a layout does not mention still come back, from the window's list, and
/// panels it mentions that no longer exist are left out.
/// </para>
/// <para>
/// A layout saved with a floating panel on a monitor since unplugged would open it off screen:
/// every floating panel is moved back onto the desktop as a layout loads.
/// </para>
/// </remarks>
public sealed class LayoutService : IWorkspaces
{
    /// <summary>The first line of a layout file this build writes and reads.</summary>
    public const string Header = "<!-- jazz-layout 1 -->";

    private readonly ILogger _log = Log.ForContext<LayoutService>();
    private readonly DockingManager _manager;
    private readonly Func<string, object?> _content;
    private readonly string _folder;
    private readonly Action<string>? _notify;
    private readonly Func<Rect> _screens;
    private readonly Func<Rect> _workArea;
    private readonly string _pristine;

    /// <summary>A service over the window's docking manager, which already holds its panels.</summary>
    /// <param name="manager">The docking manager.</param>
    /// <param name="content">The panel or document for a content id, or null when there is none now.</param>
    /// <param name="folder">Where layouts are kept, or null for the per-user folder.</param>
    /// <param name="notify">Told when a layout could not be used.</param>
    /// <param name="screens">The desktop, every monitor together; the system's when null.</param>
    /// <param name="workArea">The primary monitor's work area; the system's when null.</param>
    public LayoutService(DockingManager manager, Func<string, object?> content, string? folder = null, Action<string>? notify = null, Func<Rect>? screens = null, Func<Rect>? workArea = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(content);

        _manager = manager;
        _content = content;
        _folder = folder ?? DefaultFolder;
        _notify = notify;
        _screens = screens ?? (() => new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        _workArea = workArea ?? (() => SystemParameters.WorkArea);
        _pristine = Serialize();
        Current = WorkspaceDefinition.BuiltIn[0].Name;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>The per-user folder of layouts.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        JazzHands.Core.JazzFolders.Local, "layouts");

    /// <inheritdoc />
    public string Current { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> Names =>
        [
            .. WorkspaceDefinition.BuiltIn.Select(workspace => workspace.Name),
            .. (Directory.Exists(_folder) ? Directory.EnumerateFiles(_folder, "*.xml") : [])
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => WorkspaceDefinition.Find(name) is null)
                .Order(StringComparer.CurrentCultureIgnoreCase),
        ];

    /// <summary>Shows a workspace without saving the one before: at start.</summary>
    public void Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Current = Names.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) ?? WorkspaceDefinition.BuiltIn[0].Name;
        Load(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Switch(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SaveCurrent();
        Open(name);
    }

    /// <inheritdoc />
    public void SaveAs(string name)
    {
        string clean = name.Trim();
        if (clean.Length == 0 || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' cannot be a workspace's name: it has to be a file name.", nameof(name));
        }

        Current = clean;
        SaveCurrent();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Reset()
    {
        string path = PathOf(Current);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        ApplyDefault(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SaveCurrent()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string path = PathOf(Current);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Header + Environment.NewLine + Serialize(), Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "The {Workspace} layout could not be saved", Current);
        }
    }

    /// <summary>Moves every floating panel back onto the desktop.</summary>
    public void FitFloating()
    {
        Rect screens = _screens();
        Rect work = _workArea();
        foreach (LayoutContent content in _manager.Layout.Descendents().OfType<LayoutContent>().Where(content => content.IsFloating))
        {
            Rect was = new(content.FloatingLeft, content.FloatingTop, content.FloatingWidth, content.FloatingHeight);
            Rect now = ScreenFit.Fit(was, screens, work);
            if (now != was)
            {
                content.FloatingLeft = now.Left;
                content.FloatingTop = now.Top;
                content.FloatingWidth = now.Width;
                content.FloatingHeight = now.Height;
            }
        }
    }

    private void Load(string name)
    {
        string path = PathOf(name);
        if (!File.Exists(path))
        {
            ApplyDefault(name);
            return;
        }

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (!text.StartsWith(Header, StringComparison.Ordinal))
            {
                _notify?.Invoke($"The saved {name} layout is from another version of Jazz Hands; it is back to its default.");
                ApplyDefault(name);
                return;
            }

            Deserialize(text[Header.Length..].TrimStart());
            FitFloating();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException or FormatException)
        {
            _log.Warning(error, "The {Workspace} layout at {Path} could not be read", name, path);
            _notify?.Invoke($"The saved {name} layout could not be read; it is back to its default.");
            ApplyDefault(name);
        }
    }

    private void ApplyDefault(string name)
    {
        Deserialize(_pristine);
        WorkspaceDefinition definition = WorkspaceDefinition.Find(name) ?? WorkspaceDefinition.BuiltIn[0];
        foreach (LayoutAnchorable panel in _manager.Layout.Descendents().OfType<LayoutAnchorable>().ToList())
        {
            if (definition.Shown.Contains(panel.ContentId))
            {
                if (panel.IsHidden)
                {
                    panel.Show();
                }
            }
            else if (!panel.IsHidden)
            {
                panel.Hide();
            }
        }

        foreach (LayoutAnchorable front in _manager.Layout.Descendents().OfType<LayoutAnchorable>().Where(panel => definition.Front.Contains(panel.ContentId)).ToList())
        {
            front.IsSelected = true;
        }
    }

    private string Serialize()
    {
        var serializer = new XmlLayoutSerializer(_manager);
        using var writer = new StringWriter();
        serializer.Serialize(writer);
        return writer.ToString();
    }

    private void Deserialize(string xml)
    {
        var serializer = new XmlLayoutSerializer(_manager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is { Length: > 0 } id && _content(id) is { } content)
            {
                e.Content = content;
            }
        };

        using var reader = new StringReader(xml);
        serializer.Deserialize(reader);
    }

    private string PathOf(string name) => Path.Combine(_folder, $"{name}.xml");
}
