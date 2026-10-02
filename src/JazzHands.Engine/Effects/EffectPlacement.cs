using System.Collections.Immutable;
using JazzHands.Audio.Effects;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Validation;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Sound effects that are where <c>effect.add</c> would not have put them: one that belongs on a
/// clip on a track, or one that belongs on a track on a clip. Only a hand edit of the project file
/// puts them there.
/// </summary>
/// <remarks>
/// Here rather than in Core's validator because only the audio layer knows which effects belong
/// where. A warning, not an error: the project loads and plays, and the warning says what is off.
/// </remarks>
public static class EffectPlacement
{
    /// <summary>A warning for every sound effect on the wrong kind of owner.</summary>
    public static ImmutableArray<ValidationIssue> Misplaced(Project project) => [.. Find(project).Select(found => found.Issue)];

    /// <summary>The same warnings, each with the effect it is about.</summary>
    public static ImmutableArray<(string EffectId, ValidationIssue Issue)> Find(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        EffectRegistry registry = EffectCatalog.Registry;
        var issues = ImmutableArray.CreateBuilder<(string EffectId, ValidationIssue Issue)>();
        for (int s = 0; s < project.Sequences.Length; s++)
        {
            Sequence sequence = project.Sequences[s];
            for (int t = 0; t < sequence.Tracks.Length; t++)
            {
                Track track = sequence.Tracks[t];
                string trackPath = $"/sequences/{s}/tracks/{t}";
                for (int e = 0; e < track.Effects.Length; e++)
                {
                    Effect effect = track.Effects[e];
                    if (Implementation(registry, effect) is { } type && typeof(IClipEffect).IsAssignableFrom(type))
                    {
                        // The mixer makes up a clip effect's delay by reading the clip's file that
                        // far ahead; a track is many clips mixed, with nothing to read ahead in.
                        int latency = Latency(type);
                        string late = latency > 0 ? FormattableString.Invariant($" It runs {latency * 1000.0 / 48000.0:0} ms late against the other tracks at 48 kHz.") : string.Empty;
                        issues.Add((effect.Id, new ValidationIssue(
                            Severity.Warning,
                            "clip-only-effect",
                            $"{trackPath}/effects/{e}",
                            $"'{Name(registry, effect)}' ({effect.Id}) is on track '{track.Name}', but it goes on a clip: it reads ahead in the clip's file to stay in time.{late} Put it on the track's clips instead.")));
                    }
                }

                for (int c = 0; c < track.Clips.Length; c++)
                {
                    Clip clip = track.Clips[c];
                    for (int e = 0; e < clip.Effects.Length; e++)
                    {
                        Effect effect = clip.Effects[e];
                        if (Implementation(registry, effect) is { } type && typeof(ITrackEffect).IsAssignableFrom(type))
                        {
                            issues.Add((effect.Id, new ValidationIssue(
                                Severity.Warning,
                                "track-only-effect",
                                $"{trackPath}/clips/{c}/effects/{e}",
                                $"'{Name(registry, effect)}' ({effect.Id}) is on clip '{clip.Name}' ({clip.Id}), but it goes on a track: it listens to another track. Put it on track '{track.Name}' instead.")));
                        }
                    }
                }
            }
        }

        return issues.ToImmutable();
    }

    private static Type? Implementation(EffectRegistry registry, Effect effect) =>
        registry.Find(effect.TypeId) is { Kind: EffectKind.Audio, Implementation: { } type } ? type : null;

    private static string Name(EffectRegistry registry, Effect effect) =>
        registry.Find(effect.TypeId)?.Name ?? effect.TypeId;

    /// <summary>The effect's delay in samples, from a fresh instance of it; 0 when it cannot be made.</summary>
    private static int Latency(Type type)
    {
        try
        {
            var effect = Activator.CreateInstance(type) as AudioEffect;
            return effect?.LatencySamples ?? 0;
        }
        catch (Exception error) when (error is MissingMethodException or System.Reflection.TargetInvocationException)
        {
            return 0;
        }
    }
}
