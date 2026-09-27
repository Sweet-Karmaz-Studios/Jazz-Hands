using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Lists the project's roles, with how many tracks of the active sequence have each.</summary>
[Query("role.list", Description = "List the roles tracks can have")]
public sealed record ListRolesQuery : IQuery<RoleInfo[]>;

/// <summary>One role.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Muted">True when its tracks are silenced.</param>
/// <param name="Solo">True when it is soloed.</param>
/// <param name="Tracks">The ids of the active sequence's tracks with this role.</param>
public sealed record RoleInfo(string Name, string Color, bool Muted, bool Solo, EquatableArray<string> Tracks);
