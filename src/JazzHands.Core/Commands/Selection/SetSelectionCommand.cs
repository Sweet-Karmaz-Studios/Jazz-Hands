using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>How a new selection combines with the one there is.</summary>
public enum SelectMode
{
    /// <summary>The given ids and nothing else.</summary>
    Replace,

    /// <summary>The given ids as well as what was selected.</summary>
    Add,

    /// <summary>What was selected, less the given ids.</summary>
    Remove,

    /// <summary>Each given id flips: selected ones are dropped, the rest added.</summary>
    Toggle,
}

/// <summary>Chooses what is selected: clips, transitions, markers and comp graph nodes in the active sequence, by id.</summary>
/// <remarks>
/// The selection is what the editor is pointing at, shared by the timeline, the inspector and
/// every remote client, so a script can select and then act the way a person clicks and then
/// presses a key. It is not part of the project and is never saved or undone. Ids are taken
/// literally: selecting one clip of a linked pair selects that clip, and it is the timeline that
/// widens a click to the whole link group.
/// </remarks>
/// <param name="Ids">What to select.</param>
/// <param name="Mode">How it combines with what was selected.</param>
[Command("selection.set",
    Description = "Choose which clips and markers are selected",
    Undoable = false,
    NotUndoableReason = "The selection is what the editor points at. It is not part of the project.")]
public sealed record SetSelectionCommand(
    [property: Arg(0, "Comma-separated clip, marker or comp node ids")] EquatableArray<string> Ids,
    [property: Option("mode", "replace, add, remove or toggle")] SelectMode Mode = SelectMode.Replace) : ICommand;
