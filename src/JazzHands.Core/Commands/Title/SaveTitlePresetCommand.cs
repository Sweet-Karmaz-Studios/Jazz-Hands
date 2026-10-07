namespace JazzHands.Core.Commands;

/// <summary>Saves a title's look, place, text and animations as a preset of one's own.</summary>
/// <remarks>
/// The preset is a file in <c>%APPDATA%\JazzHands\titles</c>, in the format the built-in presets
/// use, so <c>title.add --preset</c> and the Effects panel offer it at once. Its sizes and place are
/// written for a 1080 line frame, as every preset's are, so it lands the same at any size. A value
/// that is keyframed is saved as it is at the clip's start; the animation channels are saved as the
/// animations that made them.
/// </remarks>
/// <param name="ClipId">The title clip.</param>
/// <param name="Name">The preset's name, in kebab case: what --preset takes.</param>
/// <param name="Label">What the editor calls it; the name when left out.</param>
/// <param name="Description">One sentence on when to use it.</param>
/// <param name="Replace">Write over a preset of one's own with that name, or put one in place of a built-in.</param>
[Command("title.save-preset",
    Description = "Save a title's look as a preset of your own",
    Undoable = false,
    NotUndoableReason = "It writes a preset file outside the project. Delete the file from the titles folder to take it back.")]
public sealed record SaveTitlePresetCommand(
    [property: Arg(0, "The title clip id")] string ClipId,
    [property: Option("name", "The preset's name in kebab case, as --preset takes it")] string Name,
    [property: Option("label", "What the editor calls it; the name when left out")] string? Label = null,
    [property: Option("description", "One sentence on when to use it")] string? Description = null,
    [property: Option("replace", "Write over a preset of your own with that name, or replace a built-in")] bool Replace = false) : ICommand;
