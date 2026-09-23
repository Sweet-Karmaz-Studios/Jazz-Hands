namespace JazzHands.Core.Commands;

/// <summary>Asks for every sequence in the project.</summary>
[Query("sequence.list", Description = "List the sequences in the project")]
public sealed record ListSequencesQuery : IQuery<SequenceInfo[]>;
