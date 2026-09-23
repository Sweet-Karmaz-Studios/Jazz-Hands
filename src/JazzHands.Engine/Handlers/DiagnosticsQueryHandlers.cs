using JazzHands.Core.Commands;
using JazzHands.Core.Diagnostics;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Answers what the session has noticed.</summary>
/// <remarks>
/// One of the few queries that reads the session rather than the project. What the decode
/// pipeline found out about a file is not part of the document: it is true of this machine, this
/// run and these files, and saving it would mean shipping somebody else a warning about a GPU
/// they do not have.
/// </remarks>
public sealed class ListDiagnosticsHandler : IQueryHandler<ListDiagnosticsQuery, Diagnostic[]>
{
    /// <inheritdoc />
    public Diagnostic[] Handle(Project project, ListDiagnosticsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Session is not { } session)
        {
            return [];
        }

        IEnumerable<Diagnostic> found = session.Diagnostics.Where(entry => entry.Level >= query.Level);

        if (query.MediaId is { Length: > 0 } mediaId)
        {
            found = found.Where(entry => string.Equals(entry.MediaId, mediaId, StringComparison.Ordinal));
        }

        return [.. found];
    }
}
