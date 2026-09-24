using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the picture handlers share: finding a clip with a picture and storing a change to it.</summary>
internal static class PictureHelp
{
    /// <summary>A clip on a video or adjustment track, on a track that is not locked.</summary>
    internal static ClipLocation PictureClip(Project project, string clipId)
    {
        ClipLocation found = HandlerHelp.Clip(project, clipId);

        if (found.Track.Kind is not (TrackKind.Video or TrackKind.Adjustment))
        {
            throw new CommandException(
                "not-a-picture",
                $"Clip '{clipId}' is on {found.Track.Kind.ToString().ToLowerInvariant()} track '{found.Track.Name}', and only clips on video and adjustment tracks have a picture.");
        }

        HandlerHelp.RequireUnlocked(found.Track);
        return found;
    }

    /// <summary>Stores a changed clip, or hands the project back when nothing changed.</summary>
    internal static Project Replace(Project project, ClipLocation found, Clip changed, HandlerContext context)
    {
        if (changed == found.Clip)
        {
            return project;
        }

        context.Changed(found.Clip.Id);
        return project.ReplaceTrack(found.Track.ReplaceClip(changed));
    }

    /// <summary>A value from 0 to 1, or a coded refusal.</summary>
    internal static float Unit(double value, string what)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            throw new CommandException("value-out-of-range", $"{what} runs from 0 to 1; {value} is outside that.");
        }

        return (float)value;
    }

    /// <summary>A finite number, or a coded refusal.</summary>
    internal static float Finite(double value, string what) =>
        double.IsFinite(value)
            ? (float)value
            : throw new CommandException("value-out-of-range", $"{what} has to be a number.");

    /// <summary>The static value of a parameter now, whatever it is animated to at the clip's start.</summary>
    internal static ParamValue Current(AnimatedValue value) => AnimationEvaluator.Evaluate(value, Core.Time.Flicks.Zero);

    /// <summary>Where a mask is, and the clip it is on.</summary>
    internal static Core.Effects.ParamOwner FindMask(Project project, string maskId)
    {
        if (Core.Effects.ParamTargets.Find(project, maskId) is { Kind: Core.Effects.ParamOwnerKind.Mask } found)
        {
            HandlerHelp.RequireUnlocked(found.Track);
            return found;
        }

        throw new CommandException("mask-not-found", $"No mask with id '{maskId}'.", "/sequences");
    }

    /// <summary>
    /// What a mask goes on: a clip with a picture, or a picture effect. The owner returned is the
    /// clip or the effect, unlocked.
    /// </summary>
    internal static Core.Effects.ParamOwner MaskHost(Project project, string ownerId)
    {
        Core.Effects.ParamOwner owner = ParamHelp.Owner(project, ownerId);

        if (owner.Kind == Core.Effects.ParamOwnerKind.Clip)
        {
            PictureClip(project, ownerId);
            return owner;
        }

        if (owner.Kind == Core.Effects.ParamOwnerKind.Effect && owner.Track.Kind is TrackKind.Video or TrackKind.Adjustment)
        {
            HandlerHelp.RequireUnlocked(owner.Track);
            return owner;
        }

        throw new CommandException(
            "not-a-mask-owner",
            $"'{ownerId}' is {ParamHelp.Describe(owner)}. Masks go on clips with a picture and on picture effects.");
    }

    /// <summary>The masks a host has.</summary>
    internal static EquatableArray<Mask> MasksOf(Core.Effects.ParamOwner host) =>
        host.Kind == Core.Effects.ParamOwnerKind.Effect || (host.Kind == Core.Effects.ParamOwnerKind.Mask && host.Effect is not null)
            ? host.Effect!.Masks
            : host.Clip!.Masks;

    /// <summary>A project with a host's masks replaced, reporting the host and what it sits on.</summary>
    internal static Project WithMasks(Project project, Core.Effects.ParamOwner host, EquatableArray<Mask> masks, HandlerContext context)
    {
        if (host.Effect is { } effect)
        {
            context.Changed(effect.Id);
            context.Changed(host.Clip?.Id ?? host.Track.Id);
            return Core.Effects.ParamTargets.ReplaceEffect(project, host, effect with { Masks = masks });
        }

        context.Changed(host.Clip!.Id);
        return project.ReplaceTrack(host.Track.ReplaceClip(host.Clip with { Masks = masks }));
    }

    /// <summary>Refuses a mask whose shape has nothing to draw.</summary>
    internal static void RequireDrawable(Mask mask)
    {
        if (mask.Shape is MaskShape.Rectangle or MaskShape.Ellipse)
        {
            if (mask.Bounds is null || Current(mask.Bounds) is not ParamValue.Float4 { Value: { Z: > 0, W: > 0 } })
            {
                throw new CommandException(
                    "invalid-mask",
                    $"A {mask.Shape.ToString().ToLowerInvariant()} mask needs a width and a height above zero: --width and --height.");
            }

            return;
        }

        string path = mask.PathData is null ? string.Empty : Current(mask.PathData) is ParamValue.Path value ? value.Value : string.Empty;

        try
        {
            // A closed outline needs two edges to enclose anything; one curve already bows away
            // from the straight line that closes it.
            if (MaskPath.Parse(path).Sum(figure => figure.Segments.Sum(segment => segment.IsCurve ? 2 : 1)) < 2)
            {
                throw new FormatException("A mask outline needs at least three points.");
            }
        }
        catch (FormatException error)
        {
            throw new CommandException("invalid-mask", $"The mask path cannot be read: {error.Message}");
        }
    }
}

