using JazzHands.Core.Commands;
using JazzHands.Core.Drivers;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Drives a parameter with an expression.</summary>
public sealed class SetDriverHandler : ICommandHandler<SetDriverCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetDriverCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        if (owner.IsAudio
            || (owner.Kind == ParamOwnerKind.Clip && (ParamTargets.Audio.Param(descriptor.Name) is not null || ParamTargets.RemapParams.Param(descriptor.Name) is not null)))
        {
            throw new CommandException("not-drivable", $"'{descriptor.Name}' is played by the sound or the speed curve, which drivers do not reach yet; drive a picture parameter.", "param");
        }

        if (!DriverEval.CanDrive(descriptor.Default))
        {
            throw new CommandException("not-drivable", $"'{descriptor.Name}' is a {descriptor.Type.ToString().ToLowerInvariant()}; a driver makes numbers, pairs, colours, whole numbers and switches.", "param");
        }

        try
        {
            DriverExpression.Parse(command.Expression);
        }
        catch (DriverSyntaxException error)
        {
            throw new CommandException("invalid-expression", $"The driver does not read: {error.Message}.", "expression");
        }

        AnimatedValue current = ParamTargets.Get(owner, descriptor.Name) ?? AnimatedValue.Constant(descriptor.Default);
        AnimatedValue under = current switch
        {
            DrivenValue driven => driven.Base,
            KeyframedValue { Keyframes.IsEmpty: true } => AnimatedValue.Constant(descriptor.Default),
            _ => current,
        };

        return ParamHelp.Store(project, owner, descriptor, new DrivenValue(command.Expression, under), context, driver: true);
    }
}

/// <summary>Stops driving a parameter.</summary>
public sealed class ClearDriverHandler : ICommandHandler<ClearDriverCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearDriverCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ParamOwner owner = ParamHelp.Editable(project, command.OwnerId);
        ParamDescriptor descriptor = ParamHelp.Param(owner, command.Param);
        return ParamTargets.Get(owner, descriptor.Name) is DrivenValue driven
            ? ParamHelp.Store(project, owner, descriptor, driven.Base, context, driver: true)
            : project;
    }
}
