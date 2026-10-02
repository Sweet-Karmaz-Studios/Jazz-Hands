using System.Globalization;
using JazzHands.Audio.Analysis;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Audio;
using JazzHands.Engine.Commands;
using JazzHands.Media.Audio;

namespace JazzHands.Engine.Handlers;

/// <summary>What the noise commands share: the effect, and a clip's sound read for it.</summary>
internal static class Noise
{
    /// <summary>The noise reduction effect's type.</summary>
    public const string Denoise = "audio.denoise";

    /// <summary>The rate noise is learned at.</summary>
    public const int Rate = 48_000;

    /// <summary>The rate a clip is scanned at for its quietest stretch.</summary>
    private const int ScanRate = 8_000;

    /// <summary>How far into a clip the scan for its quietest stretch looks.</summary>
    private static readonly Flicks ScanLength = Flicks.FromSeconds(1200);

    /// <summary>How long a stretch of noise is learned from when none is given.</summary>
    private static readonly Flicks Quiet = Flicks.FromMilliseconds(500);

    /// <summary>The sound clips a clip stands for: itself, or the sound linked to a picture.</summary>
    public static IReadOnlyList<string> SoundClips(Project project, string clipId) =>
        AudioSubject.Resolve(project, clipId, null, mix: false, null).ClipIds;

    /// <summary>A clip with its noise reduction changed, added when it has none.</summary>
    public static Clip WithDenoise(Clip clip, Func<Effect, Effect> change, HandlerContext context)
    {
        Effect? existing = clip.Effects.FirstOrDefault(effect => effect.TypeId == Denoise);
        Effect updated = change(existing ?? Effect.Create(Denoise));
        context.Changed([clip.Id, updated.Id]);
        return clip with
        {
            Effects = existing is null
                ? clip.Effects.Add(updated)
                : EquatableArray.Create([.. clip.Effects.Select(effect => effect.Id == existing.Id ? updated : effect)]),
        };
    }

    /// <summary>
    /// The stretch of a sound clip to learn its noise from, as source times: the one given, or the
    /// quietest half second of the clip's first twenty minutes that is not digital silence.
    /// </summary>
    public static (Flicks From, Flicks Length) Stretch(Clip clip, string path, int stream, Flicks? from, Flicks? to)
    {
        if (from is not null || to is not null)
        {
            Flicks start = from ?? clip.Start;
            Flicks end = to ?? clip.End;
            if (start < clip.Start || end > clip.End || end - start < Flicks.FromMilliseconds(100))
            {
                throw new CommandException(
                    "time-out-of-range",
                    $"The stretch of noise is at least a tenth of a second inside the clip, which runs from {Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}.");
            }

            return (clip.SourceIn + (start - clip.Start), end - start);
        }

        Flicks length = clip.SourceDuration < ScanLength ? clip.SourceDuration : ScanLength;
        float[] scan = MonoReader.Read(path, stream, clip.SourceIn, length, ScanRate);
        int window = (int)Quiet.ToSamples(ScanRate, RoundingMode.Nearest);
        if (scan.Length < window)
        {
            throw new CommandException("too-short", "The clip is shorter than half a second; give --from and --to over a stretch of only noise.");
        }

        // The quietest window in steps of a tenth of it, not counting digital silence (-90 dBFS).
        double quietest = double.MaxValue;
        int best = -1;
        for (int at = 0; at + window <= scan.Length; at += window / 10)
        {
            double sum = 0;
            foreach (float sample in scan.AsSpan(at, window))
            {
                sum += (double)sample * sample;
            }

            double meanSquare = sum / window;
            if (meanSquare > 1e-9 && meanSquare < quietest)
            {
                (quietest, best) = (meanSquare, at);
            }
        }

        if (best < 0)
        {
            throw new CommandException("silent", "The clip is silent, so there is no noise to learn.");
        }

        return (clip.SourceIn + Flicks.FromSeconds((double)best / ScanRate), Quiet);
    }

    /// <summary>A sound clip's file and stream, checked to play at normal speed and to be there.</summary>
    public static (string Path, int Stream) Source(Project project, Clip clip, string projectPath)
    {
        if (clip.Reverse || clip.IsRemapped || clip.IsHold || clip.EffectiveSpeed != Rational.One)
        {
            throw new CommandException("not-plain", $"'{clip.Name}' plays at another speed; noise is learned from a clip playing forwards at normal speed.");
        }

        MediaItem item = MediaServices.Require(project, clip.MediaId ?? throw new CommandException("no-sound", $"'{clip.Name}' has no sound."));
        MediaStream stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio)
            ?? throw new CommandException("no-sound", $"'{item.Name}' has no sound.");
        string path = HandlerHelp.Resolve(projectPath, item.RelativePath);
        return File.Exists(path) ? (path, stream.Index) : throw new CommandException("media-missing", $"'{item.Name}' is not at {path}. Relink it first.");
    }
}

