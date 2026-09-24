using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Selection;
using Serilog;

namespace JazzHands.App.ViewModels.Effects;

/// <summary>One effect type in the browser.</summary>
public sealed partial class EffectTypeItemViewModel : ObservableObject
{
    private readonly EffectsPanelViewModel _panel;
    private readonly bool _ready;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Creates an item.</summary>
    public EffectTypeItemViewModel(EffectsPanelViewModel panel, EffectDescriptor descriptor, bool favorite)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(descriptor);

        _panel = panel;
        Descriptor = descriptor;
        IsFavorite = favorite;
        _ready = true;
    }

    /// <summary>What it is.</summary>
    public EffectDescriptor Descriptor { get; }

    /// <summary>Its type id, which a drag carries.</summary>
    public string TypeId => Descriptor.TypeId;

    /// <summary>What it is called.</summary>
    public string Name => Descriptor.Name;

    /// <summary>What it does, for the tooltip.</summary>
    public string Description => Descriptor.Description.Length > 0 ? $"{Descriptor.Description}\n{Descriptor.TypeId}" : Descriptor.TypeId;

    /// <summary>True for a sound effect.</summary>
    public bool IsAudio => Descriptor.Kind == EffectKind.Audio;

    partial void OnIsFavoriteChanged(bool value)
    {
        if (_ready)
        {
            _panel.SetFavorite(this, value);
        }
    }

    [RelayCommand]
    private Task Apply() => _panel.ApplyAsync(TypeId);
}

/// <summary>A folder of the browser.</summary>
/// <param name="title">Its heading.</param>
public sealed partial class EffectCategoryViewModel(string title) : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>The heading.</summary>
    public string Title { get; } = title;

    /// <summary>The types in it, by name.</summary>
    public ObservableCollection<EffectTypeItemViewModel> Items { get; } = [];
}

/// <summary>One preset saved in the project.</summary>
public sealed partial class EffectPresetItemViewModel(EffectsPanelViewModel panel, EffectPreset preset) : ObservableObject
{
    /// <summary>Its id, which a drag carries.</summary>
    public string Id { get; } = preset.Id;

    /// <summary>Its name.</summary>
    public string Name { get; } = preset.Name;

    /// <summary>The effects in it, as names.</summary>
    public string Summary { get; } = string.Join(", ", preset.Effects.Select(effect => EffectCatalog.Registry.Find(effect.TypeId)?.Name ?? effect.TypeId));

    [RelayCommand]
    private Task Apply() => panel.ApplyPresetAsync(Id);

    [RelayCommand]
    private Task Remove() => panel.RemovePresetAsync(Id);
}

/// <summary>
/// The effects browser: every picture and sound effect by folder, a search, favorites, and the
/// project's presets.
/// </summary>
/// <remarks>
/// Applying an effect adds it to every selected clip it suits, in one undo step; dragging one
/// onto a clip, a track or the inspector does the same for that one. Presets save the first
/// selected clip's chain under a name and apply it anywhere. All of it goes through the same
/// <c>effect.*</c> commands the CLI has; only the favorites are the editor's own, a preference of
/// the person rather than a part of the project.
/// </remarks>
public sealed partial class EffectsPanelViewModel : ToolViewModel
{
    /// <summary>The docking content id.</summary>
    public const string PanelId = "effects";

    private readonly ILogger _log = Log.ForContext<EffectsPanelViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IEffectFavorites _favorites;
    private readonly EffectRegistry _registry;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _presetName = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the panel.</summary>
    public EffectsPanelViewModel(ISession session, SelectionService selection, IUiDispatcher ui, IEffectFavorites favorites, EffectRegistry? registry = null)
        : base(PanelId, "Effects")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(favorites);

        _session = session;
        _selection = selection;
        _ui = ui;
        _favorites = favorites;
        _registry = registry ?? EffectCatalog.Registry;

