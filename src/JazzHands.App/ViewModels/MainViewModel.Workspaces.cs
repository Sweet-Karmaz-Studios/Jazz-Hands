using System.Windows;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;

namespace JazzHands.App.ViewModels;

/// <summary>
/// The window's workspaces and its place on the screen, both kept in the <c>editor</c> section of
/// <c>settings.json</c> so the editor opens as it was left.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The workspaces, once the window has made its layout service.</summary>
    public IWorkspaces? Workspaces { get; private set; }

    /// <summary>Where the window was, when it has been anywhere before.</summary>
    public Rect? WindowBounds => _editor?.Current.WindowBounds?.ToRect();

    /// <summary>Whether the window was maximised.</summary>
    public bool WindowMaximized => _editor?.Current.WindowMaximized ?? false;

    /// <summary>
    /// Takes the window's workspaces and opens the one it was left in. Called once the docking
    /// manager holds every panel.
    /// </summary>
    public void AttachWorkspaces(IWorkspaces workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        Workspaces = workspaces;
        workspaces.Open(_editor?.Current.Workspace ?? WorkspaceDefinition.BuiltIn[0].Name);
        workspaces.Changed += (_, _) =>
        {
            _editor?.Update(editor => editor with { Workspace = workspaces.Current });
            BuildMenu();
        };
        BuildMenu();
    }

    /// <summary>Keeps the window's place and the workspace's layout; called as the window closes.</summary>
    public void RememberWindow(Rect bounds, bool maximized)
    {
        Workspaces?.SaveCurrent();
        _editor?.Update(editor => editor with
        {
            WindowBounds = bounds.IsEmpty ? editor.WindowBounds : new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            WindowMaximized = maximized,
            Workspace = Workspaces?.Current ?? editor.Workspace,
        });
    }

    /// <summary>Says something where the person will see it: the status line and a notification.</summary>
    public void Notify(string message) => Say(message);

    /// <summary>Window, Workspace, a workspace.</summary>
    [RelayCommand]
    private void SwitchWorkspace(string? name)
    {
        if (name is not null && Workspaces is { } workspaces && !string.Equals(workspaces.Current, name, StringComparison.OrdinalIgnoreCase))
        {
            workspaces.Switch(name);
        }
    }

    /// <summary>Window, Workspace, Save workspace as: the layout as it is now, under a new name.</summary>
    [RelayCommand]
    private async Task SaveWorkspaceAsAsync()
    {
        if (Workspaces is not { } workspaces || _dialogs is null)
        {
            return;
        }

        if (await _dialogs.AskForTextAsync("Save workspace", "Keep the panels as they are now, under the name:", $"{workspaces.Current} 2").ConfigureAwait(true) is not { Length: > 0 } name)
        {
            return;
        }

        try
        {
            workspaces.SaveAs(name);
            Say($"Saved the workspace '{name}'. It is in Window, Workspace.");
        }
        catch (ArgumentException error)
        {
            Say(error.Message);
        }
    }

    /// <summary>Window, Workspace, Reset this workspace.</summary>
    [RelayCommand]
    private void ResetWorkspace()
    {
        if (Workspaces is { } workspaces)
        {
            workspaces.Reset();
            Say($"The {workspaces.Current} workspace is back as it ships.");
        }
    }
}
