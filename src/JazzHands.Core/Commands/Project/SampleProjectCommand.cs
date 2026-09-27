namespace JazzHands.Core.Commands;

/// <summary>Replaces the session's project with the sample: a short trailer to try every tool on.</summary>
/// <remarks>
/// Twenty seconds at 1920x1080 and 30 fps, made only of the editor's own pieces, so it opens
/// anywhere with nothing to find: four gradient shots with particles over them, a crossfade and a
/// blur dissolve, a title card, a lower third and two motion templates (a feature callout and an end card), markers
/// at each beat, and a quiet bed of wind with its fades. Everything in it is ordinary clips,
/// titles and effects, edited as any other. Not undoable, for the reason <c>project.new</c> is
/// not; like it, refused over unsaved changes unless told to discard them. From jazz without an
/// editor, <c>jazz new demo.jazz --sample</c>.
/// </remarks>
/// <param name="Discard">Throw away unsaved changes in the current project.</param>
[Command("project.sample",
    Description = "Open the sample project: a 20 second trailer of gradients, particles, titles and sound to try every tool on",
    Undoable = false,
    NotUndoableReason = "Undo history belongs to a project, and this replaces the project.")]
public sealed record SampleProjectCommand(
    [property: Option("discard", "Throw away unsaved changes")] bool Discard = false) : ICommand;
