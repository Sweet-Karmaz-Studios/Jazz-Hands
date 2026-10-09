using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Playback;

/// <summary>
/// The program monitor as the multicam viewer (Phase 41): a multicam clip's angles in a grid,
/// the live one outlined; clicking an angle or pressing 1 to 9 cuts to it at the frame on
/// screen. While it plays the cuts are gathered and sent as one batch when it stops, so a
/// recording pass is one undo step; stopped, each goes at once.
/// </summary>
public sealed partial class PreviewPanelViewModel
{
    private readonly List<ICommand> _pendingSwitches = [];

    /// <summary>The multicam clip under the playhead on the active sequence, or null: what the Multicam button shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowMulticam))]
    private string? _multicamUnder;

    /// <summary>The multicam clip shown as its grid, or null for the program.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMulticamView))]
    [NotifyPropertyChangedFor(nameof(CanShowMulticam))]
    private string? _multicamClipId;

    /// <summary>The live angle's cell as fractions of the picture, for the outline; empty outside the grid.</summary>
    [ObservableProperty]
    private Rect _multicamCell = Rect.Empty;

    /// <summary>The live angle's name, for the preview bar.</summary>
    [ObservableProperty]
    private string _multicamAngle = string.Empty;

    /// <summary>True when there is a multicam clip under the playhead to show as a grid.</summary>
    public bool CanShowMulticam => MulticamUnder is not null || MulticamClipId is not null;

    /// <summary>True while the program monitor shows a multicam grid.</summary>
    public bool IsMulticamView => MulticamClipId is not null;

    /// <summary>How many switches are waiting for playback to stop.</summary>
    public int PendingSwitches => _pendingSwitches.Count;

    /// <summary>Shows the grid of the multicam clip under the playhead, or the program again.</summary>
    [RelayCommand]
    private void ToggleMulticam() => Send(new ViewMulticamCommand(MulticamClipId is null ? MulticamUnder : null));

    /// <summary>Reads which multicam is under the playhead and which angle is live, for the button and the outline.</summary>
    private void RefreshMulticam(Project project, Sequence? sequence)
    {
        Flicks at = _engine.Position;
        MulticamUnder = sequence?.Tracks
            .Where(track => track.Kind == TrackKind.Video)
            .SelectMany(track => track.Clips)
            .FirstOrDefault(clip => clip.Start <= at && at < clip.End && clip.SequenceId is { } id && project.Sequence(id)?.Multicam is not null)?.Id;
        // A grid whose clip was flattened, deleted or undone away is no grid: the program shows,
        // and the engine forgets it, so an undo that brings the clip back does not bring the grid.
        MulticamClipId = _engine.MulticamGrid is { } grid && project.FindClip(grid)?.Clip.SequenceId is { } shownId && project.Sequence(shownId)?.Multicam is not null
            ? grid
            : null;
        if (MulticamClipId is null && _engine.MulticamGrid is not null)
        {
            Send(new ViewMulticamCommand(null));
        }

        if (MulticamClipId is { } clipId && project.FindClip(clipId) is { } found
            && found.Clip.SequenceId is { } nestedId && project.Sequence(nestedId)?.Multicam is { } multicam)
        {
            int picture = multicam.ActiveAt(found.Clip.SourceTimeAt(at)).Picture;
            int[] pictures = [.. MulticamGrid.Pictures(multicam)];
            int cell = Array.IndexOf(pictures, picture);
            (double left, double top, double width, double height) = cell < 0 ? (0, 0, 0, 0) : MulticamGrid.Cell(cell, pictures.Length);
            MulticamCell = cell < 0 ? Rect.Empty : new Rect(left, top, width, height);
            MulticamAngle = $"Angle {picture + 1}: {multicam.Angles[picture].Name}";
        }
        else
        {
            MulticamCell = Rect.Empty;
            MulticamAngle = string.Empty;
        }
    }

    /// <summary>The 1 to 9 keys in the grid: true when the key cut to an angle.</summary>
    private bool MulticamKey(Key key, ModifierKeys modifiers)
    {
        if (MulticamClipId is null || modifiers != ModifierKeys.None)
        {
            return false;
        }

        int angle = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => 0,
        };

        if (angle == 0)
        {
            return false;
        }

        Cut(angle);
        return true;
    }

    /// <summary>A click on the grid, as fractions across and down the picture: cuts to the angle there.</summary>
    public bool ClickGrid(double across, double down)
    {
        if (MulticamClipId is not { } clipId || _session.Project.FindClip(clipId)?.Clip.SequenceId is not { } nestedId
            || _session.Project.Sequence(nestedId)?.Multicam is not { } multicam
            || MulticamGrid.AngleAt(multicam, across, down) is not { } angle)
        {
            return false;
        }

        Cut(angle + 1);
        return true;
    }

    /// <summary>
    /// Cuts to an angle at the frame on screen: the playhead, on the sequence frame it falls in.
    /// Playing, it waits for the stop; stopped, it goes at once.
    /// </summary>
    private void Cut(int angle)
    {
        Project project = _session.Project;
        Rational rate = project.ActiveSequence is { } sequence ? project.SettingsFor(sequence).FrameRate : project.Settings.FrameRate;
        Flicks shown = _engine.Position.SnapToFrame(rate);
        var cut = new SwitchAngleCommand(MulticamClipId!, shown, angle);
        if (IsPlaying)
        {
            _pendingSwitches.Add(cut);
            OnPropertyChanged(nameof(PendingSwitches));
        }
        else
        {
            Send(cut);
        }
    }

    /// <summary>Sends the cuts gathered while playing, as one undo step.</summary>
    private void FlushSwitches()
    {
        if (_pendingSwitches.Count == 0)
        {
            return;
        }

        ICommand[] cuts = [.. _pendingSwitches];
        _pendingSwitches.Clear();
        OnPropertyChanged(nameof(PendingSwitches));
        Send(new BatchCommand([.. cuts], cuts.Length == 1 ? "Multicam cut" : $"{cuts.Length} multicam cuts"));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (!value)
        {
            FlushSwitches();
        }
    }
}
