using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Commands;
using JazzHands.Media.Analysis;

namespace JazzHands.App.ViewModels.Media;

/// <summary>A found shot change, where it falls across the strip.</summary>
/// <param name="Fraction">How far across, 0 to 1.</param>
/// <param name="IsDissolve">True for a dissolve, drawn differently from a cut.</param>
/// <param name="Tip">What it is, for its tooltip.</param>
public sealed record SceneCutMark(double Fraction, bool IsDissolve, string Tip);

/// <summary>
/// The scene cuts dialog: "Split at scene cuts..." on a clip, "Find scene cuts..." on a media
/// item. It reads the video in the background with a progress line, then shows the cuts over a
/// strip of pictures and follows the threshold as it moves, before anything is changed.
/// </summary>
/// <remarks>
/// The reading is <see cref="SceneCutService.Measure"/>, which keeps what it measured in the
/// cache, so the command a button sends (<c>clip.split-at-cuts</c>, <c>marker.add-at-cuts</c>,
/// <c>media.subclips-from-cuts</c>) finds it there and does not read the file again. The strip is
/// in source order, from the clip's in to its out (or the item's), so a reversed clip's cuts are
/// where they are in the file.
/// </remarks>
public sealed partial class SceneCutsViewModel : ObservableObject, IDisposable
{
    /// <summary>How many pictures the strip has.</summary>
    public const int TileCount = 10;

