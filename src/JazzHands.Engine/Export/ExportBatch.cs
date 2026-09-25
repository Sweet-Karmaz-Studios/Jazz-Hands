using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Export;

/// <summary>One export of a batch: the stretch it plays and the file it writes.</summary>
/// <param name="Name">The file's name without its extension.</param>
/// <param name="Range">The stretch, in sequence time.</param>
public sealed record BatchItem(string Name, TimeRange Range);

/// <summary>What a batch export writes: one file per range marker, or per stretch, named so they sort in time order.</summary>
public static class ExportBatch
{
    /// <summary>The exports a batch asks for, in time order.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="markers">One per range marker.</param>
    /// <param name="ranges">One per stretch, when not markers.</param>
    /// <param name="nameContains">Only markers whose name contains this, ignoring case.</param>
    public static IReadOnlyList<BatchItem> Items(Sequence sequence, bool markers, IReadOnlyList<TimeRange> ranges, string? nameContains)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(ranges);

        if (markers == ranges.Count > 0)
        {
            throw new CommandException("invalid-value", "Say what to export: each range marker (--markers), or a list of stretches (--ranges), not both.");
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (markers)
        {
            Marker[] chosen = [.. sequence.Markers
                .Where(marker => marker.IsRange)
                .Where(marker => nameContains is null || marker.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                .OrderBy(marker => marker.Time)];

            if (chosen.Length == 0)
            {
                throw new CommandException(
                    "nothing-to-export",
                    nameContains is null
                        ? $"'{sequence.Name}' has no range markers. Give a marker a length, or list stretches with --ranges."
                        : $"No range marker on '{sequence.Name}' has '{nameContains}' in its name.");
            }

            return [.. chosen.Select((marker, index) => new BatchItem(
                Unique(string.Create(CultureInfo.InvariantCulture, $"{index + 1:00} {Safe(marker.Name.Length > 0 ? marker.Name : "Marker")}"), used),
                new TimeRange(marker.Time, marker.Duration)))];
        }

        return [.. ranges.OrderBy(range => range.Start).Select((range, index) => new BatchItem(
            Unique(string.Create(CultureInfo.InvariantCulture, $"{Safe(sequence.Name)} {index + 1:00}"), used),
            range))];
    }

    /// <summary>A name with the characters Windows will not have in a file name replaced.</summary>
    public static string Safe(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. name.Select(character => invalid.Contains(character) ? '-' : character)]);
        safe = safe.Trim().TrimEnd('.');
        return safe.Length == 0 ? "Export" : safe;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int copy = 2; !used.Add(candidate); copy++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({copy})");
        }

        return candidate;
    }
}