/// <summary>Moves, scales and rotates a clip's picture.</summary>
public sealed class SetClipTransformHandler : ICommandHandler<SetClipTransformCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipTransformCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = PictureHelp.PictureClip(project, command.ClipId);
        Transform current = found.Clip.Transform ?? Transform.Identity;

        Vector2 position = PictureHelp.Current(current.Position) is ParamValue.Float2 p ? p.Value : Vector2.Zero;
        Vector2 scale = PictureHelp.Current(current.Scale) is ParamValue.Float2 s ? s.Value : Vector2.One;
        float rotation = PictureHelp.Current(current.Rotation) is ParamValue.Float r ? r.Value : 0.0f;
        Vector2 anchor = PictureHelp.Current(current.Anchor) is ParamValue.Float2 a ? a.Value : Vector2.Zero;

        position = new Vector2(
            command.X is { } x ? PictureHelp.Finite(x, "x") : position.X,
            command.Y is { } y ? PictureHelp.Finite(y, "y") : position.Y);

        if (command.Scale is { } both)
        {
            scale = new Vector2(Scale(both));
        }

        scale = new Vector2(
            command.ScaleX is { } scaleX ? Scale(scaleX) : scale.X,
            command.ScaleY is { } scaleY ? Scale(scaleY) : scale.Y);

        rotation = command.Rotation is { } degrees ? PictureHelp.Finite(degrees, "The rotation") : rotation;
        anchor = new Vector2(
            command.AnchorX is { } anchorX ? PictureHelp.Finite(anchorX, "anchor-x") : anchor.X,
            command.AnchorY is { } anchorY ? PictureHelp.Finite(anchorY, "anchor-y") : anchor.Y);

        var transform = new Transform(
            AnimatedValue.Constant(new ParamValue.Float2(position)),
            AnimatedValue.Constant(new ParamValue.Float2(scale)),
            AnimatedValue.Constant(rotation),
            AnimatedValue.Constant(new ParamValue.Float2(anchor)));

        // No transform at all is stored as none, which keeps the file as it was before anyone
        // touched the clip.
        return PictureHelp.Replace(project, found, found.Clip with { Transform = transform == Transform.Identity ? null : transform }, context);
    }

    private static float Scale(double value)
    {
        float scale = PictureHelp.Finite(value, "The scale");
        return scale != 0.0f
            ? scale
            : throw new CommandException("value-out-of-range", "A scale of zero leaves nothing to see; a negative scale flips the picture.");
    }
}

/// <summary>Sets how opaque a clip's picture is.</summary>
public sealed class SetClipOpacityHandler : ICommandHandler<SetClipOpacityCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipOpacityCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = PictureHelp.PictureClip(project, command.ClipId);
        float opacity = PictureHelp.Unit(command.Opacity, "Opacity");

        return PictureHelp.Replace(project, found, found.Clip with { Opacity = opacity == 1.0f ? null : AnimatedValue.Constant(opacity) }, context);
    }
}

/// <summary>Sets how a clip's picture blends with what is underneath.</summary>
public sealed class SetClipBlendHandler : ICommandHandler<SetClipBlendCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipBlendCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!Enum.IsDefined(command.Mode))
        {
            throw new CommandException("invalid-value", $"{(int)command.Mode} is not a blend mode.");
        }

        ClipLocation found = PictureHelp.PictureClip(project, command.ClipId);
        return PictureHelp.Replace(project, found, found.Clip with { BlendMode = command.Mode }, context);
    }
}

/// <summary>Cuts away the edges of a clip's picture.</summary>
public sealed class SetClipCropHandler : ICommandHandler<SetClipCropCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipCropCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = PictureHelp.PictureClip(project, command.ClipId);
        Crop current = found.Clip.Crop ?? Crop.None;

        float left = Side(command.Left, current.Left);
        float top = Side(command.Top, current.Top);
        float right = Side(command.Right, current.Right);
        float bottom = Side(command.Bottom, current.Bottom);

        if (left + right >= 100.0f || top + bottom >= 100.0f)
        {
            throw new CommandException(
                "crop-out-of-range",
                $"That crop leaves nothing: left and right add up to {left + right}%, top and bottom to {top + bottom}%. Each pair has to stay under 100%.");
        }

        var crop = new Crop(
            AnimatedValue.Constant(left),
            AnimatedValue.Constant(top),
            AnimatedValue.Constant(right),
            AnimatedValue.Constant(bottom));

        return PictureHelp.Replace(project, found, found.Clip with { Crop = crop == Crop.None ? null : crop }, context);
    }

    private static float Side(double? given, AnimatedValue current)
    {
        if (given is not { } value)
        {
            return PictureHelp.Current(current) is ParamValue.Float existing ? existing.Value : 0.0f;
        }

        return double.IsNaN(value) || value < 0 || value > 100
            ? throw new CommandException("crop-out-of-range", $"A crop is a percentage from 0 to 100; {value} is outside that.")
            : (float)value;
    }
}

