namespace JazzHands.Core.Commands;

/// <summary>The machine learning models Jazz Hands uses, and which are downloaded.</summary>
[Query("model.list", Description = "List the machine learning models and whether each is downloaded")]
public sealed record ListModelsQuery : IQuery<ModelInfo[]>;
