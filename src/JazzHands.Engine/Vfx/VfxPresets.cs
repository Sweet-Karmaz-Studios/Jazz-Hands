namespace JazzHands.Engine.Vfx;

/// <summary>One effect of a preset: its settings, and keyframes timed from the moment of the hit.</summary>
/// <param name="TypeId">The effect type.</param>
/// <param name="Values">Parameters set to a value, as the command line writes them.</param>
/// <param name="Keys">Parameters keyframed, each key's time in seconds from the moment (negative before it).</param>
internal sealed record PresetEffect(
    string TypeId,
    (string Name, string Value)[] Values,
    (string Name, (double After, string Value)[] Keys)[]? Keys = null);

/// <summary>A combination of effects timed to one moment.</summary>
/// <param name="Name">What <c>vfx.apply-preset</c> takes.</param>
/// <param name="Title">The adjustment clip's name.</param>
/// <param name="Description">What it looks like, for the tool description.</param>
/// <param name="Before">Seconds its adjustment clip starts before the moment.</param>
/// <param name="After">Seconds it lasts after.</param>
/// <param name="Effects">The effects, in chain order.</param>
internal sealed record VfxPreset(string Name, string Title, string Description, double Before, double After, PresetEffect[] Effects);

/// <summary>The built-in impact presets.</summary>
/// <remarks>
/// Each is the impact kit's effects with their triggers on the moment. A chromatic split is
/// keyframed rather than triggered, zero a moment before and after, so it bursts on the hit.
/// </remarks>
internal static class VfxPresets
{
    public static readonly VfxPreset[] All =
    [
        new("impact.hit", "Hit", "A light hit: a quick zoom in, a short rattle and a flash of light. Footsteps landing, a sword connecting, a cut on the beat.", 0.1, 0.6,
        [
            new("video.zoom-punch", [("amount", "1.08"), ("attack", "0.04"), ("settle", "0.3"), ("curve", "smooth")]),
            new("video.shake", [("amplitude", "14"), ("frequency", "18"), ("rotation", "0.8"), ("decay", "9")]),
            new("video.flash", [("strength", "0.55"), ("hold", "0.02"), ("decay", "0.15"), ("mode", "add")]),
        ]),
        new("impact.heavy", "Heavy hit", "A heavy hit: two inverted impact frames, a punch in that overshoots, a hard shake, a burst of colour fringing and a white flash. A finishing blow, an explosion, a landing.", 0.1, 1.2,
        [
            new("video.impact-frame", [("style", "inverted-threshold"), ("frames", "2")]),
            new("video.zoom-punch", [("amount", "1.15"), ("attack", "0.05"), ("settle", "0.5"), ("curve", "back")]),
            new("video.shake", [("amplitude", "36"), ("frequency", "14"), ("rotation", "2.5"), ("decay", "4")]),
            new("video.chromatic-aberration", [], [("amount", [(-0.04, "0"), (0, "14"), (0.35, "0")])]),
            new("video.flash", [("strength", "0.9"), ("hold", "0.04"), ("decay", "0.3")]),
        ]),
        new("impact.boss-intro", "Boss intro", "A boss entrance: four red stark frames, a slow springing zoom, a long rumbling shake, heavy fringing and a warm flash. Put a title card on after it.", 0.2, 2.5,
        [
            new("video.impact-frame", [("style", "threshold"), ("colour", "#FF3030"), ("frames", "4")]),
            new("video.zoom-punch", [("amount", "1.25"), ("attack", "0.08"), ("settle", "1.4"), ("curve", "elastic")]),
            new("video.shake", [("amplitude", "55"), ("frequency", "9"), ("rotation", "3"), ("decay", "1.8")]),
            new("video.chromatic-aberration", [], [("amount", [(-0.04, "0"), (0, "20"), (0.8, "0")])]),
            new("video.flash", [("colour", "#FFE0D0"), ("strength", "1"), ("hold", "0.06"), ("decay", "0.6")]),
        ]),
    ];

    /// <summary>The preset with a name, or null.</summary>
    public static VfxPreset? Find(string name) =>
        All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
}