/// <summary>Adds a mask to a clip.</summary>
public sealed class AddMaskHandler : ICommandHandler<AddMaskCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddMaskCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Core.Effects.ParamOwner host = PictureHelp.MaskHost(project, command.OwnerId);
        string id = HandlerHelp.IdOr(command.MaskId);
        HandlerHelp.RequireUnused(project, id);

        var mask = new Mask(
            id,
            command.Shape,
            Bounds(command.X, command.Y, command.Width, command.Height, null),
            command.Path is null ? null : AnimatedValue.Constant(new ParamValue.Path(command.Path)),
            command.Feather == 0 ? null : AnimatedValue.Constant(Feather(command.Feather)),
            command.Opacity == 1 ? null : AnimatedValue.Constant(PictureHelp.Unit(command.Opacity, "Mask opacity")),
            command.Mode,
            command.Invert,
            Expansion: command.Expansion == 0 ? null : AnimatedValue.Constant(Expansion(command.Expansion)));

        PictureHelp.RequireDrawable(mask);
        context.Changed(id);

        return PictureHelp.WithMasks(project, host, PictureHelp.MasksOf(host).Add(mask), context);
    }

    /// <summary>An expansion, grow or shrink, within a thousand pixels either way.</summary>
    internal static float Expansion(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= 1000
            ? (float)value
            : throw new CommandException("value-out-of-range", $"An expansion is a distance in pixels from -1000 to 1000; {value} is not.");

    /// <summary>Bounds from whichever of the four numbers were given, over what was there.</summary>
    internal static AnimatedValue? Bounds(double? x, double? y, double? width, double? height, AnimatedValue? current)
    {
        if (x is null && y is null && width is null && height is null)
        {
            return current;
        }

        Vector4 existing = current is not null && PictureHelp.Current(current) is ParamValue.Float4 value ? value.Value : Vector4.Zero;
        return AnimatedValue.Constant(new ParamValue.Float4(new Vector4(
            x is { } left ? PictureHelp.Finite(left, "x") : existing.X,
            y is { } top ? PictureHelp.Finite(top, "y") : existing.Y,
            width is { } w ? PictureHelp.Finite(w, "The width") : existing.Z,
            height is { } h ? PictureHelp.Finite(h, "The height") : existing.W)));
    }

    /// <summary>A feather, which cannot be negative.</summary>
    internal static float Feather(double value) =>
        value >= 0 && double.IsFinite(value)
            ? (float)value
            : throw new CommandException("value-out-of-range", $"A feather is a distance in pixels, 0 or more; {value} is not.");
}

/// <summary>Takes a mask off its clip.</summary>
public sealed class RemoveMaskHandler : ICommandHandler<RemoveMaskCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveMaskCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Core.Effects.ParamOwner found = PictureHelp.FindMask(project, command.MaskId);
        context.Changed(command.MaskId);

        EquatableArray<Mask> masks = PictureHelp.MasksOf(found);
        return PictureHelp.WithMasks(project, found, masks.RemoveAt(masks.IndexOf(mask => mask.Id == command.MaskId)), context);
    }
}

/// <summary>Changes a mask.</summary>
public sealed class SetMaskHandler : ICommandHandler<SetMaskCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetMaskCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Core.Effects.ParamOwner found = PictureHelp.FindMask(project, command.MaskId);
        Mask current = found.Mask!;

        Mask changed = current with
        {
            Shape = command.Shape ?? current.Shape,
            Bounds = AddMaskHandler.Bounds(command.X, command.Y, command.Width, command.Height, current.Bounds),
            PathData = command.Path is null ? current.PathData : AnimatedValue.Constant(new ParamValue.Path(command.Path)),
            Feather = command.Feather is { } feather ? (feather == 0 ? null : AnimatedValue.Constant(AddMaskHandler.Feather(feather))) : current.Feather,
            Opacity = command.Opacity is { } opacity ? (opacity == 1 ? null : AnimatedValue.Constant(PictureHelp.Unit(opacity, "Mask opacity"))) : current.Opacity,
            Mode = command.Mode ?? current.Mode,
            Invert = command.Invert ?? current.Invert,
            Enabled = command.Enabled ?? current.Enabled,
            Expansion = command.Expansion is { } expansion ? (expansion == 0 ? null : AnimatedValue.Constant(AddMaskHandler.Expansion(expansion))) : current.Expansion,
        };

        if (changed == current)
        {
            return project;
        }

        PictureHelp.RequireDrawable(changed);
        context.Changed(command.MaskId);

        EquatableArray<Mask> masks = PictureHelp.MasksOf(found);
        return PictureHelp.WithMasks(project, found, masks.SetItem(masks.IndexOf(mask => mask.Id == command.MaskId), changed), context);
    }
}
