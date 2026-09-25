using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Queues one export for each range marker on a sequence, or for each of a list of stretches,
/// into a folder.
/// </summary>
/// <remarks>
/// A marker's file is named for the marker, numbered in time order (<c>01 Boss fight.mp4</c>); a
/// stretch's for the sequence (<c>Trailer 01.mp4</c>). Every job is planned before any is queued,
/// so one that cannot be exported stops the batch rather than leaving half of it waiting. Each
/// job id comes back as a changed id.
/// </remarks>
/// <param name="Folder">The folder the files go in.</param>
/// <param name="Preset">Which preset.</param>
/// <param name="Markers">Export each range marker.</param>
/// <param name="Ranges">Export each of these stretches of the sequence instead.</param>
/// <param name="NameContains">Only markers whose name contains this.</param>
/// <param name="Mode">copy, encode, or auto.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="Priority">low, normal or high.</param>
[Command("export.batch",
    Description = "Queue an export for each range marker or each stretch",
    Undoable = false,
    NotUndoableReason = "An export writes files. It does not change the project.")]
public sealed record BatchExportCommand(
    [property: Arg(0, "The folder the files go in")] string Folder,
    [property: Option("preset", "Which preset")] string Preset = ExportPresets.Default,
    [property: Option("markers", "Export each range marker")] bool Markers = false,
    [property: Option("ranges", "Export each of these stretches: 00:10-00:20,01:00-01:30")] EquatableArray<TimeRange> Ranges = default,
    [property: Option("name-contains", "Only markers whose name contains this")] string? NameContains = null,
    [property: Option("mode", "copy, encode, or auto")] ExportMode Mode = ExportMode.Auto,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("priority", "low, normal or high")] ExportPriority Priority = ExportPriority.Normal) : ICommand;
