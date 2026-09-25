using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Effects;

/// <summary>What owns a parameter.</summary>
public enum ParamOwnerKind
{
    /// <summary>A clip's own transform, opacity, crop, volume and pan.</summary>
    Clip,

    /// <summary>A track's volume and pan.</summary>
    Track,

    /// <summary>An effect on a clip or a track.</summary>
    Effect,

    /// <summary>A mask on a clip.</summary>
    Mask,

    /// <summary>A transition between two clips.</summary>
    Transition,
}

/// <summary>
/// Something with parameters, found by its identifier, with where it lives.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Id">Its identifier.</param>
/// <param name="Sequence">The sequence it is in.</param>
/// <param name="Track">The track it is on, or that is it.</param>
/// <param name="Clip">The clip it is on, or that is it; null for a track or a track's effect.</param>
/// <param name="Effect">The effect, for an effect.</param>
/// <param name="Mask">The mask, for a mask.</param>
/// <param name="Transition">The transition, for a transition.</param>
public sealed record ParamOwner(
    ParamOwnerKind Kind,
    string Id,
    Sequence Sequence,
    Track Track,
    Clip? Clip = null,
    Effect? Effect = null,
    Mask? Mask = null,
    Transition? Transition = null)
{
    /// <summary>
    /// Where keyframe time zero is on the sequence: a clip's start for anything on a clip, the
    /// sequence start for a track and its effects. A keyframe moves with its clip.
    /// </summary>
    public Flicks Origin => Clip?.Start ?? Flicks.Zero;

    /// <summary>How long the owner lasts from its origin, which keyframes are expected to stay inside.</summary>
    public Flicks Length => Transition?.Duration ?? Clip?.Duration ?? Sequence.Duration;

    /// <summary>True for an owner that carries sound rather than a picture.</summary>
    public bool IsAudio => Track.Kind == TrackKind.Audio;
}

/// <summary>
/// Reads and writes any parameter by its owner's identifier and its name.
/// </summary>
/// <remarks>
/// Identifiers are unique across a project, so an owner is named by its id alone:
/// <c>jazz keyframe add &lt;id&gt; radius</c> works the same for a clip's opacity, a blur's
/// radius, a track's volume and a mask's feather. A clip's own parameters have dotted names for
/// the inspector section they sit in: <c>transform.position</c>, <c>crop.left</c>, <c>opacity</c>,
/// <c>volume</c>, <c>pan</c>. This is the only code that knows where each of them is stored; every
/// command and the inspector go through it.
/// </remarks>
public static class ParamTargets
{
    /// <summary>A clip's picture: position, scale, rotation and anchor.</summary>
    public static EffectDescriptor Transform { get; } = Section("intrinsic.transform", "Transform",
        new ParamDescriptor("transform.position", ParamType.Point, new ParamValue.Float2(0, 0), "Position", "Offset from the frame centre, in sequence pixels.", Unit: "px"),
        new ParamDescriptor("transform.scale", ParamType.Float2, new ParamValue.Float2(1, 1), "Scale", "Multiplier per axis; 1 is the fitted size, a negative flips.", SliderMax: 4),
        new ParamDescriptor("transform.rotation", ParamType.Float, new ParamValue.Float(0), "Rotation", "Degrees clockwise.", Min: -36000, Max: 36000, SliderMax: 360, Unit: "deg"),
        new ParamDescriptor("transform.anchor", ParamType.Float2, new ParamValue.Float2(0, 0), "Anchor", "The pivot, in picture pixels from the picture's centre.", Unit: "px"));

    /// <summary>A clip's opacity.</summary>
    public static EffectDescriptor Opacity { get; } = Section("intrinsic.opacity", "Opacity",
        new ParamDescriptor("opacity", ParamType.Float, new ParamValue.Float(1), "Opacity", "0 is invisible, 1 fully opaque.", Min: 0, Max: 1));

