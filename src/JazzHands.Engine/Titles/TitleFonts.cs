using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Titles;
using JazzHands.Core.Validation;
using JazzHands.Render.Titles;

namespace JazzHands.Engine.Titles;

/// <summary>
/// Which titles ask for a font this machine does not have, for validation to warn about.
/// </summary>
/// <remarks>
/// Here rather than in Core's validator because only the render layer knows what is installed.
/// A missing font is a warning, never an error: the title is drawn in
/// <see cref="FontCatalog.Fallback"/> and the project loads, as a missing file loads offline.
/// </remarks>
public static class TitleFonts
{
    /// <summary>A warning for every title whose font, or a font its markup names, cannot be found.</summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">The project file, whose folder's <c>fonts</c> folder is looked in; empty for none.</param>
    public static ImmutableArray<ValidationIssue> Missing(Project project, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);

        string folder = projectPath is { Length: > 0 } ? Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty : string.Empty;
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Has(string family)
        {
            if (!known.TryGetValue(family, out bool has))
            {
                has = FontCatalog.Has(family, folder);
                known[family] = has;
            }

            return has;
        }

        for (int s = 0; s < project.Sequences.Length; s++)
        {
            Sequence sequence = project.Sequences[s];
            for (int t = 0; t < sequence.Tracks.Length; t++)
            {
                Track track = sequence.Tracks[t];
                for (int c = 0; c < track.Clips.Length; c++)
                {
                    Clip clip = track.Clips[c];
                    if (!string.Equals(clip.GeneratorId, TitleParams.GeneratorId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Effect? own = clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect));
                    var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    families.Add(own?.Parameter(TitleParams.Font) is StaticValue { Value: ParamValue.Text { Value: var font } } ? font : FontCatalog.Fallback);
                    foreach (string markup in Texts(own?.Parameter(TitleParams.Text)))
                    {
                        foreach (TitleSpan span in TitleMarkup.Parse(markup).Spans)
                        {
                            if (span.Style.Font is { } named)
                            {
                                families.Add(named);
                            }
                        }
                    }

                    foreach (string family in families.Where(family => !Has(family)))
                    {
                        issues.Add(new ValidationIssue(
                            Severity.Warning,
                            "missing-font",
                            $"/sequences/{s}/tracks/{t}/clips/{c}",
                            $"Title '{clip.Name}' ({clip.Id}) uses the font '{family}', which is not installed or in the project's fonts folder; it is drawn in {FontCatalog.Fallback}."));
                    }
                }
            }
        }

        return issues.ToImmutable();
    }

    private static IEnumerable<string> Texts(AnimatedValue? value) => value switch
    {
        StaticValue { Value: ParamValue.Text text } => [text.Value],
        KeyframedValue keyed => keyed.Keyframes.Select(key => key.Value).OfType<ParamValue.Text>().Select(text => text.Value),
        _ => [],
    };
}
