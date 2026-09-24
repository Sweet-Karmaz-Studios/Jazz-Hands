using System.Numerics;
using JazzHands.Core.Model;

namespace JazzHands.Engine.Effects;

/// <summary>
/// The looks every project has: effect presets that ship with the editor, as starting points and
/// as examples of what the colour effects do together.
/// </summary>
/// <remarks>
/// They are found after the project's own presets, so a project preset of the same name wins, and
/// they cannot be removed. Applying one copies its effects onto a clip with new ids, as any preset
/// does; from then on they are the clip's to change. Their ids start with <c>look.</c>.
/// </remarks>
public static class Looks
{
    /// <summary>The built-in looks.</summary>
    public static IReadOnlyList<EffectPreset> All { get; } =
    [
        new EffectPreset(
            "look.cinematic-teal-orange",
            "Cinematic Teal/Orange",
            EquatableArray.Create(
                Effect.Create("color.wheels")
                    .With("lift", new ParamValue.Float4(new Vector4(-0.02f, 0.01f, 0.035f, -0.01f)))
                    .With("gain", new ParamValue.Float4(new Vector4(0.06f, 0.015f, -0.05f, 0)))
                    .With("saturation", new ParamValue.Float(1.1f))
                    .With("contrast", new ParamValue.Float(1.1f)),
                Effect.Create("color.curves")
                    .With("master", new ParamValue.Text("0,0 0.25,0.21 0.75,0.8 1,1")))),
        new EffectPreset(
            "look.bleach-bypass",
            "Bleach Bypass",
            EquatableArray.Create(
                Effect.Create("color.basic")
                    .With("contrast", new ParamValue.Float(1.35f))
                    .With("saturation", new ParamValue.Float(0.45f)),
                Effect.Create("color.curves")
                    .With("master", new ParamValue.Text("0,0.02 0.25,0.2 0.75,0.83 1,0.98")))),
        new EffectPreset(
            "look.game-capture-punch",
            "Game Capture Punch",
            EquatableArray.Create(
                Effect.Create("color.basic")
                    .With("contrast", new ParamValue.Float(1.15f))
                    .With("saturation", new ParamValue.Float(1.05f))
                    .With("vibrance", new ParamValue.Float(0.35f)),
                Effect.Create("video.sharpen")
                    .With("amount", new ParamValue.Float(60))
                    .With("radius", new ParamValue.Float(1.5f)))),
    ];

    /// <summary>A built-in look by id or by name, ignoring case; null when there is none.</summary>
    public static EffectPreset? Find(string idOrName) =>
        All.FirstOrDefault(look => string.Equals(look.Id, idOrName, StringComparison.Ordinal))
        ?? All.FirstOrDefault(look => string.Equals(look.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    private static Effect With(this Effect effect, string name, ParamValue value) =>
        effect.WithParameter(name, AnimatedValue.Constant(value));
}
