using System.Diagnostics;
using System.Globalization;
using JazzHands.Audio.Output;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Playback;
using JazzHands.Media.Import;

namespace JazzHands.Cli;

/// <summary>What "jazz perf audio" reports.</summary>
/// <param name="File">The media the project was built from.</param>
/// <param name="Device">The output it played to.</param>
/// <param name="Clips">How many clips the project had.</param>
/// <param name="Tracks">How many audio tracks.</param>
/// <param name="Seconds">How long it played, by the wall clock.</param>
/// <param name="FramesPlayed">What the device says it played.</param>
/// <param name="Underruns">Times the device ran dry. The criterion is zero.</param>
/// <param name="StarvedBlocks">Blocks mixed before their source was decoded.</param>
/// <param name="Loops">Times playback reached the end and seeked back to the start.</param>
/// <param name="ClockDriftMilliseconds">How far the device clock ended up from the wall clock.</param>
public sealed record AudioBenchmarkResult(
    string File,
    string Device,
    int Clips,
    int Tracks,
    double Seconds,
    long FramesPlayed,
    long Underruns,
    long StarvedBlocks,
    int Loops,
    double ClockDriftMilliseconds);

/// <summary>
/// Plays a many-clip project through the sound card for a while and counts the gaps.
/// </summary>
/// <remarks>
/// The project is built from one file: every audio stream gets a track, and each track is cut
/// into clips with different in points, gains and fades, offset from each other so no two tracks
/// cut at the same moment. When playback reaches the end it seeks back to the start, which puts
/// a seek and a cold cache into every loop rather than just a steady stream.
///
/// It plays at zero monitor volume unless asked not to: the mix and the device do all the same
/// work, and nobody has to listen to test tones for ten minutes.
/// </remarks>
public static class AudioBenchmark
{
    /// <summary>Builds the project and plays it.</summary>
    /// <param name="path">A media file with at least one audio stream.</param>
    /// <param name="clips">How many clips to cut, spread across the tracks.</param>
    /// <param name="duration">How long to play.</param>
    /// <param name="deviceId">A device, or null for the default.</param>
    /// <param name="audible">True to hear it.</param>
    /// <param name="progress">Where to report as it goes.</param>
    public static AudioBenchmarkResult Run(
        string path,
        int clips,
        TimeSpan duration,
        string? deviceId = null,
        bool audible = false,
        TextWriter? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clips);

        MediaItem item = new MediaImporter().Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
        MediaStream[] streams = [.. item.Info?.AudioStreams ?? []];

        if (streams.Length == 0)
        {
            throw new InvalidOperationException($"'{path}' has no audio to play.");
        }

        Project project = Build(item, streams, clips);
        Flicks end = project.ActiveSequence!.Duration;

        using var transport = new Transport(new WasapiOutput(project.Settings.SampleRate, project.Settings.ChannelCount, deviceId));
        transport.MonitorGain = audible ? 1.0f : 0.0f;
        transport.Load(project);

        var watch = Stopwatch.StartNew();
        long framesAtStart = transport.Output.FramesPlayed;
        transport.Play();

        int loops = 0;
        TimeSpan nextReport = TimeSpan.FromSeconds(30);

        while (watch.Elapsed < duration)
        {
            Thread.Sleep(50);

            if (transport.Position >= end)
            {
                transport.Seek(Flicks.Zero);
                loops++;
            }

            if (progress is not null && watch.Elapsed >= nextReport)
            {
                progress.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{watch.Elapsed:mm\\:ss}  underruns {transport.Underruns}, starved {transport.StarvedBlocks}, loops {loops}"));
                nextReport += TimeSpan.FromSeconds(30);
            }
        }

        double seconds = watch.Elapsed.TotalSeconds;
        long played = transport.Output.FramesPlayed - framesAtStart;
        transport.Pause();

        return new AudioBenchmarkResult(
            Path.GetFileName(path),
            transport.Output.DeviceName,
            project.ActiveSequence.Tracks.Sum(track => track.Clips.Length),
            project.ActiveSequence.Tracks.Count(track => track.IsAudio),
            seconds,
            played,
            transport.Underruns,
            transport.StarvedBlocks,
            loops,
            ((double)played / project.Settings.SampleRate - seconds) * 1000.0);
    }

    /// <summary>A human summary.</summary>
    public static string Describe(AudioBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            {result.File}: {result.Clips} clips on {result.Tracks} tracks, played to {result.Device}
              {result.Seconds:F1} s, {result.FramesPlayed} frames, {result.Loops} loop(s)
              underruns       {result.Underruns}
              starved blocks  {result.StarvedBlocks}
              clock drift     {result.ClockDriftMilliseconds:F1} ms against the wall clock
            {(result.Underruns == 0 ? "No gaps." : "The device ran dry; see the log for when.")}
            """);
    }

    /// <summary>Cuts the file into clips across one track per stream.</summary>
    private static Project Build(MediaItem item, MediaStream[] streams, int clips)
    {
        Flicks length = item.Duration;
        Flicks clipLength = Flicks.Max(Flicks.FromMilliseconds(500), new Flicks(length.Value * 3 / 4));
        Flicks slack = length - clipLength;
        var tracks = new List<Track>();

        for (int lane = 0; lane < streams.Length; lane++)
        {
            MediaStream stream = streams[lane];
            var laneClips = new List<Clip>();

            // Tracks start at different times so their cuts do not line up.
            Flicks at = Flicks.FromMilliseconds(lane * 1300L);

            for (int index = lane; index < clips; index += streams.Length)
            {
                Flicks sourceIn = slack.IsZero ? Flicks.Zero : new Flicks(slack.Value * (index % 4) / 3);

                laneClips.Add(new Clip(
                    Id.New(),
                    new TimeRange(at, clipLength),
                    sourceIn,
                    MediaId: item.Id,
                    SourceStreamIndex: stream.Index,
                    Volume: AnimatedValue.Constant(-(index % 5) * 2.0f),
                    FadeIn: new Fade(Flicks.FromMilliseconds(200), Interp.EaseInOut),
                    FadeOut: new Fade(Flicks.FromMilliseconds(200)),
                    Name: $"{stream.Title ?? "A"} {index}"));

                at += clipLength;
            }

            tracks.Add(new Track(Id.New(), TrackKind.Audio, stream.Title ?? $"A{lane + 1}", lane, new EquatableArray<Clip>(laneClips)));
        }

        Project project = Project.CreateNew("perf audio") with { Media = EquatableArray.Create(item) };
        return project with
        {
            Sequences = EquatableArray.Create(project.Sequences[0] with { Tracks = new EquatableArray<Track>(tracks) }),
        };
    }
}
