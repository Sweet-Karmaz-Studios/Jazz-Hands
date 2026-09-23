using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>What is selected.</summary>
/// <param name="Ids">The selected clip and marker ids, in the order they were selected.</param>
/// <param name="SequenceId">The sequence they are in, the active one.</param>
public sealed record SelectionInfo(EquatableArray<string> Ids, string? SequenceId);

/// <summary>Asks what is selected.</summary>
[Query("selection.get", Description = "Which clips and markers are selected")]
public sealed record GetSelectionQuery : IQuery<SelectionInfo>;