        _session.ProjectChanged += (_, _) => _ui.Post(LoadPresets);
        Refilter();
        LoadPresets();
    }

    /// <summary>The folders, favorites first, then picture effects, then sound.</summary>
    public ObservableCollection<EffectCategoryViewModel> Categories { get; } = [];

    /// <summary>The project's presets.</summary>
    public ObservableCollection<EffectPresetItemViewModel> Presets { get; } = [];

    /// <summary>
    /// Adds an effect to every selected clip it suits, as one undo step.
    /// </summary>
    public Task ApplyAsync(string typeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);

        if (_registry.Find(typeId) is not { } descriptor)
        {
            Status = $"There is no effect called '{typeId}'.";
            return Task.CompletedTask;
        }

        Project project = _session.Project;
        string[] suited =
        [
            .. _selection.Ids.Where(id => project.FindClip(id) is { } found && Suits(descriptor, found.Track.Kind)),
        ];

        if (suited.Length == 0)
        {
            Status = _selection.Ids.IsEmpty
                ? $"Select a clip for {descriptor.Name}, or drag it onto one."
                : $"{descriptor.Name} works on {(descriptor.Kind == EffectKind.Audio ? "sound" : "pictures")}, and nothing selected is one.";
            return Task.CompletedTask;
        }

        ICommand[] adds = [.. suited.Select(id => (ICommand)new AddEffectCommand(id, typeId))];
        return RunAsync(adds.Length == 1 ? adds[0] : new BatchCommand([.. adds], $"Add {descriptor.Name}"));
    }

    /// <summary>Applies a preset to every selected clip, as one undo step.</summary>
    public Task ApplyPresetAsync(string presetId)
    {
        Project project = _session.Project;
        string[] clips = [.. _selection.Ids.Where(id => project.FindClip(id) is not null)];
        if (clips.Length == 0)
        {
            Status = "Select a clip to apply the preset to, or drag it onto one.";
            return Task.CompletedTask;
        }

        ICommand[] applies = [.. clips.Select(id => (ICommand)new ApplyEffectPresetCommand(id, presetId))];
        return RunAsync(applies.Length == 1 ? applies[0] : new BatchCommand([.. applies], "Apply preset"));
    }

    /// <summary>Deletes a preset from the project.</summary>
    public Task RemovePresetAsync(string presetId) => RunAsync(new RemoveEffectPresetCommand(presetId));

    /// <summary>Stars or unstars a type.</summary>
    internal void SetFavorite(EffectTypeItemViewModel item, bool favorite)
    {
        _favorites.Set(item.TypeId, favorite);
        Refilter();
    }

    /// <summary>True when an effect of this kind can go on a track of that kind.</summary>
    internal static bool Suits(EffectDescriptor descriptor, TrackKind track) =>
        descriptor.Kind == EffectKind.Audio ? track == TrackKind.Audio : track is TrackKind.Video or TrackKind.Adjustment;

    partial void OnSearchChanged(string value) => Refilter();

    /// <summary>Saves the first selected clip's effects as a preset.</summary>
    [RelayCommand]
    private Task SavePreset()
    {
        Project project = _session.Project;
        string? clipId = _selection.Ids.FirstOrDefault(id => project.FindClip(id) is not null);
        if (clipId is null)
        {
            Status = "Select a clip whose effects you want to keep as a preset.";
            return Task.CompletedTask;
        }

        string name = PresetName.Trim();
        if (name.Length == 0)
        {
            Status = "Give the preset a name first.";
            return Task.CompletedTask;
        }

        PresetName = string.Empty;
        return RunAsync(new SaveEffectPresetCommand(EquatableArray.Create(clipId), name));
    }

    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

    private void Refilter()
    {
        string search = Search.Trim();
        IEnumerable<EffectDescriptor> matching = _registry.All
            .Where(descriptor => descriptor.Kind is EffectKind.Video or EffectKind.Audio)
            .Where(descriptor => search.Length == 0
                || descriptor.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || descriptor.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || descriptor.TypeId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || descriptor.Description.Contains(search, StringComparison.OrdinalIgnoreCase));

        var favorites = new EffectCategoryViewModel("Favorites");
        var folders = new SortedDictionary<string, EffectCategoryViewModel>(StringComparer.Ordinal);

        foreach (EffectDescriptor descriptor in matching)
        {
            bool favorite = _favorites.Ids.Contains(descriptor.TypeId);
            if (favorite)
            {
                favorites.Items.Add(new EffectTypeItemViewModel(this, descriptor, favorite: true));
            }

            // Picture folders first, then sound: the sort key carries the kind, the title does not repeat it for pictures.
            string key = $"{(int)descriptor.Kind}:{descriptor.Category}";
            if (!folders.TryGetValue(key, out EffectCategoryViewModel? folder))
            {
                folder = new EffectCategoryViewModel(descriptor.Kind == EffectKind.Audio ? $"Audio: {descriptor.Category}" : descriptor.Category);
                folders[key] = folder;
            }

            folder.Items.Add(new EffectTypeItemViewModel(this, descriptor, favorite));
        }

        Categories.Clear();
        if (favorites.Items.Count > 0)
        {
            Categories.Add(favorites);
        }

        foreach (EffectCategoryViewModel folder in folders.Values)
        {
            Categories.Add(folder);
        }
    }

    private void LoadPresets()
    {
        Presets.Clear();
        foreach (EffectPreset preset in _session.Project.EffectPresets)
        {
            Presets.Add(new EffectPresetItemViewModel(this, preset));
        }
    }

    private async Task RunAsync(ICommand command)
    {
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            _ui.Post(() => Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "The effects panel's {Command} failed", CommandRegistry.NameOf(command));
            _ui.Post(() => Status = exception.Message);
        }
    }
}
