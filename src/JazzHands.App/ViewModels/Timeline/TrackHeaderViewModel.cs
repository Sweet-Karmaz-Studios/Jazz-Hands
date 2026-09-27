using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>
/// One track's header beside the timeline: its name, its toggles, its colour and its height.
/// </summary>
/// <remarks>
/// Every change is a track command through the timeline's session; the header shows what the
/// project says after it, so a toggle that was refused (a track that cannot be soloed) springs
/// back. The height drag previews live on the timeline and becomes one <c>track.set-height</c>
/// when the drag ends.
/// </remarks>
public sealed partial class TrackHeaderViewModel : ObservableObject
{
    /// <summary>The colours a click on the swatch cycles through, the bin's label colours.</summary>
    public static readonly string[] Swatches = ["#3A6EA5", "#4C8AD0", "#5FA95F", "#C9C14E", "#D08A3E", "#D05353", "#9A6FC4", "#808080"];

    private readonly TimelineViewModel _timeline;
    private string _committedName = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private bool _muted;

    [ObservableProperty]
    private bool _solo;

    [ObservableProperty]
    private bool _syncLocked = true;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    private string _color = Swatches[0];

    [ObservableProperty]
    private bool _isAudio;

    /// <summary>True when edits from the source monitor go to this track (Phase 38).</summary>
    [ObservableProperty]
    private bool _isTarget;

    /// <summary>True for a picture or sound track, which can be targeted.</summary>
    [ObservableProperty]
    private bool _canTarget;

    /// <summary>The track's role (Phase 40).</summary>
    [ObservableProperty]
    private string _roleName = string.Empty;

    /// <summary>Its role's colour.</summary>
    [ObservableProperty]
    private string _roleColor = "#808080";

    private string _nextRole = string.Empty;

    internal TrackHeaderViewModel(TimelineViewModel timeline, string trackId)
    {
        _timeline = timeline;
        TrackId = trackId;
    }

    /// <summary>The track.</summary>
    public string TrackId { get; }

    /// <summary>Takes on what the project now says about the track.</summary>
    internal void Update(Track track, string label, double height, bool isTarget = false, IReadOnlyList<Role>? roles = null)
    {
        RoleName = Role.Of(track);
        Role[] all = [.. roles ?? Role.BuiltIn];
        int at = Array.FindIndex(all, role => string.Equals(role.Name, RoleName, StringComparison.OrdinalIgnoreCase));
        RoleColor = at >= 0 ? all[at].Color : "#808080";
        _nextRole = all.Length == 0 ? string.Empty : all[(at + 1) % all.Length].Name;
        IsTarget = isTarget;
        CanTarget = track.Kind is TrackKind.Video or TrackKind.Audio;
        _committedName = track.Name;
        Name = track.Name;
        Label = label;
        Locked = track.Locked;
        Muted = track.Muted;
        Solo = track.Solo;
        SyncLocked = track.IsSyncLocked;
        Height = height;
        Color = track.Color;
        IsAudio = track.Kind == TrackKind.Audio;
    }

    /// <summary>Renames the track, when the name was edited.</summary>
    [RelayCommand]
    public async Task CommitNameAsync()
    {
        string name = Name.Trim();

        if (name.Length == 0 || string.Equals(name, _committedName, StringComparison.Ordinal))
        {
            Name = _committedName;
            return;
        }

        if (!await _timeline.RunAsync(new RenameTrackCommand(TrackId, name)).ConfigureAwait(true))
        {
            Name = _committedName;
        }
    }

    /// <summary>Locks or unlocks the track.</summary>
    [RelayCommand]
    public Task ToggleLockAsync() => _timeline.RunAsync(new SetTrackLockCommand(TrackId, !Locked));

    /// <summary>Gives the track the next of the project's roles.</summary>
    [RelayCommand]
    public Task NextRoleAsync() => _nextRole.Length == 0 ? Task.CompletedTask : _timeline.RunAsync(new SetTrackRoleCommand(TrackId, _nextRole));

    /// <summary>Targets the track for edits from the source monitor, or stops.</summary>
    [RelayCommand]
    public Task ToggleTargetAsync() => _timeline.RunAsync(new SetTrackTargetCommand(TrackId, !IsTarget));

    /// <summary>Mutes or unmutes the track.</summary>
    [RelayCommand]
    public Task ToggleMuteAsync() => _timeline.RunAsync(new SetTrackMuteCommand(TrackId, !Muted));

    /// <summary>Solos or unsolos the track.</summary>
    [RelayCommand]
    public Task ToggleSoloAsync() => _timeline.RunAsync(new SetTrackSoloCommand(TrackId, !Solo));

    /// <summary>Turns sync lock on or off: whether ripples on other tracks move this one.</summary>
    [RelayCommand]
    public Task ToggleSyncLockAsync() => _timeline.RunAsync(new SetTrackSyncLockCommand(TrackId, !SyncLocked));

    /// <summary>Moves the track to the next colour in the palette.</summary>
    [RelayCommand]
    public Task NextColorAsync()
    {
        int index = Array.FindIndex(Swatches, swatch => string.Equals(swatch, Color, StringComparison.OrdinalIgnoreCase));
        return _timeline.RunAsync(new SetTrackColorCommand(TrackId, Swatches[(index + 1) % Swatches.Length]));
    }

    /// <summary>The height drag moved: show the new height without committing it.</summary>
    public void PreviewHeight(double height)
    {
        Height = Math.Clamp(height, TimelineRowLimits.MinHeight, TimelineRowLimits.MaxHeight);
        _timeline.PreviewTrackHeight(TrackId, Height);
    }

    /// <summary>The height drag ended: make it so.</summary>
    public Task CommitHeightAsync() => _timeline.RunAsync(new SetTrackHeightCommand(TrackId, Math.Round(Height)));
}
