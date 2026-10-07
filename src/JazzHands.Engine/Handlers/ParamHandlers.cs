using System.Globalization;
using System.Numerics;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Sets any parameter: a constant, or a keyframe on one that is animated.</summary>
public sealed class SetParamHandler : ICommandHandler<SetParamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetParamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return Set(project, ParamHelp.Editable(project, command.OwnerId), command.Param, command.Value, command.At, command.Local, context);
    }

    /// <summary>The shared body of <c>param.set</c> and <c>effect.set-param</c>.</summary>
    internal static Project Set(Project project, ParamOwner owner, string name, string text, Flicks? at, bool local, HandlerContext context)
    {
        ParamDescriptor descriptor = ParamHelp.Param(owner, name);
        ParamValue value = ParamHelp.Value(descriptor, text);

        if (ParamTargets.Get(owner, name) is KeyframedValue { IsAnimated: true } keyed)
        {
            if (at is not { } time)
            {
                throw new CommandException(
                    "param-animated",
                    $"'{name}' has keyframes, so it has no one value to set. Give --at to set the keyframe there, or turn animation off with 'jazz param clear-keyframes {owner.Id} {name}'.");
            }

            Flicks when = ParamHelp.Local(owner, time, local);
            KeyframedValue updated = ParamHelp.Upsert(keyed, descriptor, when, value, interp: null, ParamHelp.Tolerance(project, owner));
            return ParamHelp.Store(project, owner, descriptor, updated, context);
        }

        return ParamHelp.Store(project, owner, descriptor, AnimatedValue.Constant(value), context);
    }
}

/// <summary>Turns animation off, keeping one value.</summary>
public sealed class ClearKeyframesHandler : ICommandHandler<ClearKeyframesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearKeyframesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);

        if (ParamTargets.Get(owner, command.Param) is not KeyframedValue { IsAnimated: true } keyed)
        {
            return project;
        }

        Flicks when = command.At is { } at ? ParamHelp.Local(owner, at, command.Local) : keyed.Start;
        ParamValue value = ParamEval.Eval(keyed, descriptor, when);
        return ParamHelp.Store(project, owner, descriptor, AnimatedValue.Constant(value), context);
    }
}

/// <summary>Adds a keyframe, or changes the one at that time.</summary>
public sealed class AddKeyframeHandler : ICommandHandler<AddKeyframeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddKeyframeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);

        if (!descriptor.Animatable)
        {
            throw new CommandException("not-animatable", $"'{descriptor.Name}' cannot be keyframed; set it with 'jazz param set'.");
        }

        if (command.Interp is { } shape && !Enum.IsDefined(shape))
        {
            throw new CommandException("invalid-value", $"{(int)shape} is not an interpolation.");
        }

        Flicks when = ParamHelp.Local(owner, command.At, command.Local);
        AnimatedValue? current = ParamTargets.Get(owner, command.Param);
        ParamValue value = command.Value is { } text
            ? ParamHelp.Value(descriptor, text)
            : ParamEval.Eval(current, descriptor, when);

        KeyframedValue updated = ParamHelp.Upsert(current as KeyframedValue, descriptor, when, value, command.Interp, ParamHelp.Tolerance(project, owner));
        return ParamHelp.Store(project, owner, descriptor, updated, context);
    }
}

/// <summary>Removes a keyframe; the last one leaves its value behind as a constant.</summary>
public sealed class RemoveKeyframeHandler : ICommandHandler<RemoveKeyframeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveKeyframeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        KeyframedValue keyed = ParamHelp.Keyframed(owner, descriptor);
        int index = ParamHelp.Find(owner, descriptor, keyed, ParamHelp.Local(owner, command.At, command.Local), ParamHelp.Tolerance(project, owner));

        AnimatedValue value = keyed.Keyframes.Length == 1
            ? AnimatedValue.Constant(ParamValues.Coerce(descriptor, keyed.Keyframes[0].Value) ?? descriptor.Default)
            : new KeyframedValue(keyed.Keyframes.RemoveAt(index));

        return ParamHelp.Store(project, owner, descriptor, value, context);
    }
}

