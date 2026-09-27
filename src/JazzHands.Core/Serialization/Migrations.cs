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
/// Version 1 is the first published format; version 2 (Phase 36) keeps a clip's pitch at any speed.
/// <see cref="MigrationTests"/> in the Core tests runs the chain against fixture pairs.
/// </remarks>
public static class Migrations
{
    /// <summary>The migrations, in order.</summary>
    public static ImmutableArray<IMigration> All { get; } = [new PitchFollowsSpeedMigration()];

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

/// <summary>
/// Version 1 to 2 (Phase 36): a clip's sound keeps its pitch at any speed from now on. Every clip
/// played its sound like tape before, so a clip already at a speed other than normal, or on a
/// speed curve, is marked to keep doing so: a project opens sounding exactly as it did.
/// </summary>
public sealed class PitchFollowsSpeedMigration : IMigration
{
    /// <inheritdoc />
    public int From => 1;

    /// <inheritdoc />
    public int To => 2;

    /// <inheritdoc />
    public string Description => "Clips at a speed keep sounding as they did (pitch follows speed); new ones keep their pitch.";

    /// <inheritdoc />
    public void Apply(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (JsonNode? sequence in document["sequences"] as JsonArray ?? [])
        {
            foreach (JsonNode? track in sequence?["tracks"] as JsonArray ?? [])
            {
                foreach (JsonNode? node in track?["clips"] as JsonArray ?? [])
                {
                    if (node is JsonObject clip && AtASpeed(clip))
                    {
                        clip["pitchFollowsSpeed"] = true;
                    }
                }
            }
        }
    }

    private static bool AtASpeed(JsonObject clip)
    {
        if (clip["remap"] is not null)
        {
            return true;
        }

        return clip["speed"] is JsonObject speed
            && speed["num"] is JsonValue num && num.TryGetValue(out long n)
            && speed["den"] is JsonValue den && den.TryGetValue(out long d)
            && n != d;
    }
}
