namespace JazzHands.Core.Commands;

/// <summary>Asks for the project as a whole.</summary>
[Query("project.get", Description = "Summarise the project: settings, counts and duration")]
public sealed record GetProjectQuery : IQuery<ProjectInfo>;
