using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization;

/// <summary>One step from one schema version to the next.</summary>
/// <remarks>
/// A migration is a JSON-to-JSON transform, deliberately. It runs before anything is
/// deserialized, so it is written against the shape the old file actually has rather than against
/// a model that no longer matches it, and an old file keeps loading however far the records move.
/// Each one needs a fixture pair under <c>tests/fixtures/migrations</c>: the document before and
/// the document after.
/// </remarks>
public interface IMigration
{
    /// <summary>The version this migration reads.</summary>
    int From { get; }

    /// <summary>The version it produces. Always <see cref="From"/> plus one.</summary>
    int To { get; }

    /// <summary>What changed, for the log and for the CLI.</summary>
    string Description { get; }

    /// <summary>Rewrites the document in place.</summary>
    void Apply(JsonObject document);
}

/// <summary>
/// The chain that brings any supported .jazz document up to the version this build writes.
/// </summary>
/// <remarks>
/// There is nothing in the chain yet: version 1 is the first published format. The pipeline
/// exists now, with its tests, because the moment a migration is needed is the worst moment to
/// find out the mechanism was never built. <see cref="MigrationTests"/> in the Core tests proves
/// it works by running a fake migration through it.
/// </remarks>
public static class Migrations
{
    /// <summary>The migrations, in order.</summary>
    public static ImmutableArray<IMigration> All { get; } = [];

    /// <summary>The oldest version this build can open.</summary>
    public static int OldestSupported => All.IsEmpty ? Project.CurrentSchemaVersion : All[0].From;

    /// <summary>The version of a document, or 0 when it does not say.</summary>
    public static int VersionOf(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document["schemaVersion"] is JsonValue value && value.TryGetValue(out int version) ? version : 0;
    }

    /// <summary>
    /// Brings a document up to <see cref="Project.CurrentSchemaVersion"/>, in place.
    /// </summary>
    /// <param name="document">The parsed file. Modified.</param>
    /// <param name="applied">What ran, oldest first.</param>
    /// <returns>False when the document is too old or too new to open.</returns>
    public static bool Upgrade(JsonObject document, out ImmutableArray<IMigration> applied) =>
        Upgrade(document, All, out applied);

    /// <summary>Runs a specific chain, which is how the pipeline is tested without inventing a format.</summary>
    public static bool Upgrade(JsonObject document, ImmutableArray<IMigration> chain, out ImmutableArray<IMigration> applied)
    {
        ArgumentNullException.ThrowIfNull(document);

        applied = [];
        int version = VersionOf(document);

        if (version == Project.CurrentSchemaVersion)
        {
            return true;
        }

        if (version > Project.CurrentSchemaVersion)
        {
            // Refusing beats guessing. A newer file may use members that change the meaning of
            // ones this build does understand, and saving it back would quietly rewrite it.
            return false;
        }

        var ran = ImmutableArray.CreateBuilder<IMigration>();

        foreach (IMigration migration in chain)
        {
            if (migration.From != version)
            {
                continue;
            }

            migration.Apply(document);
            document["schemaVersion"] = migration.To;
            version = migration.To;
            ran.Add(migration);
        }

        applied = ran.ToImmutable();
        return version == Project.CurrentSchemaVersion;
    }
}