    private readonly ISession _session;
    private readonly SceneCutService _scenes;
    private readonly IMediaImagery? _imagery;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private MediaItem? _item;
    private Clip? _clip;
    private MediaStream? _stream;
    private SceneMeasurements? _measured;
    private Flicks _from;
    private Flicks _to;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SplitCommand), nameof(MarkCommand), nameof(SubclipsCommand))]
    private bool _isReading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _threshold = SceneCuts.DefaultThreshold;

    [ObservableProperty]
    private double _minShotSeconds = SceneCuts.DefaultMinShot.ToSeconds();

    [ObservableProperty]
    private string _found = string.Empty;

    /// <summary>A dialog over a session.</summary>
    public SceneCutsViewModel(ISession session, SceneCutService scenes, IMediaImagery? imagery = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scenes);
        _session = session;
        _scenes = scenes;
        _imagery = imagery;
        _imagery?.Changed += OnPicturesChanged;
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>True when the dialog is about a clip rather than a media item.</summary>
    public bool IsClip => _clip is not null;

    /// <summary>True when it is about a media item.</summary>
    public bool IsMedia => _clip is null;

    /// <summary>The window's title.</summary>
    public string Title => IsClip ? "Split at scene cuts" : "Find scene cuts";

    /// <summary>What is being looked at.</summary>
    public string TargetName { get; private set; } = string.Empty;

    /// <summary>The pictures across the strip, left to right; a null until one is ready.</summary>
    public ObservableCollection<ImageSource?> Tiles { get; } = [];

    /// <summary>The cuts found at the current threshold, across the strip.</summary>
    public ObservableCollection<SceneCutMark> Marks { get; } = [];

    /// <summary>Sets the dialog up for a clip or a media item and starts reading the video.</summary>
    /// <param name="mediaId">The media item, or the clip's.</param>
    /// <param name="clipId">The clip, or null for the media item itself.</param>
    public Task LoadAsync(string mediaId, string? clipId)
    {
        Project project = _session.Project;
        _clip = clipId is null ? null : project.FindClip(clipId)?.Clip;
        _item = project.MediaItem(mediaId);
        if (_item is null)
        {
            Status = "That media item is no longer in the project.";
            return Task.CompletedTask;
        }

        int? wanted = _clip is { } clip && _item.Info?.Streams.FirstOrDefault(stream => stream.Index == clip.SourceStreamIndex)?.Kind == MediaStreamKind.Video
            ? clip.SourceStreamIndex
            : null;
        _stream = _item.Info?.VideoStreams.FirstOrDefault(stream => wanted is null || stream.Index == wanted);
        (_from, _to) = _clip is { } range ? (range.SourceIn, range.SourceOut) : (_item.DefaultIn, _item.DefaultOut);
        TargetName = _clip is { } named && named.Name.Length > 0 ? named.Name : _item.Name;
        OnPropertyChanged(nameof(IsClip));
        OnPropertyChanged(nameof(IsMedia));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TargetName));

        for (int tile = 0; tile < TileCount; tile++)
        {
            Tiles.Add(null);
        }

        FillTiles();

        if (_stream is null)
        {
            Status = $"'{_item.Name}' has no picture, so it has no shots to find.";
            return Task.CompletedTask;
        }

        return ReadAsync(_item, _stream);
    }

    private async Task ReadAsync(MediaItem item, MediaStream stream)
    {
        if (_scenes.Cached(item.Hash, stream.Index) is { } known)
        {
            Show(known);
            return;
        }

        string path = ProjectPaths.Resolve(_session.ProjectPath, item.RelativePath);
        if (!System.IO.File.Exists(path))
        {
            Status = $"'{item.Name}' is not at {path}. Relink it first.";
            return;
        }

        IsReading = true;
        Status = "Reading the video...";
        var progress = new Progress<double>(done =>
        {
            Progress = done;
            Status = string.Create(CultureInfo.InvariantCulture, $"Reading the video... {done * 100:0}%");
        });

        try
        {
            SceneMeasurements measured = await Task.Run(() => _scenes.Measure(item, path, stream, progress, _stop.Token), _stop.Token).ConfigureAwait(true);
            Show(measured);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (JazzHands.Media.Interop.FfmpegException error)
        {
            Status = $"The video could not be read: {error.Message}";
        }
        finally
        {
            IsReading = false;
        }
    }

    private void Show(SceneMeasurements measured)
    {
        _measured = measured;
        Progress = 1;
        Status = string.Empty;
        Refresh();
        SplitCommand.NotifyCanExecuteChanged();
        MarkCommand.NotifyCanExecuteChanged();
        SubclipsCommand.NotifyCanExecuteChanged();
    }

    partial void OnThresholdChanged(double value) => Refresh();

    partial void OnMinShotSecondsChanged(double value) => Refresh();

    /// <summary>Finds the cuts again at the current settings, from what was measured.</summary>
    private void Refresh()
    {
        Marks.Clear();
        if (_measured is null || _to <= _from)
        {
            Found = string.Empty;
            return;
        }

        double span = (_to - _from).Value;
        Rational rate = _stream?.FrameRate is { IsZero: false } known ? known : Rational.Fps30;
        SceneCut[] inside = [.. _measured.Cuts(Math.Clamp(Threshold, 0, 100), MinShot).Where(cut => cut.Time > _from && cut.Time < _to)];
        foreach (SceneCut cut in inside)
        {
            string kind = cut.Kind == SceneCutKind.Dissolve ? "Dissolve" : "Cut";
            Marks.Add(new SceneCutMark(
                (cut.Time - _from).Value / span,
                cut.Kind == SceneCutKind.Dissolve,
                string.Create(CultureInfo.InvariantCulture, $"{kind} at {Timecode.Format(cut.Time, rate)} (score {cut.Score:0.0})")));
        }

        Found = inside.Length switch
        {
            0 => "No cuts at this threshold: one shot.",
            1 => "1 cut, 2 shots.",
            int many => string.Create(CultureInfo.InvariantCulture, $"{many} cuts, {many + 1} shots."),
        };
    }

    private Flicks MinShot => Flicks.FromSeconds(Math.Max(0, MinShotSeconds));

    private void FillTiles()
    {
        if (_imagery is null || _item is null || _to <= _from)
        {
            return;
        }

        int stream = _stream?.Index ?? 0;
        Flicks spacing = (_to - _from) / TileCount;
        if (spacing <= Flicks.Zero)
        {
            return;
        }

        IReadOnlyList<(Engine.Caching.ThumbnailImage Image, ImageSource Picture)> strip = _imagery.Strip(_item, stream, spacing, _from, _to);
        foreach ((Engine.Caching.ThumbnailImage image, ImageSource picture) in strip)
        {
            int tile = (int)Math.Clamp((image.Time - _from).Value / spacing.Value, 0, TileCount - 1);
            Tiles[tile] = picture;
        }
    }

    private void OnPicturesChanged(object? sender, EventArgs e) => FillTiles();

    private bool CanAct() => !IsReading && _measured is not null;

    /// <summary>Splits the clip (and its linked sound) at every cut: one undo.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SplitAsync() => _clip is { } clip
        ? RunAsync(new SplitClipAtCutsCommand(clip.Id, Threshold, MinShot))
        : Task.CompletedTask;

    /// <summary>Marks every cut on the clip, or on the file.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task MarkAsync() => RunAsync(new AddMarkersAtCutsCommand(_clip?.Id ?? _item!.Id, Threshold, MinShot));

    /// <summary>Makes a subclip of every shot, in a folder of the file's name.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task SubclipsAsync() => _item is { } item
        ? RunAsync(new SubclipsFromCutsCommand(item.Id, Threshold, MinShot))
        : Task.CompletedTask;

    private async Task RunAsync(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        if (!result.Ok)
        {
            Status = result.Error ?? "It did not work.";
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the reading, if it is still going, and closes.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!_disposed)
        {
            _stop.Cancel();
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Once only: the dialog disposes it when it closes, and the editor's services once did again
        // at exit, when cancelling a disposed source crashed the quit (2026-09-29).
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _stop.Dispose();
        _imagery?.Changed -= OnPicturesChanged;
    }
}
