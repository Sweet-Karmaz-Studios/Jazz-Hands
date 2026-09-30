using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Models;
using JazzHands.Engine.Selection;

namespace JazzHands.App.ViewModels.Transcript;

/// <summary>
/// The Transcript panel (Phase 39): what is said in the selected clip, or in the whole sequence,
/// as paragraphs of words. The word being said follows the playhead; clicking a word goes there;
/// selecting words and pressing Delete cuts them out of the picture and the sound. Filler words
/// and long pauses are greyed, with one button to take them all out.
/// </summary>
/// <remarks>
/// Every edit is a command the command line has: a deletion is <c>clip.remove-words</c> (one undo
/// step, several stretches as one batch), removing fillers <c>clip.remove-fillers</c>, a click
/// <c>playback.seek</c>. The words are <c>speech.transcript</c>. Hearing runs on
/// <see cref="TranscriptionService"/> in the background with progress, not through the command
/// queue, so editing carries on while it works; the model is fetched only when the person presses
/// the button that says its size.
/// </remarks>
public sealed partial class TranscriptPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "transcript";

    /// <summary>A pause at least this long is shown, greyed, and taken out by Remove fillers (down to a quarter second).</summary>
    public static readonly Flicks LongPause = Flicks.OneSecond;

    private readonly ISession _session;
    private readonly IUiDispatcher _ui;
    private readonly TranscriptionService? _speech;
    private readonly IPreviewEngine? _preview;
    private readonly SelectionService? _selection;
    private readonly CommandPump _pump;
    private CancellationTokenSource? _work;
    private TranscriptWordViewModel? _anchor;

    [ObservableProperty]
    private string _scope = "Sequence";

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private int _untranscribed;

    [ObservableProperty]
    private int _fillerCount;

    [ObservableProperty]
    private int _matchCount;

    /// <summary>Creates the panel.</summary>
    public TranscriptPanelViewModel(ISession session, IUiDispatcher ui, TranscriptionService? speech = null, IPreviewEngine? preview = null, SelectionService? selection = null)
        : base(PanelId, "Transcript")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _ui = ui;
        _speech = speech;
        _preview = preview;
        _selection = selection;
        _pump = new CommandPump(session, ui);
        _pump.Refused += (_, message) => Status = message;

        _session.ProjectChanged += (_, _) => _ui.Post(Refresh);
        _preview?.PlayheadMoved += (_, _) => _ui.Post(MarkCurrent);
        _selection?.Changed += (_, _) => _ui.Post(Refresh);
        _speech?.Made += _ => _ui.Post(Refresh);
        Refresh();
    }

    /// <summary>The words in paragraphs: a paragraph per sentence, and a greyed line for each long pause.</summary>
    public ObservableCollection<TranscriptParagraph> Paragraphs { get; } = [];

    /// <summary>Every word shown, in order.</summary>
    public IReadOnlyList<TranscriptWordViewModel> Words { get; private set; } = [];

    /// <summary>True when the speech model has to be fetched before anything can be heard.</summary>
    public bool NeedsModel => !ModelStore.IsPresent(ModelStore.Whisper);

    /// <summary>What fetching the model costs, for its button.</summary>
    public string ModelText => string.Create(CultureInfo.InvariantCulture, $"Download the speech model ({ModelStore.Whisper.Bytes / 1e9:0.0} GB, whisper large-v3-turbo, MIT)");

    /// <summary>True when there is nothing said to show.</summary>
    public bool IsEmpty => Words.Count == 0;

    /// <summary>What the empty panel says.</summary>
    public string EmptyText => Untranscribed > 0
        ? "Nothing here has been transcribed yet. Transcribe hears it on this computer."
        : "Nothing here plays sound from a file. Select a clip with speech, or add one.";

    /// <summary>How many filler words there are, said as a count.</summary>
    public string FillerText => JazzHands.Core.Words.Count(FillerCount, "filler");

    /// <summary>How many words are selected.</summary>
    public int SelectedCount => Words.Count(word => word.IsSelected);

    private Rational Rate => _session.Project.ActiveSequence is { } sequence ? _session.Project.SettingsFor(sequence).FrameRate : Rational.Fps30;

    /// <summary>Reads the words of the selected clip, or of the sequence. UI thread.</summary>
    public void Refresh()
    {
        Project project = _session.Project;
        string? clipId = _selection?.Ids.FirstOrDefault(id => project.FindClip(id) is { Clip.MediaId: not null });
        TranscriptInfo info;
        try
        {
            info = _session.Query(new TranscriptQuery(clipId));
        }
        catch (CommandException error)
        {
            info = new TranscriptInfo([], string.Empty, string.Empty, []);
            Status = error.Message;
        }

        Scope = clipId is not null && project.FindClip(clipId) is { } found ? found.Clip.Name : "Sequence";
        var selected = new HashSet<(string, int)>(Words.Where(word => word.IsSelected).Select(word => (word.ClipId, word.Index)));

        var words = new List<TranscriptWordViewModel>();
        Paragraphs.Clear();
        TranscriptParagraph? paragraph = null;
        TranscriptWordViewModel? previous = null;
        foreach (WordInfo word in info.Words)
        {
            var item = new TranscriptWordViewModel(this, word) { IsSelected = selected.Contains((word.ClipId, word.Index)) };
            bool newClip = previous is not null && previous.ClipId != word.ClipId;
            if (previous is not null && word.Start - previous.End >= LongPause)
            {
                Paragraphs.Add(TranscriptParagraph.Pause(string.Create(CultureInfo.InvariantCulture, $"Pause, {(word.Start - previous.End).ToSeconds():0.0} s")));
                paragraph = null;
            }

            if (paragraph is null || newClip || (previous?.Text.TrimEnd() is { } said && (said.EndsWith('.') || said.EndsWith('?') || said.EndsWith('!'))))
            {
                paragraph = new TranscriptParagraph(Timecode.Format(word.Start, Rate));
                Paragraphs.Add(paragraph);
            }

            paragraph.Words.Add(item);
            words.Add(item);
            previous = item;
        }

        Words = words;
        Untranscribed = info.Untranscribed.Length;
        FillerCount = words.Count(word => word.IsFiller);
        ApplySearch();
        OnPropertyChanged(nameof(Words));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NeedsModel));
        OnPropertyChanged(nameof(SelectedCount));
        TranscribeCommand.NotifyCanExecuteChanged();
        MarkCurrent();
    }

    /// <summary>
    /// A click on a word: goes there and selects it, or, with Shift, selects from the last word
    /// clicked to this one (within one clip, since a cut is of one clip's words).
    /// </summary>
    public void Click(TranscriptWordViewModel word, bool extend)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (extend && _anchor is { } anchor && anchor.ClipId == word.ClipId)
        {
            int from = Math.Min(anchor.Index, word.Index);
            int to = Math.Max(anchor.Index, word.Index);
            foreach (TranscriptWordViewModel each in Words)
            {
                each.IsSelected = each.ClipId == word.ClipId && each.Index >= from && each.Index <= to;
            }
        }
        else
        {
            foreach (TranscriptWordViewModel each in Words)
            {
                each.IsSelected = ReferenceEquals(each, word);
            }

            _anchor = word;
            Send(new SeekCommand(word.Start));
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    partial void OnSearchChanged(string value) => ApplySearch();

    partial void OnUntranscribedChanged(int value) => OnPropertyChanged(nameof(EmptyText));

    partial void OnFillerCountChanged(int value) => OnPropertyChanged(nameof(FillerText));

    /// <summary>Cuts the selected words out, with the pause after each stretch, as one undo step.</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        // Runs of neighbouring words in one clip, the latest first so the earlier indices still hold.
        var runs = new List<(string ClipId, int From, int To)>();
        foreach (TranscriptWordViewModel word in Words.Where(word => word.IsSelected).OrderBy(word => word.ClipId, StringComparer.Ordinal).ThenBy(word => word.Index))
        {
            if (runs.Count > 0 && runs[^1].ClipId == word.ClipId && runs[^1].To == word.Index - 1)
            {
                runs[^1] = runs[^1] with { To = word.Index };
            }
            else
            {
                runs.Add((word.ClipId, word.Index, word.Index));
            }
        }

        if (runs.Count == 0)
        {
            return;
        }

        ICommand[] cuts = [.. runs.OrderByDescending(run => Words.First(word => word.ClipId == run.ClipId && word.Index == run.From).Start)
            .Select(run => (ICommand)new RemoveWordsCommand(run.ClipId, run.From, run.To))];
        Send(cuts.Length == 1 ? cuts[0] : new BatchCommand(cuts, "Delete words"));
        _anchor = null;
    }

    /// <summary>Takes out every filler word and shortens every long pause, as one undo step.</summary>
    [RelayCommand]
    private void RemoveFillers()
    {
        // The latest clip first, so cutting one does not move the others' words under it.
        string[] clips = [.. Words.GroupBy(word => word.ClipId).OrderByDescending(group => group.First().Start).Select(group => group.Key)];
        ICommand[] cuts = [.. clips.Select(clip => (ICommand)new RemoveFillersCommand(clip, Pauses: LongPause))];
        if (cuts.Length > 0)
        {
            Send(cuts.Length == 1 ? cuts[0] : new BatchCommand(cuts, "Remove fillers and pauses"));
        }
    }

    /// <summary>
    /// Lays the words shown out as captions (<c>subtitle.from-transcript</c>) on the first unlocked
    /// subtitle track, or on a new one called Captions, as one undo step. Cues already there over
    /// the same stretch are replaced, so pressing it again after an edit brings them up to date.
    /// </summary>
    [RelayCommand]
    private void MakeCaptions()
    {
        if (Words.Count == 0 || _session.Project.ActiveSequence is not { } sequence)
        {
            return;
        }

        var commands = new List<ICommand>();
        string trackId;
        if (sequence.Tracks.FirstOrDefault(track => track.Kind == TrackKind.Subtitle && !track.Locked) is { } existing)
        {
            trackId = existing.Id;
        }
        else
        {
            trackId = Id.New();
            commands.Add(new AddTrackCommand(TrackKind.Subtitle, "Captions", TrackId: trackId));
        }

        commands.AddRange(Words.Select(word => word.ClipId).Distinct(StringComparer.Ordinal).Select(clip => (ICommand)new SubtitleFromTranscriptCommand(trackId, clip)));
        Send(new BatchCommand([.. commands], "Make captions"));
    }

    /// <summary>Goes to the next word that matches the search, after the playhead.</summary>
    [RelayCommand]
    private void FindNext()
    {
        Flicks now = _preview?.Position ?? Flicks.Zero;
        TranscriptWordViewModel? next = Words.FirstOrDefault(word => word.IsMatch && word.Start > now) ?? Words.FirstOrDefault(word => word.IsMatch);
        if (next is not null)
        {
            Click(next, extend: false);
        }
    }

    /// <summary>Hears every sounding clip here that has no transcript, in the background.</summary>
    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        if (_speech is null)
        {
            return;
        }

        if (NeedsModel)
        {
            Status = "The speech model is not downloaded yet: use the button above, which says its size.";
            return;
        }

        Project project = _session.Project;
        string? clipId = _selection?.Ids.FirstOrDefault(id => project.FindClip(id) is { Clip.MediaId: not null });
        string[] clips = _session.Query(new TranscriptQuery(clipId)).Untranscribed;
        await RunAsync("Transcribing", async (progress, token) =>
        {
            for (int index = 0; index < clips.Length; index++)
            {
                int done = index;
                var each = new Progress<double>(fraction => progress.Report((done + fraction) / clips.Length));
                await _speech.TranscribeClipAsync(project, clips[index], _session.ProjectPath, progress: each, cancellationToken: token).ConfigureAwait(false);
            }
        }).ConfigureAwait(true);
    }

    private bool CanTranscribe() => !IsBusy && Untranscribed > 0 && _speech is not null;

    /// <summary>Fetches the speech model: the person pressed the button that says its size.</summary>
    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        await RunAsync("Downloading the speech model", (progress, token) => ModelStore.DownloadAsync(ModelStore.Whisper, progress, token)).ConfigureAwait(true);
        OnPropertyChanged(nameof(NeedsModel));
        if (!NeedsModel && Untranscribed > 0)
        {
            await TranscribeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Stops the hearing or the download.</summary>
    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    partial void OnIsBusyChanged(bool value) => TranscribeCommand.NotifyCanExecuteChanged();

    private async Task RunAsync(string what, Func<IProgress<double>, CancellationToken, Task> work)
    {
        _work?.Dispose();
        _work = new CancellationTokenSource();
        CancellationToken token = _work.Token;
        IsBusy = true;
        Progress = 0;
        Status = what + "...";
        var progress = new Progress<double>(fraction => _ui.Post(() =>
        {
            Progress = fraction;
            Status = string.Create(CultureInfo.InvariantCulture, $"{what}... {fraction * 100:0}%");
        }));

        try
        {
            await Task.Run(() => work(progress, token), token).ConfigureAwait(true);
            Status = null;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Http.HttpRequestException or CommandException or InvalidOperationException)
        {
            Status = error.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _ui.Post(Refresh);
        }
    }

    private void ApplySearch()
    {
        string[] terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int matches = 0;
        foreach (TranscriptWordViewModel word in Words)
        {
            word.IsMatch = terms.Length > 0 && terms.Any(term => word.Bare.StartsWith(term, StringComparison.OrdinalIgnoreCase));
            matches += word.IsMatch ? 1 : 0;
        }

        MatchCount = matches;
    }

    private void MarkCurrent()
    {
        Flicks now = _preview?.Position ?? Flicks.Zero;
        foreach (TranscriptWordViewModel word in Words)
        {
            word.IsCurrent = word.Start <= now && now < word.End;
        }
    }

    private void Send(ICommand command) => _pump.Send(Id.New(), () => command);
}