    /// <summary>A clip's crop, per side.</summary>
    public static EffectDescriptor Crop { get; } = Section("intrinsic.crop", "Crop",
        new ParamDescriptor("crop.left", ParamType.Float, new ParamValue.Float(0), "Left", "Percent of the width taken off the left.", Min: 0, Max: 100, Unit: "%"),
        new ParamDescriptor("crop.top", ParamType.Float, new ParamValue.Float(0), "Top", "Percent of the height taken off the top.", Min: 0, Max: 100, Unit: "%"),
        new ParamDescriptor("crop.right", ParamType.Float, new ParamValue.Float(0), "Right", "Percent of the width taken off the right.", Min: 0, Max: 100, Unit: "%"),
        new ParamDescriptor("crop.bottom", ParamType.Float, new ParamValue.Float(0), "Bottom", "Percent of the height taken off the bottom.", Min: 0, Max: 100, Unit: "%"));

    /// <summary>A clip's or a track's volume and pan.</summary>
    public static EffectDescriptor Audio { get; } = Section("intrinsic.audio", "Audio",
        new ParamDescriptor("volume", ParamType.Float, new ParamValue.Float(0), "Volume", "Gain in decibels; -144 is silence.", Min: -144, Max: 24, SliderMax: 12, Unit: "dB"),
        new ParamDescriptor("pan", ParamType.Float, new ParamValue.Float(0), "Pan", "-1 hard left, 1 hard right.", Min: -1, Max: 1));

    /// <summary>A media clip's time remap: its speed as a curve over clip time (Phase 29).</summary>
    public static EffectDescriptor RemapParams { get; } = Section("intrinsic.remap", "Speed",
        new ParamDescriptor(Animation.TimeRemap.Parameter, ParamType.Float, new ParamValue.Float(1), "Speed", "How fast the source plays at each moment: 1 normal, 0.5 half, 0 a held frame. Keyframe it for a speed ramp.", Min: 0, Max: 100, SliderMax: 4, Unit: "x"));

    /// <summary>A mask's shape and strength.</summary>
    public static EffectDescriptor MaskParams { get; } = Section("intrinsic.mask", "Mask",
        new ParamDescriptor("bounds", ParamType.Float4, new ParamValue.Float4(Vector4.Zero), "Bounds", "x, y, width and height of a rectangle or ellipse, in source pixels."),
        new ParamDescriptor("path", ParamType.Path, new ParamValue.Path(string.Empty), "Path", "The outline of a polygon or bezier mask, SVG path syntax, in source pixels."),
        new ParamDescriptor("feather", ParamType.Float, new ParamValue.Float(0), "Feather", "Softening of the edge, in sequence pixels.", Min: 0, Max: 1000, SliderMax: 200, Unit: "px"),
        new ParamDescriptor("opacity", ParamType.Float, new ParamValue.Float(1), "Opacity", "How strongly it applies, 0 to 1.", Min: 0, Max: 1),
        new ParamDescriptor("expansion", ParamType.Float, new ParamValue.Float(0), "Expansion", "Grows the shape outwards by this many sequence pixels, or shrinks it when negative.", Min: -1000, Max: 1000, SliderMax: 100, Unit: "px"));

