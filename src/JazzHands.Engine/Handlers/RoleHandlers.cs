using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the role handlers share.</summary>
internal static class RoleHelp
{
    /// <summary>A role by name, or a coded refusal naming the ones there are.</summary>
    internal static Role Require(Project project, string name) =>
        Role.Find(project, name)
        ?? throw new CommandException(
            "role-not-found",
            $"There is no role called '{name}'. The roles are {string.Join(", ", Role.All(project).Select(role => role.Name))}.");

    /// <summary>The project with its roles, made its own from the built-in ones on the first change.</summary>
    internal static Project WithRoles(Project project, EquatableArray<Role> roles) => project with { Roles = roles };

    /// <summary>Replaces one role, found by name.</summary>
    internal static EquatableArray<Role> Replace(Project project, Role before, Role after) =>
        [.. Role.All(project).Select(role => ReferenceEquals(role, before) ? after : role)];

    /// <summary>Changes every track of every sequence whose role is the one named.</summary>
    internal static Project Retag(Project project, string from, string? to, HandlerContext context)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            Sequence updated = sequence;
            foreach (Track track in sequence.Tracks)
            {
                if (string.Equals(Role.Of(track), from, StringComparison.OrdinalIgnoreCase))
                {
                    updated = updated.ReplaceTrack(track with { Role = to });
                    context.Changed(track.Id);
                }
            }

            if (!ReferenceEquals(updated, sequence))
            {
                project = project.ReplaceSequence(updated);
            }
        }

        return project;
    }
}

/// <summary>Lists the roles.</summary>
public sealed class ListRolesHandler : IQueryHandler<ListRolesQuery, RoleInfo[]>
{
    /// <inheritdoc />
    public RoleInfo[] Handle(Project project, ListRolesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        Track[] tracks = [.. project.ActiveSequence?.Tracks ?? []];
        return [.. Role.All(project).Select(role => new RoleInfo(
            role.Name,
            role.Color,
            role.Muted,
            role.Solo,
            [.. tracks.Where(track => string.Equals(Role.Of(track), role.Name, StringComparison.OrdinalIgnoreCase)).Select(track => track.Id)]))];
    }
}

/// <summary>Adds a role.</summary>
public sealed class AddRoleHandler : ICommandHandler<AddRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new CommandException("invalid-value", "A role needs a name.", "name");
        }

        if (Role.Find(project, name) is not null)
        {
            throw new CommandException("duplicate-role", $"There is a role called '{name}' already.");
        }

        string color = command.Color is { Length: > 0 } given ? CommandValues.ParseColor(given) : "#808080";
        context.Changed(project.Id);
        return RoleHelp.WithRoles(project, [.. Role.All(project), new Role(name, color)]);
    }
}

/// <summary>Renames a role, and its tracks with it.</summary>
public sealed class RenameRoleHandler : ICommandHandler<RenameRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RenameRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Role role = RoleHelp.Require(project, command.Name);
        string name = command.NewName.Trim();
        if (name.Length == 0)
        {
            throw new CommandException("invalid-value", "A role needs a name.", "newName");
        }

        if (string.Equals(name, role.Name, StringComparison.Ordinal))
        {
            return project;
        }

        if (Role.Find(project, name) is { } other && !ReferenceEquals(other, role))
        {
            throw new CommandException("duplicate-role", $"There is a role called '{name}' already.");
        }

        // Tracks named into the role (a Game track) are given it by name, so they follow it.
        project = RoleHelp.Retag(project, role.Name, name, context);
        context.Changed(project.Id);
        return RoleHelp.WithRoles(project, RoleHelp.Replace(project, role, role with { Name = name }));
    }
}

/// <summary>Removes a role; its tracks take another.</summary>
public sealed class RemoveRoleHandler : ICommandHandler<RemoveRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Role role = RoleHelp.Require(project, command.Name);
        Role[] remaining = [.. Role.All(project).Where(candidate => !ReferenceEquals(candidate, role))];
        if (remaining.Length == 0)
        {
            throw new CommandException("last-role", "A project keeps at least one role.");
        }

        Role to = command.To is { Length: > 0 } named ? RoleHelp.Require(project, named) : remaining[0];
        if (ReferenceEquals(to, role))
        {
            throw new CommandException("invalid-value", "The tracks cannot take the role being removed.", "to");
        }

        project = RoleHelp.Retag(project, role.Name, to.Name, context);
        context.Changed(project.Id);
        return RoleHelp.WithRoles(project, [.. remaining]);
    }
}

/// <summary>Mutes or unmutes a role.</summary>
public sealed class MuteRoleHandler : ICommandHandler<MuteRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MuteRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Role role = RoleHelp.Require(project, command.Name);
        if (role.Muted == command.Muted)
        {
            return project;
        }

        context.Changed(project.Id);
        return RoleHelp.WithRoles(project, RoleHelp.Replace(project, role, role with { Muted = command.Muted }));
    }
}

/// <summary>Solos or unsolos a role.</summary>
public sealed class SoloRoleHandler : ICommandHandler<SoloRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SoloRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Role role = RoleHelp.Require(project, command.Name);
        if (role.Solo == command.Solo)
        {
            return project;
        }

        context.Changed(project.Id);
        return RoleHelp.WithRoles(project, RoleHelp.Replace(project, role, role with { Solo = command.Solo }));
    }
}

/// <summary>Gives a track a role.</summary>
public sealed class SetTrackRoleHandler : ICommandHandler<SetTrackRoleCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackRoleCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (_, Track track) = HandlerHelp.Track(project, command.TrackId);
        string? role = command.Role.Trim() is { Length: > 0 } name ? RoleHelp.Require(project, name).Name : null;
        if (track.Role == role)
        {
            return project;
        }

        context.Changed(track.Id);
        return project.ReplaceTrack(track with { Role = role });
    }
}