/// <summary>A paragraph of the transcript: a sentence's words, or a long pause.</summary>
public sealed class TranscriptParagraph
{
    /// <summary>A paragraph of words starting at a timecode.</summary>
    public TranscriptParagraph(string time) => Time = time;

    /// <summary>When it starts, as timecode; empty for a pause.</summary>
    public string Time { get; }

    /// <summary>What a pause line says; null for words.</summary>
    public string? PauseText { get; private init; }

    /// <summary>True for a long pause.</summary>
    public bool IsPause => PauseText is not null;

    /// <summary>Its words.</summary>
    public ObservableCollection<TranscriptWordViewModel> Words { get; } = [];

    /// <summary>A pause line.</summary>
    public static TranscriptParagraph Pause(string text) => new(string.Empty) { PauseText = text };
}

/// <summary>One word in the Transcript panel.</summary>
public sealed partial class TranscriptWordViewModel : ObservableObject
{
    private readonly TranscriptPanelViewModel _panel;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isMatch;

    internal TranscriptWordViewModel(TranscriptPanelViewModel panel, WordInfo word)
    {
        _panel = panel;
        ClipId = word.ClipId;
        Index = word.Index;
        Text = word.Text;
        Start = word.Start;
        End = word.End;
        IsFiller = word.Filler;
        Confidence = word.Confidence;
        Bare = new string([.. word.Text.Where(character => char.IsLetterOrDigit(character) || character == '\'')]);
    }

    /// <summary>The clip it is said in.</summary>
    public string ClipId { get; }

    /// <summary>Its index among that clip's words.</summary>
    public int Index { get; }

    /// <summary>The word as written.</summary>
    public string Text { get; }

    /// <summary>The word without punctuation, for searching.</summary>
    public string Bare { get; }

    /// <summary>When it begins on the timeline.</summary>
    public Flicks Start { get; }

    /// <summary>When it ends on the timeline.</summary>
    public Flicks End { get; }

    /// <summary>True for a filler word, shown greyed.</summary>
    public bool IsFiller { get; }

    /// <summary>How sure the model was.</summary>
    public double Confidence { get; }

    /// <summary>The tooltip: when, and how sure.</summary>
    public string Tip => string.Create(CultureInfo.InvariantCulture, $"{Timecode.FormatClock(Start)}, {Confidence * 100:0}% sure{(IsFiller ? ", a filler word" : string.Empty)}");

    /// <summary>Clicks it.</summary>
    public void Click(bool extend) => _panel.Click(this, extend);
}