    /// <summary>Finds whatever has an identifier, or null.</summary>
    public static ParamOwner? Find(Project project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                if (Is(track.Id, id))
                {
                    return new ParamOwner(ParamOwnerKind.Track, id, sequence, track);
                }

                foreach (Transition transition in track.Transitions)
                {
                    if (Is(transition.Id, id))
                    {
                        return new ParamOwner(ParamOwnerKind.Transition, id, sequence, track, Transition: transition);
                    }
                }

                foreach (Effect effect in track.Effects)
                {
                    if (Is(effect.Id, id))
                    {
                        return new ParamOwner(ParamOwnerKind.Effect, id, sequence, track, Effect: effect);
                    }

                    if (MaskOf(effect.Masks, id) is { } mask)
                    {
                        return new ParamOwner(ParamOwnerKind.Mask, id, sequence, track, Effect: effect, Mask: mask);
                    }
                }

                foreach (Clip clip in track.Clips)
                {
                    if (Is(clip.Id, id))
                    {
                        return new ParamOwner(ParamOwnerKind.Clip, id, sequence, track, clip);
                    }

                    foreach (Effect effect in clip.Effects)
                    {
                        if (Is(effect.Id, id))
                        {
                            return new ParamOwner(ParamOwnerKind.Effect, id, sequence, track, clip, effect);
                        }

                        if (MaskOf(effect.Masks, id) is { } effectMask)
                        {
                            return new ParamOwner(ParamOwnerKind.Mask, id, sequence, track, clip, effect, effectMask);
                        }
                    }

                    if (MaskOf(clip.Masks, id) is { } mask)
                    {
                        return new ParamOwner(ParamOwnerKind.Mask, id, sequence, track, clip, Mask: mask);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The sections an owner's parameters are shown in, in inspector order. A clip's picture
    /// sections, its sound, a track's sound, an effect's own descriptor or a mask's.
    /// </summary>
    public static ImmutableArray<EffectDescriptor> Sections(ParamOwner owner, EffectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(registry);

        return owner.Kind switch
        {
            // A generator's own parameters (a solid's colour) come first, as what the clip is.
            ParamOwnerKind.Clip when owner.Track.Kind is TrackKind.Video or TrackKind.Adjustment =>
                registry.Find(owner.Clip!.GeneratorId) is { Kind: EffectKind.Generator } generator
                    ? [generator, Transform, Opacity, Crop]
                    : owner.Clip.IsMedia && owner.Clip.IsRemapped ? [Transform, Opacity, Crop, RemapParams] : [Transform, Opacity, Crop],
            ParamOwnerKind.Clip when owner.Track.Kind == TrackKind.Audio =>
                registry.Find(owner.Clip!.GeneratorId) is { Kind: EffectKind.AudioGenerator } sound
                    ? [sound, Audio]
                    : owner.Clip.IsRemapped ? [Audio, RemapParams] : [Audio],
            ParamOwnerKind.Track when owner.Track.Kind == TrackKind.Audio => [Audio],
            ParamOwnerKind.Effect => registry.Find(owner.Effect!.TypeId) is { } descriptor ? [descriptor] : [],
            ParamOwnerKind.Mask => [MaskParams],
            ParamOwnerKind.Transition => registry.Find(owner.Transition!.TypeId) is { } transition ? [transition] : [],
            _ => [],
        };
    }

    /// <summary>Every parameter an owner has, in inspector order.</summary>
    public static ImmutableArray<ParamDescriptor> Params(ParamOwner owner, EffectRegistry registry) =>
        [.. Sections(owner, registry).SelectMany(section => section.Params)];

    /// <summary>One parameter of an owner, or null when it has none by that name.</summary>
    public static ParamDescriptor? Param(ParamOwner owner, string name, EffectRegistry registry)
    {
        foreach (EffectDescriptor section in Sections(owner, registry))
        {
            if (section.Param(name) is { } parameter)
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>What the project holds for a parameter, or null when it is at its default.</summary>
    public static AnimatedValue? Get(ParamOwner owner, string name)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return owner.Kind switch
        {
            ParamOwnerKind.Effect => owner.Effect!.Parameter(name),
            ParamOwnerKind.Transition => owner.Transition!.Parameter(name),
            ParamOwnerKind.Mask => name switch
            {
                "bounds" => owner.Mask!.Bounds,
                "path" => owner.Mask!.PathData,
                "feather" => owner.Mask!.Feather,
                "opacity" => owner.Mask!.Opacity,
                "expansion" => owner.Mask!.Expansion,
                _ => null,
            },
            ParamOwnerKind.Track => name switch
            {
                "volume" => owner.Track.Volume,
                "pan" => owner.Track.Pan,
                _ => null,
            },
            _ => ClipValue(owner.Clip!, name),
        };
    }

    /// <summary>
    /// A project with one parameter changed. Null, or a constant equal to the default, puts the
    /// parameter back to its default and stores nothing, so the file reads as if nobody had
    /// touched it.
    /// </summary>
    public static Project Set(Project project, ParamOwner owner, ParamDescriptor descriptor, AnimatedValue? value)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (value is StaticValue constant && ParamValues.Coerce(descriptor, constant.Value) == descriptor.Default)
        {
            value = null;
        }

        string name = descriptor.Name;

        switch (owner.Kind)
        {
            case ParamOwnerKind.Track:
                Track track = name == "volume" ? owner.Track with { Volume = value } : owner.Track with { Pan = value };
                return project.ReplaceTrack(track);

            case ParamOwnerKind.Effect:
                Effect effect = owner.Effect!;
                Effect changed = value is null
                    ? effect with { Parameters = new EquatableArray<EffectParameter>(effect.Parameters.Where(parameter => !Is(parameter.Name, name))) }
                    : effect.WithParameter(name, value);
                return ReplaceEffect(project, owner, changed);

            case ParamOwnerKind.Transition:
                Transition transition = owner.Transition!;
                EquatableArray<EffectParameter> parameters = value is null
                    ? new EquatableArray<EffectParameter>(transition.Parameters.Where(parameter => !Is(parameter.Name, name)))
                    : transition.AsEffect().WithParameter(name, value).Parameters;
                return ReplaceTransition(project, owner, transition with { Parameters = parameters });

            case ParamOwnerKind.Mask:
                Mask mask = owner.Mask!;
                Mask edited = name switch
                {
                    "bounds" => mask with { Bounds = value },
                    "path" => mask with { PathData = value },
                    "feather" => mask with { Feather = value },
                    "expansion" => mask with { Expansion = value },
                    _ => mask with { Opacity = value },
                };
                return ReplaceMask(project, owner, edited);

            default:
                Project updated = project.ReplaceTrack(owner.Track.ReplaceClip(WithClipValue(owner.Clip!, name, value, descriptor)));

                // A speed curve is the whole link's: the picture and its sound stay in step.
                if (name == Animation.TimeRemap.Parameter && owner.Clip!.LinkGroupId is { } link)
                {
                    foreach (Track other in owner.Sequence.Tracks)
                    {
                        foreach (Clip partner in other.Clips.Where(candidate => candidate.LinkGroupId == link && candidate.Id != owner.Clip.Id))
                        {
                            Track current = updated.Sequence(owner.Sequence.Id)!.Track(other.Id)!;
                            updated = updated.ReplaceTrack(current.ReplaceClip(partner with { Remap = value }));
                        }
                    }
                }

                return updated;
        }
    }

    /// <summary>A project with a mask replaced where it is, on its clip or on its effect.</summary>
    public static Project ReplaceMask(Project project, ParamOwner owner, Mask mask)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mask);

        if (owner.Effect is { } effect)
        {
            return ReplaceEffect(project, owner, effect with { Masks = effect.Masks.SetItem(effect.Masks.IndexOf(item => Is(item.Id, mask.Id)), mask) });
        }

        Clip clip = owner.Clip!;
        return project.ReplaceTrack(owner.Track.ReplaceClip(clip with { Masks = clip.Masks.SetItem(clip.Masks.IndexOf(item => Is(item.Id, mask.Id)), mask) }));
    }

    /// <summary>A project with a transition replaced on its track.</summary>
    public static Project ReplaceTransition(Project project, ParamOwner owner, Transition transition)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(transition);

        int index = owner.Track.Transitions.IndexOf(item => Is(item.Id, transition.Id));
        return project.ReplaceTrack(owner.Track with { Transitions = owner.Track.Transitions.SetItem(index, transition) });
    }

