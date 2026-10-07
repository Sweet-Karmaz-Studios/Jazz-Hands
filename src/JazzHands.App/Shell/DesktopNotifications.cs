using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using JazzHands.Core.Export;
using Serilog;

namespace JazzHands.App.Shell;

/// <summary>
/// Windows notifications: an export finished or failed, proxies ready, a recovery to look at, a
/// client attached. Through the WinRT toast API the Windows target already reaches.
/// </summary>
/// <remarks>
/// <para>
/// An app that is not packaged needs an AppUserModelID Windows knows (registered per user by
/// <see cref="ShellRegistration"/>) or its toasts are dropped. Buttons are <c>protocol</c>
/// activations: Play opens the file with whatever plays it, Show in folder and a click on the
/// notification are <c>jazzhands:</c> links, which start JazzHands.exe, which hands them to the
/// running editor. Nothing needs a COM activator.
/// </para>
/// <para>
/// One setting turns them all off. Do Not Disturb is Windows' own: a notification then goes
/// quietly to the notification centre. When Windows will not show one (an older build, the
/// identity not registered), the notification area icon's balloon says it instead.
/// </para>
/// </remarks>
public sealed class DesktopNotifications
{
    private readonly ILogger _log = Log.ForContext<DesktopNotifications>();
    private readonly Func<bool> _enabled;
    private readonly Action<string, string>? _fallback;

    /// <summary>Notifications while <paramref name="enabled"/> says so, and the balloon when toasts fail.</summary>
    public DesktopNotifications(Func<bool> enabled, Action<string, string>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
        _fallback = fallback;
    }

    /// <summary>Says an export finished, with Play and Show in folder; or that it failed, with Show details.</summary>
    public void ExportFinished(ExportJobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State == ExportJobState.Done)
        {
            Show(ExportDoneXml(job.OutputPath), "Export finished", Path.GetFileName(job.OutputPath));
        }
        else if (job.State == ExportJobState.Failed)
        {
            Show(ExportFailedXml(job.OutputPath, job.Error), "Export failed", $"{Path.GetFileName(job.OutputPath)}: {job.Error}");
        }
    }

    /// <summary>Says proxies are ready.</summary>
    public void ProxiesReady(int count) =>
        Show(Toast("jazzhands:show", "Proxies ready", count == 1 ? "A proxy is ready; playback uses it when proxies are on." : $"{count} proxies are ready; playback uses them when proxies are on."), "Proxies ready", $"{count} ready");

    /// <summary>Says a project has a recovery from a crash waiting.</summary>
    public void RecoveryAvailable(string project, string description) =>
        Show(Toast("jazzhands:show", "A recovery is waiting", $"{Path.GetFileName(project)}: {description}"), "A recovery is waiting", Path.GetFileName(project));

    /// <summary>Says a client attached: off by default (Settings, General).</summary>
    public void ClientAttached(string client) =>
        Show(Toast("jazzhands:show", client == "mcp" ? "Claude Code attached" : "A client attached", $"{client} is connected and can edit the project."), "A client attached", client);

    /// <summary>The finished export's notification: Play and Show in folder.</summary>
    public static string ExportDoneXml(string output) =>
        Toast(
            LaunchRequest.RevealLink(output),
            "Export finished",
            Path.GetFileName(output),
            ("Play", new Uri(output).AbsoluteUri),
            ("Show in folder", LaunchRequest.RevealLink(output)));

    /// <summary>The failed export's notification: Show details opens the queue.</summary>
    public static string ExportFailedXml(string output, string? error) =>
        Toast(
            "jazzhands:exports",
            "Export failed",
            $"{Path.GetFileName(output)}: {error ?? "see the export queue"}",
            ("Show details", "jazzhands:exports"));

    /// <summary>A toast with a title, a line and buttons, every activation a link.</summary>
    public static string Toast(string launch, string title, string text, params (string Label, string Link)[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        string actions = buttons.Length == 0
            ? string.Empty
            : "<actions>" + string.Concat(buttons.Select(button =>
                $"<action content=\"{SecurityElement.Escape(button.Label)}\" activationType=\"protocol\" arguments=\"{SecurityElement.Escape(button.Link)}\"/>")) + "</actions>";
        return $"<toast launch=\"{SecurityElement.Escape(launch)}\" activationType=\"protocol\">"
            + "<visual><binding template=\"ToastGeneric\">"
            + $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text>"
            + "</binding></visual>"
            + actions
            + "</toast>";
    }

    private void Show(string xml, string title, string text)
    {
        if (!_enabled())
        {
            return;
        }

        try
        {
            var document = new Windows.Data.Xml.Dom.XmlDocument();
            document.LoadXml(xml);
            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(ShellRegistration.AppId)
                .Show(new Windows.UI.Notifications.ToastNotification(document));
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Windows would not show the notification; the notification area says it instead");
            _fallback?.Invoke(title, text);
        }
    }
}
