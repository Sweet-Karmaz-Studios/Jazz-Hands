using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Engine.Commands;

namespace JazzHands.App.ViewModels.Subtitles;

/// <summary>
/// The Subtitles panel: a subtitle track's cues as a list to read and edit, with find and replace,
/// shifting, splitting long cues, the track's style, and import and export.
/// </summary>
/// <remarks>
/// Everything it does is a command the command line has: typing into a cue is
/// <c>subtitle.set-text</c> (one undo step while typing), a time typed in is
/// <c>subtitle.set-time</c>, the buttons are <c>subtitle.add</c>, <c>clip.remove</c>,
/// <c>subtitle.replace</c>, <c>subtitle.shift</c>, <c>subtitle.split-long</c>,
/// <c>subtitle.import</c> and <c>subtitle.export</c>, and the style fields
/// <c>subtitle.set-style</c>. Selecting a cue moves the playhead to it (<c>playback.seek</c>).
/// </remarks>
public sealed partial class SubtitlesPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "subtitles";

    private readonly ISession _session;
    private readonly IUiDispatcher _ui;
    private readonly IFileDialogService? _files;
    private readonly IPreviewEngine? _preview;
    private readonly CommandPump _pump;
    private bool _applying;

    [ObservableProperty]
    private SubtitleTrackChoice? _selectedTrack;

    [ObservableProperty]
    private CueRowViewModel? _selectedCue;

    [ObservableProperty]
    private string _find = string.Empty;

    [ObservableProperty]
    private string _replaceWith = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _shiftBy = "0.5";

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _styleFont = SubtitleStyle.Default.Font;

    [ObservableProperty]
    private double _styleSize = SubtitleStyle.Default.Size * 100;

    [ObservableProperty]
    private string _styleColor = SubtitleStyle.Default.Color;

    [ObservableProperty]
    private string _styleBox = SubtitleStyle.Default.Box;

    [ObservableProperty]
    private bool _styleBoxed;

    [ObservableProperty]
    private string? _language;

    /// <summary>Creates the panel.</summary>
    public SubtitlesPanelViewModel(ISession session, IUiDispatcher ui, IFileDialogService? files = null, IPreviewEngine? preview = null)
        : base(PanelId, "Subtitles")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _ui = ui;
        _files = files;
        _preview = preview;
        _pump = new CommandPump(session, ui);
        _pump.Refused += (_, message) => Status = message;
        _pump.Settled += (_, _) => Refresh();

        _session.ProjectChanged += (_, _) => _ui.Post(Refresh);
        _preview?.PlayheadMoved += (_, _) => _ui.Post(MarkCurrent);
        Refresh();
    }

    /// <summary>The active sequence's subtitle tracks.</summary>
    public ObservableCollection<SubtitleTrackChoice> Tracks { get; } = [];

    /// <summary>The chosen track's cues, in time order.</summary>
    public ObservableCollection<CueRowViewModel> Cues { get; } = [];

    /// <summary>True when the sequence has a subtitle track to show.</summary>
    public bool HasTrack => SelectedTrack is not null;

    /// <summary>The style's size as a label: percent of the frame's height, and pixels at 1080.</summary>
    public string StyleSizeText => string.Create(CultureInfo.InvariantCulture, $"{StyleSize:0.0}% ({StyleSize * 10.8:0} px at 1080)");

    private Flicks Playhead => _preview?.Position ?? Flicks.Zero;

    private Rational Rate => _session.Project.ActiveSequence is { } sequence ? _session.Project.SettingsFor(sequence).FrameRate : Rational.Fps30;

    /// <summary>Reads the active sequence's subtitle tracks and the chosen one's cues. UI thread.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        Track[] tracks = project.ActiveSequence is { } sequence
            ? [.. sequence.Tracks.Where(track => track.Kind == TrackKind.Subtitle).OrderByDescending(track => track.Order)]
            : [];

        _applying = true;
        try
        {
            string? chosen = SelectedTrack?.Id;
            Tracks.Clear();
            foreach (Track track in tracks)
            {
                Tracks.Add(new SubtitleTrackChoice(track.Id, track.Language is { } language ? $"{track.Name} ({language})" : track.Name));
            }

            SelectedTrack = Tracks.FirstOrDefault(track => track.Id == chosen) ?? Tracks.FirstOrDefault();
            Track? current = tracks.FirstOrDefault(track => track.Id == SelectedTrack?.Id);

            SyncCues(current);
            SubtitleStyle style = current?.SubtitleStyle ?? SubtitleStyle.Default;
            StyleFont = style.Font;
            StyleSize = Math.Round(style.Size * 100, 2);
            StyleColor = style.Color;
            StyleBoxed = !style.Box.EndsWith("00", StringComparison.Ordinal) || style.Box.Length == 7;
            StyleBox = style.Box;
            Language = current?.Language;
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(HasTrack));
        MarkCurrent();
    }

    partial void OnSelectedTrackChanged(SubtitleTrackChoice? value)
    {
        if (!_applying)
        {
            Refresh();
        }
    }

    partial void OnSelectedCueChanged(CueRowViewModel? value)
    {
        if (!_applying && value is not null)
        {
            Send(new SeekCommand(value.Start));
        }
    }

    partial void OnStyleFontChanged(string value) => SendStyle(() => new SetSubtitleStyleCommand(SelectedTrack!.Id, Font: value));

    partial void OnStyleSizeChanged(double value)
    {
        OnPropertyChanged(nameof(StyleSizeText));
        SendStyle(() => new SetSubtitleStyleCommand(SelectedTrack!.Id, Size: Math.Round(value / 100, 4)));
    }

    partial void OnStyleColorChanged(string value) => SendStyle(() => new SetSubtitleStyleCommand(SelectedTrack!.Id, Color: value));

    partial void OnStyleBoxedChanged(bool value) =>
        SendStyle(() => new SetSubtitleStyleCommand(SelectedTrack!.Id, Box: value ? "#000000A0" : "#00000000", Shadow: value ? "#00000000" : SubtitleStyle.Default.Shadow));

    partial void OnLanguageChanged(string? value)
    {
        if (!_applying && SelectedTrack is { } track)
        {
            Send(new SetTrackLanguageCommand(track.Id, value is { Length: > 0 } code ? code : "und"));
        }
    }

    /// <summary>Makes a subtitle track, which new projects do not have.</summary>
    [RelayCommand]
    private void AddTrack() => Send(new AddTrackCommand(TrackKind.Subtitle, "Subtitles"));

    /// <summary>Adds a cue at the playhead, two seconds long, and selects nothing so typing starts in it.</summary>
    [RelayCommand]
    private void AddCue()
    {
        if (SelectedTrack is { } track)
        {
            Send(new AddCueCommand(track.Id, Playhead, Flicks.FromSeconds(2), "New subtitle"));
        }
    }

    /// <summary>Takes the selected cue away.</summary>
    [RelayCommand]
    private void RemoveCue()
    {
        if (SelectedCue is { } cue)
        {
            Send(new RemoveClipCommand(cue.Id));
        }
    }

    /// <summary>Replaces every match in the track.</summary>
    [RelayCommand]
    private void ReplaceAll()
    {
        if (SelectedTrack is { } track && Find.Length > 0)
        {
            Send(new ReplaceCueTextCommand(track.Id, Find, ReplaceWith, MatchCase));
        }
    }

    /// <summary>Moves every cue by the seconds typed, earlier when negative.</summary>
    [RelayCommand]
    private void Shift()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        if (!double.TryParse(ShiftBy, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds))
        {
            Status = $"'{ShiftBy}' is not a number of seconds.";
            return;
        }

        Send(new ShiftCuesCommand(track.Id, Flicks.FromSeconds(seconds)));
    }

    /// <summary>Breaks cues too long to read into lines and shorter cues.</summary>
    [RelayCommand]
    private void SplitLong()
    {
        if (SelectedTrack is { } track)
        {
            Send(new SplitLongCuesCommand(track.Id));
        }
    }

    /// <summary>Imports a subtitle file: onto the chosen track, or a new one when there is none.</summary>
    [RelayCommand]
    private void Import()
    {
        if (_files?.OpenSubtitles() is { } file)
        {
            Send(new ImportSubtitlesCommand(file, TrackId: SelectedTrack?.Id));
        }
    }

    /// <summary>Writes the chosen track as a file.</summary>
    [RelayCommand]
    private void Export()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        string folder = _session.ProjectPath.Length > 0 ? Path.GetDirectoryName(_session.ProjectPath)! : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (_files?.SaveSubtitles(Path.Combine(folder, "subtitles.srt")) is not { } path)
        {
            return;
        }

        try
        {
            SubtitleExport written = _session.Query(new ExportSubtitlesQuery(track.Id, Output: path));
            Status = $"Wrote {written.Cues} cues to {written.Path}.";
        }
        catch (CommandException error)
        {
            Status = error.Message;
        }
    }

    /// <summary>Sends a cue's edited text, one undo step while typing.</summary>
    internal void SendText(string cueId, string text) =>
        _pump.Send("text:" + cueId, () => new SetCueTextCommand(cueId, text));

    /// <summary>Sends a cue's edited start or end, typed as a time.</summary>
    internal void SendTime(CueRowViewModel cue, string text, bool end)
    {
        try
        {
            var time = (Flicks)CommandValues.Parse(typeof(Flicks), text, Rate, end ? "end" : "at")!;
            Send(end ? new SetCueTimeCommand(cue.Id, End: time) : new SetCueTimeCommand(cue.Id, At: time));
        }
        catch (CommandException error)
        {
            Status = error.Message;
            Refresh();
        }
    }

    private void Send(ICommand command) => _pump.Send(Id.New(), () => command);

    private void SendStyle(Func<ICommand> make)
    {
        if (!_applying && SelectedTrack is not null)
        {
            _pump.Send("style", make);
        }
    }

    private void SyncCues(Track? track)
    {
        Clip[] clips = track is null ? [] : [.. track.Clips.Where(clip => clip.Cue is not null)];
        string? selected = SelectedCue?.Id;

        // Rows are kept while their cue is, so the one being typed into is not swapped for a copy.
        var existing = Cues.ToDictionary(row => row.Id, StringComparer.Ordinal);
        Cues.Clear();
        foreach (Clip clip in clips)
        {
            CueRowViewModel row = existing.TryGetValue(clip.Id, out CueRowViewModel? kept) ? kept : new CueRowViewModel(this, clip.Id);
            row.Apply(clip, _pump.IsSending("text:" + clip.Id));
            Cues.Add(row);
        }

        SelectedCue = Cues.FirstOrDefault(row => row.Id == selected);
    }

    private void MarkCurrent()
    {
        Flicks now = Playhead;
        foreach (CueRowViewModel row in Cues)
        {
            row.IsCurrent = row.Start <= now && now < row.End;
        }
    }
}

