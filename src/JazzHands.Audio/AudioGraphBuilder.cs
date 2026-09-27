using JazzHands.Audio.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;

namespace JazzHands.Audio;

/// <summary>
/// Turns a sequence into a <see cref="MixSnapshot"/> the audio thread can read.
/// </summary>
/// <remarks>
/// Runs on whatever thread noticed the project changed, never on the audio thread: it walks the
/// model, converts every time to samples and allocates freely, so that the audio thread does
/// none of that. Cheap enough to run on every change; a thirty clip project is a few hundred
/// small objects.
///
/// Only audio tracks are mixed. A movie's sound is on audio clips of its own, linked to the
/// picture, so that muting the microphone is a track operation rather than a checkbox hidden
/// inside a video clip.
///
/// Effects come from an <see cref="AudioEffectHost"/>, which keeps each effect's instance, and
/// so its state, across builds. A caller that rebuilds (the transport, after every edit) passes
/// the same host every time; one that builds once (an export) may pass none.
/// </remarks>
public static class AudioGraphBuilder
{
    /// <summary>Builds the mix for a sequence.</summary>
    /// <param name="project">The project, for its media and settings.</param>
    /// <param name="sequence">The sequence, or null for the active one.</param>
    /// <param name="effects">Where effect instances live between builds; a fresh one when null.</param>
    public static MixSnapshot Build(Project project, Sequence? sequence = null, AudioEffectHost? effects = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        sequence ??= project.ActiveSequence;
        ProjectSettings settings = sequence is null ? project.Settings : project.SettingsFor(sequence);
        int rate = settings.SampleRate;
        int channels = Math.Clamp(settings.ChannelCount, 1, Dsp.MaxChannels);

        if (sequence is null)
        {
            return MixSnapshot.Silent(rate, channels);
        }

        effects ??= new AudioEffectHost();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<TrackMix>();

        foreach (Track track in sequence.Tracks.OrderBy(track => track.Order))
        {
            if (track.Kind != TrackKind.Audio)
            {
                continue;
            }

            var clips = new List<ClipMix>();
            Dictionary<string, Joins> joins = Crossfades(project, track, settings.FrameRate, rate);
            foreach (Clip clip in track.Clips)
            {
                if ((Generated(clip, rate, channels, effects, used) ?? BuildClip(project, clip, rate, effects, used, joins.GetValueOrDefault(clip.Id))) is { } built)
                {
                    clips.Add(built);
                }
            }

            AudioEffectSlot[] chain = effects.Chain(track.Effects, rate, channels, used);
            double tail = chain.Length == 0 ? 0.0 : chain.Max(slot => slot.Effect.TailSeconds);
            tracks.Add(new TrackMix(
                track.Id,
                track.Name,
                track.Muted,
                track.Solo,
                ScalarCurve.From(track.Volume, 0.0f, rate),
                ScalarCurve.From(track.Pan, 0.0f, rate),
                clips,
                chain,
                (long)Math.Ceiling(tail * rate)));
        }

        effects.Retain(used);
        return new MixSnapshot(rate, channels, Keyed(tracks, channels), Master(sequence.Master, rate));
    }

    /// <summary>
    /// Connects effects that listen to another track (a ducker's key) to it, by id or else by name,
    /// and puts the tracks listened to first, so a listener hears the same block. A key that names
    /// nothing, or its own track, is left unconnected and the effect hears silence.
    /// </summary>
    private static List<TrackMix> Keyed(List<TrackMix> tracks, int channels)
    {
        var keys = new HashSet<TrackMix>();
        foreach (TrackMix track in tracks)
        {
            foreach (AudioEffectSlot slot in track.EffectArray)
            {
                if (slot.KeyName is not { } name)
                {
                    continue;
                }

                TrackMix? key = tracks.FirstOrDefault(candidate => string.Equals(candidate.Id, name, StringComparison.Ordinal))
                    ?? tracks.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (key is null || ReferenceEquals(key, track))
                {
                    continue;
                }

                slot.KeyTrack = key;
                key.KeyOut ??= new AudioBuffer(channels, AudioGraph.BlockSize);
                keys.Add(key);
            }
        }

        return keys.Count == 0 ? tracks : [.. tracks.Where(keys.Contains), .. tracks.Where(track => !keys.Contains(track))];
    }