/// <summary>Moves a keyframe in time.</summary>
public sealed class MoveKeyframeHandler : ICommandHandler<MoveKeyframeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MoveKeyframeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        KeyframedValue keyed = ParamHelp.Keyframed(owner, descriptor);
        Flicks tolerance = ParamHelp.Tolerance(project, owner);
        int index = ParamHelp.Find(owner, descriptor, keyed, ParamHelp.Local(owner, command.At, command.Local), tolerance);
        Flicks to = ParamHelp.Local(owner, command.To, command.Local);
        Keyframe keyframe = keyed.Keyframes[index];
        if (command.Value is { } text)
        {
            keyframe = keyframe with { Value = ParamHelp.Value(descriptor, text) };
        }

        if (keyframe.Time == to && keyframe == keyed.Keyframes[index])
        {
            return project;
        }

        int there = ParamHelp.Nearest(keyed, to, tolerance);
        if (there >= 0 && there != index)
        {
            throw new CommandException(
                "keyframe-exists",
                $"'{descriptor.Name}' already has a keyframe at {Timecode.FormatClock(keyed.Keyframes[there].Time + owner.Origin)}. Remove it first, or move this one elsewhere.");
        }

        var moved = new KeyframedValue(keyed.Keyframes.SetItem(index, keyframe with { Time = to }));
        return ParamHelp.Store(project, owner, descriptor, moved, context);
    }
}

/// <summary>Changes a keyframe's value.</summary>
public sealed class SetKeyframeValueHandler : ICommandHandler<SetKeyframeValueCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetKeyframeValueCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        KeyframedValue keyed = ParamHelp.Keyframed(owner, descriptor);
        int index = ParamHelp.Find(owner, descriptor, keyed, ParamHelp.Local(owner, command.At, command.Local), ParamHelp.Tolerance(project, owner));
        ParamValue value = ParamHelp.Value(descriptor, command.Value);

        var changed = new KeyframedValue(keyed.Keyframes.SetItem(index, keyed.Keyframes[index] with { Value = value }));
        return ParamHelp.Store(project, owner, descriptor, changed, context);
    }
}

/// <summary>Changes how the curve leaves a keyframe.</summary>
public sealed class SetKeyframeInterpHandler : ICommandHandler<SetKeyframeInterpCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetKeyframeInterpCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!Enum.IsDefined(command.Interp))
        {
            throw new CommandException("invalid-value", $"{(int)command.Interp} is not an interpolation.");
        }

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        KeyframedValue keyed = ParamHelp.Keyframed(owner, descriptor);
        int index = ParamHelp.Find(owner, descriptor, keyed, ParamHelp.Local(owner, command.At, command.Local), ParamHelp.Tolerance(project, owner));

        if (!descriptor.Default.IsContinuous && command.Interp != Interp.Hold)
        {
            throw new CommandException(
                "not-interpolable",
                $"'{descriptor.Name}' is a {descriptor.Type.ToString().ToLowerInvariant()}, which holds from one keyframe to the next; it cannot ease.");
        }

        var changed = new KeyframedValue(keyed.Keyframes.SetItem(index, keyed.Keyframes[index] with { Interp = command.Interp }));
        return ParamHelp.Store(project, owner, descriptor, changed, context);
    }
}

/// <summary>Sets a keyframe's bezier handles.</summary>
public sealed class SetKeyframeHandlesHandler : ICommandHandler<SetKeyframeHandlesCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetKeyframeHandlesCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.In is null && command.Out is null)
        {
            throw new CommandException("missing-value", "Give --in, --out or both, each as 'time, value'.");
        }

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        KeyframedValue keyed = ParamHelp.Keyframed(owner, descriptor);
        int index = ParamHelp.Find(owner, descriptor, keyed, ParamHelp.Local(owner, command.At, command.Local), ParamHelp.Tolerance(project, owner));

        if (!descriptor.Default.IsContinuous)
        {
            throw new CommandException(
                "not-interpolable",
                $"'{descriptor.Name}' is a {descriptor.Type.ToString().ToLowerInvariant()}, which holds from one keyframe to the next; it has no curve.");
        }

        Keyframe current = keyed.Keyframes[index];
        Keyframe changed = current with
        {
            Interp = Interp.Bezier,
            InHandle = command.In is { } handleIn ? Handle(handleIn, "--in") : current.InHandle,
            OutHandle = command.Out is { } handleOut ? Handle(handleOut, "--out") : current.OutHandle,
        };

        return ParamHelp.Store(project, owner, descriptor, new KeyframedValue(keyed.Keyframes.SetItem(index, changed)), context);
    }

    private static Vector2 Handle(string text, string option)
    {
        string[] parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float time)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(time)
            || !float.IsFinite(value))
        {
            throw new CommandException("invalid-value", $"{option} takes 'time, value', such as '0.42, 0', not '{text}'.");
        }

        return time is >= 0.0f and <= 1.0f
            ? new Vector2(time, value)
            : throw new CommandException(
                "handle-out-of-range",
                $"A handle's time runs from 0 to 1 across the segment, so the curve never runs backwards; {time} is outside that.");
    }
}
