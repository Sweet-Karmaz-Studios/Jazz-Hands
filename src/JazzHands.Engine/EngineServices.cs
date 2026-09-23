using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Engine.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine;

/// <summary>Wires the engine into a service collection.</summary>
/// <remarks>
/// Handlers are found by scanning rather than listed, for the same reason commands are: a handler
/// that exists but was never added to a list is a command that fails at runtime with "nothing
/// handles this", and nobody finds out until someone types it.
/// <see cref="VerifyEveryCommandHasAHandler"/> turns that into a test instead.
/// </remarks>
public static class EngineServices
{
    /// <summary>Registers every command and query handler in the engine assembly.</summary>
    public static IServiceCollection AddJazzHandsEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (Type type in typeof(EngineServices).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            foreach (Type contract in type.GetInterfaces())
            {
                if (!contract.IsGenericType)
                {
                    continue;
                }

                Type definition = contract.GetGenericTypeDefinition();

                if (definition == typeof(ICommandHandler<>) || definition == typeof(IQueryHandler<,>))
                {
                    services.AddSingleton(contract, type);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Names every command and query with nothing to handle it.
    /// </summary>
    /// <remarks>
    /// Empty is the only acceptable answer, and a test says so. The dispatcher handles undo, redo
    /// and batch itself, so those three are expected to have no handler.
    /// </remarks>
    public static IReadOnlyList<string> VerifyEveryCommandHasAHandler(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var missing = new List<string>();

        foreach (CommandMetadata metadata in CommandRegistry.All)
        {
            if (IsHandledByTheDispatcher(metadata.Type))
            {
                continue;
            }

            Type handlerType = metadata.IsQuery
                ? typeof(IQueryHandler<,>).MakeGenericType(metadata.Type, metadata.ResultType!)
                : typeof(ICommandHandler<>).MakeGenericType(metadata.Type);

            if (services.GetService(handlerType) is null)
            {
                missing.Add(metadata.Name);
            }
        }

        return missing;
    }

    /// <summary>The three the dispatcher runs itself, because they are about history, not the project.</summary>
    internal static bool IsHandledByTheDispatcher(Type commandType) =>
        commandType == typeof(UndoCommand)
        || commandType == typeof(RedoCommand)
        || commandType == typeof(BatchCommand);
}
