using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>
/// The Quick Trim side of a timeline: the stretches kept, what they add up to, and the buttons
/// for keeping, cutting and exporting.
/// </summary>
/// <remarks>
/// A Quick Trim timeline is an ordinary timeline over the file laid out at its source times; the
/// timeline control, the track headers and every mouse gesture are the timeline's own. This adds
/// the segment list and the keep and cut actions, which are <c>trim.add-segment</c> and
/// <c>trim.remove-range</c> between the in and out points, the same commands Enter and Backspace
/// send. Muting a lane is the track header's mute button.
/// </remarks>
public sealed partial class QuickTrimViewModel : ObservableObject
{
    private readonly TimelineViewModel _timeline;
    private readonly IDialogService? _dialogs;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _inOut = string.Empty;

    internal QuickTrimViewModel(TimelineViewModel timeline, IDialogService? dialogs)
    {
        _timeline = timeline;
        _dialogs = dialogs;
    }

    /// <summary>The kept stretches, in order.</summary>
    public ObservableCollection<TrimSegmentViewModel> Segments { get; } = [];

    /// <summary>Takes a new snapshot of the sequence.</summary>
    internal void Update(Project project, Sequence sequence)
    {
        Rational rate = project.SettingsFor(sequence).FrameRate;
        MediaItem? media = sequence.QuickTrim is { } trim ? project.MediaItem(trim.MediaId) : null;
        var segments = QuickTrimOps.Segments(sequence);

        // Replace only what changed, so a row being clicked stays under the mouse.
        for (int index = 0; index < segments.Length; index++)
        {
            if (index < Segments.Count && Segments[index].Range == segments[index])
            {
                continue;
            }

            var row = new TrimSegmentViewModel(this, index + 1, segments[index], rate);
            if (index < Segments.Count)
            {
                Segments[index] = row;
            }
            else
            {
                Segments.Add(row);
            }
        }

        while (Segments.Count > segments.Length)
        {
            Segments.RemoveAt(Segments.Count - 1);
        }

        Flicks kept = Flicks.Zero;
        foreach (TimeRange segment in segments)
        {
            kept += segment.Duration;
        }

        Summary = media is null
            ? "The file this trims is no longer in the project."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{segments.Length} stretch{(segments.Length == 1 ? string.Empty : "es")} kept: {Timecode.FormatClock(kept)} of {Timecode.FormatClock(QuickTrimOps.Length(media))}");

        // The out point reads as the last frame kept, the one O was pressed on, as the Preview's
        // readout has it; the range itself ends after that frame.
        InOut = sequence.InOut is { } marked
            ? $"In {Timecode.Format(marked.Start, rate)}, out {Timecode.Format(marked.End - project.SettingsFor(sequence).FrameDuration, rate)}"
            : "Mark an in and an out with I and O, then keep or cut between them.";

        KeepInOutCommand.NotifyCanExecuteChanged();
        CutInOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves the playhead to a stretch's start.</summary>
    internal Task GoToAsync(Flicks at) => _timeline.RunAsync(new SeekCommand(at));

    /// <summary>Cuts a whole stretch.</summary>
    internal Task RemoveAsync(TimeRange range) =>
        _timeline.RunAsync(new RemoveTrimRangeCommand(range.Start, range.End, _timeline.SequenceId));

    [RelayCommand(CanExecute = nameof(HasInOut))]
    private Task KeepInOutAsync() => InOutCommand((start, end) => new AddTrimSegmentCommand(start, end, _timeline.SequenceId));

    [RelayCommand(CanExecute = nameof(HasInOut))]
    private Task CutInOutAsync() => InOutCommand((start, end) => new RemoveTrimRangeCommand(start, end, _timeline.SequenceId));

    [RelayCommand]
    private Task ExportAsync() => _dialogs?.ShowExportAsync(_timeline.SequenceId) ?? Task.CompletedTask;

    private bool HasInOut() => _timeline.Content.Sequence.InOut is not null;

    private Task InOutCommand(Func<Flicks, Flicks, ICommand> make) =>
        _timeline.Content.Sequence.InOut is { } marked
            ? _timeline.RunAsync(make(marked.Start, marked.End))
            : Task.CompletedTask;
}

/// <summary>One kept stretch in the Quick Trim list.</summary>
public sealed partial class TrimSegmentViewModel : ObservableObject
{
    private readonly QuickTrimViewModel _owner;

    internal TrimSegmentViewModel(QuickTrimViewModel owner, int number, TimeRange range, Rational rate)
    {
        _owner = owner;
        Number = number;
        Range = range;
        Label = $"{Timecode.Format(range.Start, rate)} to {Timecode.Format(range.End, rate)}";
        Length = string.Create(CultureInfo.InvariantCulture, $"{range.Duration.ToSeconds():F1} s");
    }

    /// <summary>Its place in the list, from one.</summary>
    public int Number { get; }

    /// <summary>The stretch, in source time.</summary>
    public TimeRange Range { get; }

    /// <summary>Where it starts and ends, as timecode.</summary>
    public string Label { get; }

    /// <summary>How long it is.</summary>
    public string Length { get; }

    [RelayCommand]
    private Task GoToAsync() => _owner.GoToAsync(Range.Start);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(Range);
}
