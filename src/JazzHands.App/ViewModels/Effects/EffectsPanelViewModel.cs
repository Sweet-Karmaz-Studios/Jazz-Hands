using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
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

    [ObservableProperty]
    private ImageSource? _preview;

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

    /// <summary>True for a generator, which makes a clip rather than changing one.</summary>
    public bool IsGenerator => Descriptor.Kind == EffectKind.Generator;

    /// <summary>True for the types with a preview: everything but sound.</summary>
    public bool HasPicture => !IsAudio;

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
public sealed partial class EffectPresetItemViewModel(EffectsPanelViewModel panel, EffectPreset preset, bool builtIn = false) : ObservableObject
{
    /// <summary>True for a look the editor comes with: listed in every project, never removed.</summary>
    public bool IsBuiltIn { get; } = builtIn;

    /// <summary>True when the preset can be deleted: the project's own.</summary>
    public bool CanRemove => !IsBuiltIn;

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
/// The effects browser: every picture effect, generator and sound effect by folder, each with a
/// picture of what it does, a search, favorites, and the project's presets.
/// </summary>
/// <remarks>
/// Applying an effect adds it to every selected clip it suits, in one undo step; dragging one
/// onto a clip, a track or the inspector does the same for that one. A generator makes a clip
/// instead: + puts one at the playhead, and dragging one onto a video track puts it there. Presets save the first
/// selected clip's chain under a name and apply it anywhere. All of it goes through the same
/// <c>effect.*</c> commands the CLI has; only the favorites are the editor's own, a preference of
/// the person rather than a part of the project.
/// </remarks>
public sealed partial class EffectsPanelViewModel : ToolViewModel
{
    /// <summary>The docking content id.</summary>
    public const string PanelId = "effects";

    /// <summary>How long a generator clip added from the panel runs, as a title dropped on a timeline does.</summary>
    public static readonly Flicks GeneratorLength = Flicks.FromSeconds(5);

    private readonly ILogger _log = Log.ForContext<EffectsPanelViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IEffectFavorites _favorites;
    private readonly IEffectPreviewImages? _previews;
    private readonly IPreviewEngine? _playback;
    private readonly EffectRegistry _registry;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _presetName = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Creates the panel.</summary>
    /// <param name="session">The session commands go through.</param>
    /// <param name="selection">The selected clips, which the + buttons add to.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="favorites">The person's starred types.</param>
    /// <param name="previews">Pictures of the types, or null for names only.</param>
    /// <param name="playback">Where the playhead is, for placing a generator; the start when null.</param>
    /// <param name="registry">The types, or the editor's own.</param>
    public EffectsPanelViewModel(
        ISession session,
        SelectionService selection,
        IUiDispatcher ui,
        IEffectFavorites favorites,
        IEffectPreviewImages? previews = null,
        IPreviewEngine? playback = null,
        EffectRegistry? registry = null)
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
        _previews = previews;
        _playback = playback;
        _registry = registry ?? EffectCatalog.Registry;

        _previews?.Ready += OnPreviewReady;

        _session.ProjectChanged += (_, _) => _ui.Post(LoadPresets);
        Refilter();
        LoadPresets();
    }

    /// <summary>The folders, favorites first, then picture effects, then generators, then sound.</summary>
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

        if (descriptor.Kind == EffectKind.Generator)
        {
            return AddGeneratorAsync(descriptor);
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

    /// <summary>
    /// Puts a generator on the timeline at the playhead: on the highest video track with five
    /// free seconds there, or on a new track above the rest when none has.
    /// </summary>
    public Task AddGeneratorAsync(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (_session.Project.ActiveSequence is not { } sequence)
        {
            Status = "Open a sequence to add a generator to.";
            return Task.CompletedTask;
        }

        Flicks at = _playback?.Position ?? Flicks.Zero;
        Flicks end = at + GeneratorLength;
        Track? free = sequence.Tracks
            .Where(track => track.Kind == TrackKind.Video && !track.Locked)
            .OrderByDescending(track => track.Order)
            .FirstOrDefault(track => !track.Clips.Any(clip => clip.Start < end && at < clip.Start + clip.Duration));

        if (free is not null)
        {
            return RunAsync(new AddClipCommand(free.Id, at, GeneratorId: descriptor.TypeId, Duration: GeneratorLength, Name: descriptor.Name));
        }

        string trackId = Id.New();
        return RunAsync(new BatchCommand(
            [
                new AddTrackCommand(TrackKind.Video, TrackId: trackId),
                new AddClipCommand(trackId, at, GeneratorId: descriptor.TypeId, Duration: GeneratorLength, Name: descriptor.Name),
            ],
            $"Add {descriptor.Name}"));
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
            .Where(descriptor => descriptor.Kind is EffectKind.Video or EffectKind.Audio or EffectKind.Generator)
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
                favorites.Items.Add(Item(descriptor, favorite: true));
            }

            // Picture folders first, then generators, then sound: the sort key carries the kind,
            // the title does not repeat it for pictures.
            int rank = descriptor.Kind switch
            {
                EffectKind.Video => 0,
                EffectKind.Generator => 1,
                _ => 2,
            };
            string key = $"{rank}:{descriptor.Category}";
            if (!folders.TryGetValue(key, out EffectCategoryViewModel? folder))
            {
                folder = new EffectCategoryViewModel(descriptor.Kind == EffectKind.Audio ? $"Audio: {descriptor.Category}" : descriptor.Category);
                folders[key] = folder;
            }

            folder.Items.Add(Item(descriptor, favorite));
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

    private EffectTypeItemViewModel Item(EffectDescriptor descriptor, bool favorite) =>
        new(this, descriptor, favorite) { Preview = _previews?.Find(descriptor.TypeId) };

    private void OnPreviewReady(object? sender, string typeId)
    {
        ImageSource? image = _previews?.Find(typeId);
        foreach (EffectTypeItemViewModel item in Categories.SelectMany(category => category.Items).Where(item => item.TypeId == typeId))
        {
            item.Preview = image;
        }
    }

    private void LoadPresets()
    {
        Presets.Clear();
        foreach (EffectPreset preset in _session.Project.EffectPresets)
        {
            Presets.Add(new EffectPresetItemViewModel(this, preset));
        }

        // The looks every project has, after its own; one of its own with the same name wins.
        foreach (EffectPreset look in Looks.All.Where(look => !_session.Project.EffectPresets.Any(preset => string.Equals(preset.Name, look.Name, StringComparison.OrdinalIgnoreCase))))
        {
            Presets.Add(new EffectPresetItemViewModel(this, look, builtIn: true));
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