    /// <summary>A project with an effect instance replaced where it is, on its clip or its track.</summary>
    public static Project ReplaceEffect(Project project, ParamOwner owner, Effect effect)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(effect);

        if (owner.Clip is { } clip)
        {
            int index = clip.Effects.IndexOf(item => Is(item.Id, effect.Id));
            return project.ReplaceTrack(owner.Track.ReplaceClip(clip with { Effects = clip.Effects.SetItem(index, effect) }));
        }

        int at = owner.Track.Effects.IndexOf(item => Is(item.Id, effect.Id));
        return project.ReplaceTrack(owner.Track with { Effects = owner.Track.Effects.SetItem(at, effect) });
    }

    /// <summary>The effect entry holding a generator clip's own parameters, or null.</summary>
    private static Effect? OwnParameters(Clip clip) =>
        clip.Effects.FirstOrDefault(effect => EffectChains.IsOwnParameters(clip, effect));

    private static bool IsIntrinsic(string name) =>
        name is "opacity" or "volume" or "pan" or Animation.TimeRemap.Parameter
        || name.StartsWith("transform.", StringComparison.Ordinal)
        || name.StartsWith("crop.", StringComparison.Ordinal);

    private static AnimatedValue? ClipValue(Clip clip, string name) => name switch
    {
        _ when !IsIntrinsic(name) => OwnParameters(clip)?.Parameter(name),
        "transform.position" => clip.Transform?.Position,
        "transform.scale" => clip.Transform?.Scale,
        "transform.rotation" => clip.Transform?.Rotation,
        "transform.anchor" => clip.Transform?.Anchor,
        "opacity" => clip.Opacity,
        "crop.left" => clip.Crop?.Left,
        "crop.top" => clip.Crop?.Top,
        "crop.right" => clip.Crop?.Right,
        "crop.bottom" => clip.Crop?.Bottom,
        "volume" => clip.Volume,
        "pan" => clip.Pan,
        Animation.TimeRemap.Parameter => clip.Remap,
        _ => null,
    };

    private static Clip WithClipValue(Clip clip, string name, AnimatedValue? value, ParamDescriptor descriptor)
    {
        AnimatedValue stored = value ?? AnimatedValue.Constant(descriptor.Default);

        if (!IsIntrinsic(name))
        {
            // A generator's parameter lives on the effect entry of its own type, made on first use.
            Effect? own = OwnParameters(clip);
            if (own is null && value is null)
            {
                return clip;
            }

            own ??= Effect.Create(clip.GeneratorId!);
            Effect changed = value is null
                ? own with { Parameters = new EquatableArray<EffectParameter>(own.Parameters.Where(parameter => !Is(parameter.Name, name))) }
                : own.WithParameter(name, value);

            int index = clip.Effects.IndexOf(effect => Is(effect.Id, own.Id));
            return clip with { Effects = index < 0 ? clip.Effects.Insert(0, changed) : clip.Effects.SetItem(index, changed) };
        }

        switch (name)
        {
            case "opacity":
                return clip with { Opacity = value };
            case "volume":
                return clip with { Volume = value };
            case "pan":
                return clip with { Pan = value };
            case Animation.TimeRemap.Parameter:
                return clip with { Remap = value };
        }

        if (name.StartsWith("transform.", StringComparison.Ordinal))
        {
            Transform current = clip.Transform ?? Model.Transform.Identity;
            Transform transform = name switch
            {
                "transform.position" => current with { Position = stored },
                "transform.scale" => current with { Scale = stored },
                "transform.rotation" => current with { Rotation = stored },
                _ => current with { Anchor = stored },
            };

            return clip with { Transform = transform == Model.Transform.Identity ? null : transform };
        }

        Crop crop = clip.Crop ?? Model.Crop.None;
        Crop cropped = name switch
        {
            "crop.left" => crop with { Left = stored },
            "crop.top" => crop with { Top = stored },
            "crop.right" => crop with { Right = stored },
            _ => crop with { Bottom = stored },
        };

        return clip with { Crop = cropped == Model.Crop.None ? null : cropped };
    }

    private static EffectDescriptor Section(string typeId, string name, params ParamDescriptor[] parameters) =>
        new(typeId, EffectKind.Video, name, "Intrinsic", string.Empty, [.. parameters]);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static Mask? MaskOf(EquatableArray<Mask> masks, string id)
    {
        foreach (Mask mask in masks)
        {
            if (Is(mask.Id, id))
            {
                return mask;
            }
        }

        return null;
    }
}