/// <summary>A subtitle track to choose in the panel.</summary>
/// <param name="Id">The track.</param>
/// <param name="Name">Its name and language.</param>
public sealed record SubtitleTrackChoice(string Id, string Name);

/// <summary>One cue in the list: its times and its text, editable.</summary>
public sealed partial class CueRowViewModel : ObservableObject
{
    private readonly SubtitlesPanelViewModel _panel;
    private bool _applying;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _startText = string.Empty;

    [ObservableProperty]
    private string _endText = string.Empty;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isTop;

    internal CueRowViewModel(SubtitlesPanelViewModel panel, string id)
    {
        _panel = panel;
        Id = id;
    }

    /// <summary>The cue.</summary>
    public string Id { get; }

    /// <summary>When it appears.</summary>
    public Flicks Start { get; private set; }

    /// <summary>When it goes.</summary>
    public Flicks End { get; private set; }

    /// <summary>How many characters its longest line has, which a reader has to take in.</summary>
    public int LongestLine { get; private set; }

    /// <summary>True when a line is longer than 42 characters, the usual limit.</summary>
    public bool IsLong => LongestLine > SubtitleStyle.Default.MaxChars;

    /// <summary>Reads the cue in without sending anything back.</summary>
    internal void Apply(Clip clip, bool keepText)
    {
        _applying = true;
        try
        {
            Start = clip.Start;
            End = clip.End;
            StartText = Timecode.FormatClock(clip.Start);
            EndText = Timecode.FormatClock(clip.End);
            IsTop = clip.Cue!.Align is SubtitleAlign.TopLeft or SubtitleAlign.Top or SubtitleAlign.TopRight;
            if (!keepText)
            {
                Text = clip.Cue.Text;
            }

            LongestLine = TitleMarkup.PlainText(clip.Cue.Text).Split('\n').Max(line => line.Length);
            OnPropertyChanged(nameof(IsLong));
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!_applying)
        {
            _panel.SendText(Id, value);
        }
    }

    // The time fields update when they are left, so a time is sent once it is whole.
    partial void OnStartTextChanged(string value)
    {
        if (!_applying)
        {
            _panel.SendTime(this, value, end: false);
        }
    }

    partial void OnEndTextChanged(string value)
    {
        if (!_applying)
        {
            _panel.SendTime(this, value, end: true);
        }
    }
}
