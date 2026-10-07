using JazzHands.App.Services;
using JazzHands.Core.Commands;

namespace JazzHands.App.ViewModels.Media;

/// <summary>
/// Relinking from the editor: a file of another length is refused by <c>media.relink</c> (every cut
/// made against the item would move), so the editor asks, and on a yes relinks with
/// <c>--force</c>, as the command line does when it is typed.
/// </summary>
internal static class Relinking
{
    /// <summary>The refusal that asking can get past.</summary>
    internal const string DifferentLength = "different-duration";

    /// <summary>Relinks an item to a file, asking first when the file's length differs; the last result.</summary>
    internal static async Task<CommandResult> RelinkAsync(ISession session, IDialogService? dialogs, string mediaId, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        CommandResult result = await session.ExecuteAsync(new RelinkMediaCommand(mediaId, path)).ConfigureAwait(true);
        if (result.Ok || result.Code != DifferentLength || dialogs is null
            || !await dialogs.AskToRelinkAnywayAsync(Reason(result.Error)).ConfigureAwait(true))
        {
            return result;
        }

        return await session.ExecuteAsync(new RelinkMediaCommand(mediaId, path, Force: true)).ConfigureAwait(true);
    }

    /// <summary>The refusal without its advice for the command line, which a dialog asks instead.</summary>
    internal static string Reason(string? error)
    {
        string text = error ?? "The file is not the same length.";
        int advice = text.IndexOf(" Pass --force", StringComparison.Ordinal);
        return advice > 0 ? text[..advice] : text;
    }
}