    /// <summary>The master bus in samples: unity with the limiter on when the sequence says nothing.</summary>
    internal static MasterMix Master(MasterBus? master, int rate) => master is null
        ? MasterMix.Default
        : new MasterMix(ScalarCurve.From(master.Volume, 0.0f, rate), master.LimiterEnabled, double.IsNaN(master.CeilingDb) ? (float)MasterBus.DefaultCeiling : (float)Math.Clamp(master.CeilingDb, -24.0, 0.0));

    /// <summary>
    /// The mix form of one clip, or null when it has nothing to play.
    /// </summary>
    /// <remarks>
    /// A disabled clip, a freeze frame, a generator, a compound clip and a clip whose media or stream cannot be
    /// found are all silent. The last is not an error here: a project whose drive is unplugged
    /// still opens, and validation is where a missing file is reported.
    /// </remarks>
    internal static ClipMix? BuildClip(Project project, Clip clip, int rate, AudioEffectHost? effects = null, ISet<string>? used = null, Joins joins = default)
    {
        if (!clip.Enabled || clip.IsHold || clip.MediaId is not { } mediaId || clip.Duration <= Flicks.Zero)
        {
            return null;
        }

        MediaItem? item = project.MediaItem(mediaId);
        MediaStream? stream = item?.Info?.Streams.FirstOrDefault(
            candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio);

        if (stream is null)
        {
            return null;
        }

        int channels = Math.Clamp(stream.Channels <= 0 ? 2 : stream.Channels, 1, Dsp.MaxChannels);
        Rational speed = clip.EffectiveSpeed;
        long speedNum = speed.Num;
        long speedDen = speed.Den;
        long start = clip.Start.ToSamples(rate, RoundingMode.Nearest);
        long end = clip.End.ToSamples(rate, RoundingMode.Nearest);
        long[]? remap = null;

        if (clip.Remap is { } curve)
        {
            // A speed curve: the source position every RemapStep samples, over a fixed
            // denominator, and a speed bound that sizes the read-ahead window.
            const long Denominator = 1024;
            double fastest = Math.Max(Core.Animation.TimeRemap.Fastest(curve, clip.Duration), 1.0 / Denominator);
            // Kept apart rather than as a Rational, which would reduce the fixed denominator away.
            speedNum = (long)Math.Ceiling(fastest * Denominator);
            speedDen = Denominator;
            int entries = (int)((end - start) / ClipMix.RemapStep) + 2;
            remap = new long[entries];
            for (int index = 0; index < entries; index++)
            {
                var local = new Flicks((long)index * ClipMix.RemapStep * Flicks.PerSecond / rate);
                Flicks source = clip.SourceTimeAt(clip.Start + local);
                remap[index] = (long)Math.Round((double)source.Value * rate * Denominator / Flicks.PerSecond);
            }
        }

        long sourceIn = clip.SourceIn.ToSamples(rate, RoundingMode.Nearest);
        long sourceOut = clip.SourceOut.ToSamples(rate, RoundingMode.Nearest);
        var samples = new AudioSourceRef(mediaId, stream.Index, channels);

        // Phase 36: at a speed, a clip that keeps its pitch is stretched rather than read like tape;
        // past what the stretcher does well it plays like tape after all.
        double tempo = (double)speedNum / speedDen;
        if (clip.KeepsPitch && (remap is not null || (speedNum != speedDen && tempo >= StretchPlan.MinTempo && tempo <= StretchPlan.MaxTempo)))
        {
            samples = samples with { Stretch = new StretchPlan(sourceIn, sourceOut, speedNum, speedDen, clip.Reverse, remap) };
        }

        Fade fadeIn = clip.FadeIn ?? Fade.None;
        Fade fadeOut = clip.FadeOut ?? Fade.None;

        return new ClipMix(
            clip.Id,
            samples,
            start,
            end,
            sourceIn,
            sourceOut,
            speedNum,
            speedDen,
            clip.Reverse,
            fadeIn.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeIn.Curve,
            fadeOut.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeOut.Curve,
            ScalarCurve.From(clip.Volume, 0.0f, rate),
            ScalarCurve.From(clip.Pan, 0.0f, rate),
            clip.ChannelMap ?? AudioChannelMap.Auto,
            effects?.Chain(clip.Effects, rate, channels, used ?? new HashSet<string>(StringComparer.Ordinal)),
            joins.LeadIn,
            joins.Tail,
            joins.CrossIn,
            joins.CrossOut,
            remap);
    }

