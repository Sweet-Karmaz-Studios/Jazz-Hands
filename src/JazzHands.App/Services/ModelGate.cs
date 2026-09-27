using JazzHands.Core.Commands;

namespace JazzHands.App.Services;

/// <summary>
/// Runs a command that may need a machine learning model the person has not downloaded (Phase
/// 43): when it is refused with <c>model-missing</c>, asks them (what the model is for, its size
/// and licence), downloads it on yes with <c>model.download</c>, and runs the command again.
/// </summary>
public static class ModelGate
{
    /// <summary>Runs <paramref name="command"/>, offering the download it needs.</summary>
    public static async Task<CommandResult> RunAsync(ISession session, IDialogService? dialogs, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        CommandResult result = await session.ExecuteAsync(command).ConfigureAwait(true);
        if (result is not { Ok: false, Code: "model-missing", Path: { Length: > 0 } name } || dialogs is null
            || session.Query(new ListModelsQuery()).FirstOrDefault(model => model.Name == name) is not { } model
            || !await dialogs.AskToDownloadModelAsync(model).ConfigureAwait(true))
        {
            return result;
        }

        CommandResult fetched = await session.ExecuteAsync(new DownloadModelCommand(name)).ConfigureAwait(true);
        return fetched.Ok ? await session.ExecuteAsync(command).ConfigureAwait(true) : fetched;
    }
}