/// <summary>Learns a clip's noise and puts it on its noise reduction.</summary>
/// <remarks>The sound is read and its noise learned before the command is queued.</remarks>
public sealed class LearnNoiseHandler : ICommandHandler<LearnNoiseCommand>, IPreparingHandler<LearnNoiseCommand>
{
    /// <inheritdoc />
    public object? Prepare(Project project, LearnNoiseCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return new PreparedWork(Key(project, command), Learn(project, command, context.ProjectPath));
    }

    /// <inheritdoc />
    public Project Handle(Project project, LearnNoiseCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Dictionary<string, double[]> profiles = PreparedWork.Reuse(context, Key(project, command), () => Learn(project, command, context.ProjectPath));
        foreach ((string id, double[] profile) in profiles)
        {
            ClipLocation found = HandlerHelp.Clip(project, id);
            HandlerHelp.RequireUnlocked(found.Track);
            Clip learned = Noise.WithDenoise(found.Clip, effect => effect.WithParameter("profile", AnimatedValue.Constant(new ParamValue.Text(NoiseProfile.Format(profile)))), context);
            project = project.ReplaceTrack(found.Track.ReplaceClip(learned));
        }

        return project;
    }

    /// <summary>What the noise is learned from: the clip named and every sound clip learned with it, and their files.</summary>
    private static (EquatableArray<Clip>, EquatableArray<MediaItem>) Key(Project project, LearnNoiseCommand command)
    {
        Clip[] clips = [HandlerHelp.Clip(project, command.ClipId).Clip, .. Noise.SoundClips(project, command.ClipId).Select(id => HandlerHelp.Clip(project, id).Clip)];
        return ([.. clips], [.. clips.Select(clip => clip.MediaId is { } media ? project.MediaItem(media) : null).OfType<MediaItem>()]);
    }

    /// <summary>Each sound clip's noise profile, learned from its file.</summary>
    private static Dictionary<string, double[]> Learn(Project project, LearnNoiseCommand command, string projectPath)
    {
        var profiles = new Dictionary<string, double[]>(StringComparer.Ordinal);
        ClipLocation given = HandlerHelp.Clip(project, command.ClipId);
        foreach (string id in Noise.SoundClips(project, command.ClipId))
        {
            ClipLocation found = HandlerHelp.Clip(project, id);
            HandlerHelp.RequireUnlocked(found.Track);
            (string path, int stream) = Noise.Source(project, found.Clip, projectPath);

            // The stretch is given on the sequence, against the clip that was named: its linked
            // sound is learned over the same moments.
            Flicks shift = found.Clip.Start - given.Clip.Start;
            (Flicks from, Flicks length) = Noise.Stretch(found.Clip, path, stream, command.From + shift, command.To + shift);
            float[] samples = MonoReader.Read(path, stream, from, length, Noise.Rate);
            profiles[id] = NoiseProfile.Learn(samples, Noise.Rate)
                ?? throw new CommandException("too-short", "The stretch of noise is too short to learn from.");
        }

        return profiles;
    }
}

/// <summary>Turns a clip's noise reduction on, changes it, or takes it off.</summary>
public sealed class ReduceNoiseHandler : ICommandHandler<ReduceNoiseCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ReduceNoiseCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.Reduction is < 0 or > 40 || command.Sensitivity is < 0.5 or > 4)
        {
            throw new CommandException("invalid-value", string.Create(CultureInfo.InvariantCulture, $"The reduction is 0 to 40 dB and the sensitivity 0.5 to 4."));
        }

        foreach (string id in Noise.SoundClips(project, command.ClipId))
        {
            ClipLocation found = HandlerHelp.Clip(project, id);
            HandlerHelp.RequireUnlocked(found.Track);
            Clip clip = found.Clip;

            if (command.Off)
            {
                if (!clip.Effects.Any(effect => effect.TypeId == Noise.Denoise))
                {
                    throw new CommandException("not-reduced", $"'{clip.Name}' has no noise reduction.");
                }

                context.Changed(clip.Id);
                clip = clip with { Effects = EquatableArray.Create([.. clip.Effects.Where(effect => effect.TypeId != Noise.Denoise)]) };
            }
            else
            {
                clip = Noise.WithDenoise(clip, effect =>
                {
                    Effect changed = command.Reduction is { } reduction ? effect.WithParameter("reduction", AnimatedValue.Constant((float)reduction)) : effect;
                    return command.Sensitivity is { } sensitivity ? changed.WithParameter("sensitivity", AnimatedValue.Constant((float)sensitivity)) : changed;
                }, context);
            }

            project = project.ReplaceTrack(found.Track.ReplaceClip(clip));
        }

        return project;
    }
}