    /// <summary>
    /// The mix form of a sound generator's clip (a test tone, pink noise), made with the clip's own
    /// parameters, writing the mix's channels; null when the clip is not a sound generator's.
    /// </summary>
    internal static ClipMix? Generated(Clip clip, int rate, int channels, AudioEffectHost? effects = null, ISet<string>? used = null)
    {
        if (!clip.Enabled || clip.GeneratorId is not { } generatorId || clip.Duration <= Flicks.Zero
            || AudioEffects.Registry.Find(generatorId) is not { Kind: Core.Effects.EffectKind.AudioGenerator, Implementation: { } type } descriptor
            || !typeof(AudioGenerator).IsAssignableFrom(type))
        {
            return null;
        }

        Effect? own = clip.Effects.FirstOrDefault(effect => string.Equals(effect.TypeId, generatorId, StringComparison.Ordinal));
        var generator = (AudioGenerator)Activator.CreateInstance(type)!;
        generator.Configure(
            descriptor.Params.ToDictionary(
                parameter => parameter.Name,
                parameter => AudioEffectHost.Numeric(parameter, own?.Parameter(parameter.Name) is StaticValue { } set ? set.Value : parameter.Default),
                StringComparer.Ordinal),
            rate);

        Fade fadeIn = clip.FadeIn ?? Fade.None;
        Fade fadeOut = clip.FadeOut ?? Fade.None;
        EquatableArray<Effect> chain = EquatableArray.Create([.. clip.Effects.Where(effect => !ReferenceEquals(effect, own))]);
        return new ClipMix(
            clip.Id,
            new AudioSourceRef(generatorId, 0, channels),
            clip.Start.ToSamples(rate, RoundingMode.Nearest),
            clip.End.ToSamples(rate, RoundingMode.Nearest),
            clip.SourceIn.ToSamples(rate, RoundingMode.Nearest),
            clip.SourceOut.ToSamples(rate, RoundingMode.Nearest),
            fadeInLength: fadeIn.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeInCurve: fadeIn.Curve,
            fadeOutLength: fadeOut.Duration.ToSamples(rate, RoundingMode.Nearest),
            fadeOutCurve: fadeOut.Curve,
            volume: ScalarCurve.From(clip.Volume, 0.0f, rate),
            pan: ScalarCurve.From(clip.Pan, 0.0f, rate),
            effects: effects?.Chain(chain, rate, channels, used ?? new HashSet<string>(StringComparer.Ordinal)))
        {
            Generator = generator,
        };
    }

    /// <summary>
    /// What each clip of a track plays into its neighbours: the crossfades of the track's
    /// transitions, in samples, with the outgoing clip playing on past its end and the incoming one
    /// starting before its start, as far as their files have sound. Past that the crossfade goes on
    /// over silence.
    /// </summary>
    internal static Dictionary<string, Joins> Crossfades(Project project, Track track, Rational frameRate, int rate)
    {
        var joins = new Dictionary<string, Joins>(StringComparer.Ordinal);
        foreach (TransitionSpan span in TransitionTiming.Spans(track, frameRate))
        {
            long start = span.Range.Start.ToSamples(rate, RoundingMode.Nearest);
            long end = span.Range.End.ToSamples(rate, RoundingMode.Nearest);
            var crossfade = new Crossfade(start, end - start, JazzHands.Audio.Effects.Crossfades.CurveOf(span.Transition.TypeId));

            Flicks tail = Flicks.Min(span.After, TransitionTiming.HandleAfter(project, span.Left));
            Flicks leadIn = Flicks.Min(span.Before, TransitionTiming.HandleBefore(project, span.Right));

            Joins left = joins.GetValueOrDefault(span.Left.Id);
            joins[span.Left.Id] = left with { Tail = tail.ToSamples(rate, RoundingMode.Nearest), CrossOut = crossfade };

            Joins right = joins.GetValueOrDefault(span.Right.Id);
            joins[span.Right.Id] = right with { LeadIn = leadIn.ToSamples(rate, RoundingMode.Nearest), CrossIn = crossfade };
        }

        return joins;
    }

    /// <summary>How a clip plays into the transitions at its ends.</summary>
    /// <param name="LeadIn">Samples before its start it plays.</param>
    /// <param name="Tail">Samples after its end it plays.</param>
    /// <param name="CrossIn">The crossfade it comes in on.</param>
    /// <param name="CrossOut">The crossfade it goes out on.</param>
    internal readonly record struct Joins(long LeadIn, long Tail, Crossfade? CrossIn, Crossfade? CrossOut);
}
