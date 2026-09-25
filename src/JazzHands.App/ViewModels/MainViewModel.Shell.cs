using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Render.Color;
using WpfCommand = System.Windows.Input.ICommand;

namespace JazzHands.App.ViewModels;

/// <summary>
/// The window's shell: the menu, the project commands (new, open, save, recent) and the window's
/// own keymap actions.
/// </summary>
/// <remarks>
/// <para>
/// The menu is built here from the keymap, so an item that stands for a key does what the key
/// does (<see cref="KeymapService.Invoke"/>) and shows the key it is bound to now. Rebinding a key
/// in Settings rebuilds the menu. Transport items press the preview's own keys, which are not in
/// the keymap because they need a key-up (JKL). Every shortcut therefore has a menu item, and a
/// test walks the menu to prove it.
/// </para>
/// <para>
/// New, Open and Save are the engine's <c>project.new</c>, <c>project.open</c> and
/// <c>project.save</c>, asked first about unsaved changes. The recent list is kept in
/// <c>settings.json</c>.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The window's menu.</summary>
    public ObservableCollection<MenuItemViewModel> Menu { get; } = [];

    /// <summary>The projects opened lately, newest first.</summary>
    public IReadOnlyList<string> RecentProjects => _recent?.Current.Paths ?? [];

    /// <summary>Raised with a sentence for the status bar or a notification.</summary>
    public event EventHandler<string>? Said;

    /// <summary>Handles one of the window's keymap actions: <c>ui.new</c>, <c>ui.open</c> and the rest.</summary>
    public bool WindowAction(string action)
    {
        switch (action)
        {
            case "ui.new":
                _ = NewProjectAsync();
                return true;
            case "ui.open":
                _ = OpenProjectAsync();
                return true;
            case "ui.save":
                _ = SaveAsync();
                return true;
            case "ui.save-as":
                _ = SaveAsAsync();
                return true;
            case "ui.import":
                Media.ImportCommand.Execute(null);
                return true;
            case "ui.export":
                _ = ExportAsync();
                return true;
            case "ui.settings":
                _ = ShowSettingsAsync();
                return true;
            default:
                return false;
        }
    }

    /// <summary>File, New project: asks about unsaved changes, then what the project should be.</summary>
    [RelayCommand]
    public async Task NewProjectAsync()
    {
        if (_dialogs is null || !await ReadyToCloseAsync().ConfigureAwait(true))
        {
            return;
        }

        if (await _dialogs.ShowNewProjectAsync().ConfigureAwait(true) is not { } choice)
        {
            return;
        }

        CommandResult result = await _session.ExecuteAsync(new NewProjectCommand(choice.Name, choice.Fps, new FrameSize(choice.Width, choice.Height), Discard: true)).ConfigureAwait(true);
        Report(result, $"New project '{choice.Name}'. Save it to choose where it lives.");
    }

    /// <summary>File, Open: asks about unsaved changes, then for a file.</summary>
    [RelayCommand]
    public async Task OpenProjectAsync()
    {
        if (_files?.OpenProject() is not { } path)
        {
            return;
        }

        await OpenAsync(path).ConfigureAwait(true);
    }

    /// <summary>File, Open recent: a project from the list.</summary>
    [RelayCommand]
    public async Task OpenRecentAsync(string? path)
    {
        if (path is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            _recent?.Update(recent => recent.Without(path));
            BuildMenu();
            Say($"{Path.GetFileName(path)} is not there any more, so it is off the list.");
            return;
        }

        await OpenAsync(path).ConfigureAwait(true);
    }

    /// <summary>Opens a project by path, after asking about unsaved changes; for the menu, a second launch and a dropped file.</summary>
    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!await ReadyToCloseAsync().ConfigureAwait(true))
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        CommandResult result = await _session.ExecuteAsync(new OpenProjectCommand(full, Discard: true)).ConfigureAwait(true);
        if (result.Ok)
        {
            Remember(full);
        }

        Report(result, $"Opened {Path.GetFileName(full)}.");
        if (result.Ok)
        {
            await OfferRecoveryAsync().ConfigureAwait(true);
        }

        return result.Ok;
    }

    /// <summary>
    /// Offers the work a crash left beside the open project, or, with none open, the newest never
    /// saved project a crash rescued: bring it back, or keep what was saved and set it aside. At
    /// start and after every open (Phase 33).
    /// </summary>
    public async Task OfferRecoveryAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        RecoveryInfo info;
        try
        {
            info = _session.Query(new CheckRecoveryQuery());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (info.Available)
        {
            bool recover = await _dialogs.AskToRecoverAsync(info.Description).ConfigureAwait(true);
            CommandResult result = await _session.ExecuteAsync(recover ? new AcceptRecoveryCommand() : new DiscardRecoveryCommand()).ConfigureAwait(true);
            Report(result, recover
                ? "The unsaved work is back. Save to keep it."
                : "Kept the project as it was saved; the unsaved work is set aside in its .jazz.d\\recovered folder.");
            return;
        }

        if (_session.ProjectPath.Length == 0 && !_session.IsDirty && info.Untitled.FirstOrDefault() is { } rescued)
        {
            string when = rescued.SavedAt.ToLocalTime().ToString("HH:mm on d MMM", System.Globalization.CultureInfo.CurrentCulture);
            bool recover = await _dialogs.AskToRecoverAsync($"'{rescued.Name}' was never saved, and was rescued when Jazz Hands closed at {when}.").ConfigureAwait(true);
            CommandResult result = await _session.ExecuteAsync(recover ? new AcceptRecoveryCommand(rescued.Path) : new DiscardRecoveryCommand(rescued.Path)).ConfigureAwait(true);
            Report(result, recover ? $"'{rescued.Name}' is back. Save it to choose where it lives." : $"Removed the rescued copy of '{rescued.Name}'.");
        }
    }

    /// <summary>File, Save: where it came from, or asks where for a project never saved.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsync()
    {
        if (_session.ProjectPath.Length == 0)
        {
            return await SaveAsAsync().ConfigureAwait(true);
        }

        CommandResult result = await _session.ExecuteAsync(new SaveProjectCommand()).ConfigureAwait(true);
        if (result.Ok)
        {
            Remember(_session.ProjectPath);
        }

        Report(result, $"Saved {Path.GetFileName(_session.ProjectPath)}.");
        return result.Ok;
    }

    /// <summary>File, Save as.</summary>
    [RelayCommand]
    public async Task<bool> SaveAsAsync()
    {
        string suggested = _session.ProjectPath.Length > 0
            ? _session.ProjectPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{_session.Project.Name}.jazz");
        if (_files?.SaveProject(suggested) is not { } path)
        {
            return false;
        }

        CommandResult result = await _session.ExecuteAsync(new SaveProjectCommand(path)).ConfigureAwait(true);
        if (result.Ok)
        {
            Remember(path);
        }

        Report(result, $"Saved {Path.GetFileName(path)}.");
        return result.Ok;
    }

    /// <summary>File, Settings.</summary>
    [RelayCommand]
    public Task ShowSettingsAsync() => _dialogs?.ShowSettingsAsync() ?? Task.CompletedTask;

    /// <summary>Help, Keyboard shortcuts: Settings on the keymap page.</summary>
    [RelayCommand]
    public Task ShowShortcutsAsync() => _dialogs?.ShowSettingsAsync("keymap") ?? Task.CompletedTask;

    /// <summary>Window, a panel: shows it and puts it in front.</summary>
    [RelayCommand]
    public void ShowPanel(string? contentId)
    {
        if (contentId is not null)
        {
            PanelsRequested?.Invoke(this, [contentId]);
        }
    }

    /// <summary>
    /// True when the open project may go: nothing unsaved, or saved or discarded as the person chose.
    /// </summary>
    public async Task<bool> ReadyToCloseAsync()
    {
        if (!_session.IsDirty || _dialogs is null)
        {
            return true;
        }

        string name = _session.ProjectPath.Length > 0 ? Path.GetFileNameWithoutExtension(_session.ProjectPath) : _session.Project.Name;
        return await _dialogs.AskToSaveAsync(name).ConfigureAwait(true) switch
        {
            SaveChoice.Save => await SaveAsync().ConfigureAwait(true),
            SaveChoice.Discard => await ForgetUnsavedAsync().ConfigureAwait(true),
            _ => false,
        };
    }

    /// <summary>Unsaved changes deliberately thrown away are not offered again after a restart.</summary>
    private async Task<bool> ForgetUnsavedAsync()
    {
        await _session.ExecuteAsync(new DiscardRecoveryCommand()).ConfigureAwait(true);
        return true;
    }

    /// <summary>Builds the menu again: at start, and when the keymap or the recent list changes.</summary>
    public void BuildMenu()
    {
        Menu.Clear();
        Menu.Add(FileMenu());
        Menu.Add(EditMenu());
        Menu.Add(ClipMenu());
        Menu.Add(TimelineMenu());
        Menu.Add(PlaybackMenu());
        Menu.Add(WindowMenu());
        Menu.Add(new MenuItemViewModel("_Help").With(new MenuItemViewModel("_Keyboard shortcuts...", ShowShortcutsCommand)));

        // Anything bound in the keymap that the menus above do not show: a person's own bindings.
        MenuItemViewModel[] placed = [.. Menu.SelectMany(item => item.Descendants())];
        KeymapBinding[] missing = [.. (Keys?.Keymap.Bindings ?? []).Where(binding => !placed.Any(item => Stands(item, binding))).OrderBy(binding => binding.Keys, StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            MenuItemViewModel more = new("More _shortcuts");
            foreach (KeymapBinding binding in missing)
            {
                more.Items.Add(new MenuItemViewModel(Input.Keymap.Describe(binding.Command), new RelayCommand(() => Keys!.Invoke(binding)), binding.Keys) { KeymapCommand = binding.Command, KeymapArgs = binding.Args });
            }

            Menu[^1].Items.Add(MenuItemViewModel.Separator());
            Menu[^1].Items.Add(more);
        }
    }

    private void InitializeShell()
    {
        if (Keys is { } keys)
        {
            keys.WindowActions = WindowAction;
            keys.Changed += (_, _) => _ui.Post(BuildMenu);
        }

        BuildMenu();
    }

    private MenuItemViewModel FileMenu() =>
        new MenuItemViewModel("_File").With(
            Bound("_New project...", "ui.new", fallback: NewProjectCommand),
            Bound("_Open...", "ui.open", fallback: OpenProjectCommand),
            RecentMenu(),
            Bound("_Save", "ui.save", fallback: SaveCommand),
            Bound("Save _as...", "ui.save-as", fallback: SaveAsCommand),
            MenuItemViewModel.Separator(),
            Bound("_Import media...", "ui.import", fallback: Media.ImportCommand),
            new MenuItemViewModel("Quick _Trim a recording...", QuickTrimFileCommand),
            Bound("_Export...", "ui.export", fallback: ExportCommand),
            MenuItemViewModel.Separator(),
            new MenuItemViewModel("Find _missing media...", Media.FindMissingCommand, toolTip: "Find files that moved, by hash, and relink them"),
            new MenuItemViewModel("_Consolidate project...", ConsolidateCommand, toolTip: "Gather the project and every file it uses into one folder"),
            new MenuItemViewModel("Arc_hive project...", ArchiveCommand, toolTip: "Write the project and every file it uses into one zip"),
            MenuItemViewModel.Separator(),
            Bound("Se_ttings...", "ui.settings", fallback: ShowSettingsCommand),
            MenuItemViewModel.Separator(),
            new MenuItemViewModel("E_xit", ExitCommand));

    [RelayCommand]
    private Task ConsolidateAsync() => _dialogs?.ShowConsolidateAsync(archive: false) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ArchiveAsync() => _dialogs?.ShowConsolidateAsync(archive: true) ?? Task.CompletedTask;

    private MenuItemViewModel RecentMenu()
    {
        MenuItemViewModel recent = new("Open _recent");
        foreach (string path in RecentProjects)
        {
            recent.Items.Add(new MenuItemViewModel(path.Replace("_", "__", StringComparison.Ordinal), new RelayCommand(() => _ = OpenRecentAsync(path)), toolTip: path));
        }

        if (recent.Items.Count == 0)
        {
            recent.Items.Add(new MenuItemViewModel("Nothing yet"));
        }

        return recent;
    }

    private MenuItemViewModel EditMenu() =>
        new MenuItemViewModel("_Edit").With(
            Bound("_Undo", "undo"),
            Bound("_Redo", "redo"),
            MenuItemViewModel.Separator(),
            Bound("Cu_t", "ui.cut"),
            Bound("_Copy", "ui.copy"),
            Bound("_Paste", "ui.paste"),
            Bound("Paste _insert", "ui.paste-insert"),
            MenuItemViewModel.Separator(),
            Bound("Select _all", "selection.set"),
            Bound("_Delete", "clip.remove"),
            Bound("Ripple dele_te", "clip.ripple-delete"),
            Bound("D_uplicate", "clip.duplicate"));

    private MenuItemViewModel ClipMenu() =>
        new MenuItemViewModel("_Clip").With(
            Bound("_Split at the playhead", "clip.split"),
            Bound("Ripple trim start to the playhead", "clip.ripple-trim", Args(("edge", "start"))),
            Bound("Ripple trim end to the playhead", "clip.ripple-trim", Args(("edge", "end"))),
            MenuItemViewModel.Separator(),
            Bound("Nudge a frame earlier", "clip.nudge", Args(("frames", -1))),
            Bound("Nudge a frame later", "clip.nudge", Args(("frames", 1))),
            Bound("Nudge ten frames earlier", "clip.nudge", Args(("frames", -10))),
            Bound("Nudge ten frames later", "clip.nudge", Args(("frames", 10))),
            MenuItemViewModel.Separator(),
            Bound("Add the default _transition", "transition.apply-default", Args(("kind", "video"))),
            Bound("Add the default sound crossfade", "transition.apply-default", Args(("kind", "audio"))),
            Bound("Add both", "transition.apply-default", Args(("kind", "both"))),
            MenuItemViewModel.Separator(),
            Bound("_Match frame", "ui.match-frame"));

    private MenuItemViewModel TimelineMenu()
    {
        MenuItemViewModel menu = new("_Timeline");
        foreach ((_, string action, _, string label) in ViewModels.Timeline.TimelineTools.All)
        {
            menu.Items.Add(Bound($"{label} tool", action));
        }

        menu.Items.Add(MenuItemViewModel.Separator());
        menu.Items.Add(Bound("_Snapping", "ui.snap"));
        menu.Items.Add(Bound("Add a _marker at the playhead", "marker.add"));
        menu.Items.Add(MenuItemViewModel.Separator());
        menu.Items.Add(Bound("_Keep in to out (Quick Trim)", "trim.add-segment"));
        menu.Items.Add(Bound("_Cut in to out (Quick Trim)", "trim.remove-range"));
        return menu;
    }

    private MenuItemViewModel PlaybackMenu()
    {
        MenuItemViewModel menu = new("_Playback");
        if (Preview is not { } preview)
        {
            menu.Items.Add(new MenuItemViewModel("No preview in this window"));
            return menu;
        }

        menu.With(
            Press("_Play or pause", Key.Space),
            Press("Shuttle _backwards", Key.J),
            Press("Stop shuttling", Key.K),
            Press("Shuttle _forwards", Key.L),
            new MenuItemViewModel("_Stop", preview.StopCommand),
            MenuItemViewModel.Separator(),
            Press("Back one frame", Key.Left),
            Press("Forward one frame", Key.Right),
            Press("Back ten frames", Key.Left, ModifierKeys.Shift),
            Press("Forward ten frames", Key.Right, ModifierKeys.Shift),
            Press("Previous edit", Key.Up),
            Press("Next edit", Key.Down),
            Press("Go to st_art", Key.Home),
            Press("Go to _end", Key.End),
            MenuItemViewModel.Separator(),
            Bound("Mark _in", "playback.set-in"),
            Bound("Mark _out", "playback.set-out"),
            Press("Go to the in point", Key.I, ModifierKeys.Shift),
            Press("Go to the out point", Key.O, ModifierKeys.Shift),
            Press("_Clear in and out", Key.X, ModifierKeys.Control | ModifierKeys.Shift),
            new MenuItemViewModel("_Loop", new RelayCommand(() => preview.Loop = !preview.Loop), "Ctrl+L", () => preview.Loop),
            MenuItemViewModel.Separator(),
            new MenuItemViewModel("F_ull screen", preview.ToggleFullScreenCommand, "F11"));
        return menu;
    }

    private MenuItemViewModel WindowMenu()
    {
        MenuItemViewModel menu = new("_Window");
        menu.Items.Add(WorkspaceMenu());
        menu.Items.Add(MenuItemViewModel.Separator());
        foreach (ToolViewModel panel in Panels.OrderBy(panel => panel.Title, StringComparer.CurrentCulture))
        {
            menu.Items.Add(new MenuItemViewModel(panel.Title, ShowPanelCommand, isChecked: () => panel.IsVisible) { CommandParameter = panel.ContentId });
        }

        if (Preview is { } preview)
        {
            menu.Items.Add(MenuItemViewModel.Separator());
            menu.Items.Add(new MenuItemViewModel("Preview _display").With(
                new MenuItemViewModel("_sRGB monitor", preview.SetDisplayCommand, isChecked: () => preview.Display == DisplayTransfer.Srgb) { CommandParameter = DisplayTransfer.Srgb },
                new MenuItemViewModel("Gamma _2.2 monitor", preview.SetDisplayCommand, isChecked: () => preview.Display == DisplayTransfer.Gamma22) { CommandParameter = DisplayTransfer.Gamma22 },
                new MenuItemViewModel("BT.1886 _reference display", preview.SetDisplayCommand, isChecked: () => preview.Display == DisplayTransfer.Bt1886) { CommandParameter = DisplayTransfer.Bt1886 }));
        }

        return menu;
    }

    private MenuItemViewModel WorkspaceMenu()
    {
        MenuItemViewModel menu = new("_Workspace");
        if (Workspaces is not { } workspaces)
        {
            return menu.With(
                new MenuItemViewModel("_Colour", ShowColorWorkspaceCommand),
                new MenuItemViewModel("_Audio", ShowAudioWorkspaceCommand));
        }

        foreach (string name in workspaces.Names)
        {
            menu.Items.Add(new MenuItemViewModel(name, SwitchWorkspaceCommand, isChecked: () => string.Equals(workspaces.Current, name, StringComparison.OrdinalIgnoreCase)) { CommandParameter = name });
        }

        return menu.With(
            MenuItemViewModel.Separator(),
            new MenuItemViewModel("_Save workspace as...", SaveWorkspaceAsCommand),
            new MenuItemViewModel("_Reset this workspace", ResetWorkspaceCommand));
    }

    /// <summary>
    /// An item for a keymap binding: what the key does, with the keys shown beside it; the
    /// fallback runs when nothing is bound to the command.
    /// </summary>
    private MenuItemViewModel Bound(string header, string command, JsonObject? args = null, WpfCommand? fallback = null)
    {
        KeymapBinding[] bindings = [.. (Keys?.Keymap.Bindings ?? []).Where(binding => binding.Command == command && (args is null || Contains(binding.Args, args))).OrderBy(binding => binding.Keys, StringComparer.Ordinal)];
        WpfCommand? run = bindings.Length > 0 ? new RelayCommand(() => Keys!.Invoke(bindings[0])) : fallback;
        string keys = string.Join(", ", bindings.Select(binding => binding.Keys));
        return new MenuItemViewModel(header, run, keys, toolTip: keys.Length > 0 ? $"{Input.Keymap.Describe(command)} ({keys})" : null)
        {
            KeymapCommand = command,
            KeymapArgs = args,
        };
    }

    /// <summary>An item that presses one of the preview's own keys, which need a key-up and so are not in the keymap.</summary>
    private MenuItemViewModel Press(string header, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        // KeyGesture will not describe a letter without a modifier, so the text is made here.
        string gesture = string.Concat(
            modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : string.Empty,
            modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : string.Empty,
            modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : string.Empty,
            key.ToString());
        return new MenuItemViewModel(header, new RelayCommand(() =>
        {
            Preview?.KeyDown(key, modifiers, isRepeat: false);
            Preview?.KeyUp(key);
        }), gesture);
    }

    private static bool Stands(MenuItemViewModel item, KeymapBinding binding) =>
        item.KeymapCommand == binding.Command && (item.KeymapArgs is null || Contains(binding.Args, item.KeymapArgs));

    private static bool Contains(JsonObject all, JsonObject some) =>
        some.All(pair => all.TryGetPropertyValue(pair.Key, out JsonNode? value) && JsonNode.DeepEquals(value, pair.Value));

    private static JsonObject Args(params (string Name, JsonNode Value)[] pairs)
    {
        var args = new JsonObject();
        foreach ((string name, JsonNode value) in pairs)
        {
            args[name] = value;
        }

        return args;
    }

    private void Remember(string path)
    {
        if (_recent is null)
        {
            return;
        }

        _recent.Update(recent => recent.With(path));
        OnPropertyChanged(nameof(RecentProjects));
        BuildMenu();
    }

    private void Report(CommandResult result, string done) => Say(result.Ok ? done : result.Error ?? result.Code ?? "That did not work.");
}
