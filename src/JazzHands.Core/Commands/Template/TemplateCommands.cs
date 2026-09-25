using System.Collections.Immutable;
using JazzHands.Core.Templates;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Places a motion template: a hero intro, a feature callout, an end card, a countdown, a sting, a button pop.</summary>
/// <remarks>
/// <para>
/// Fill in the template's parameters (<c>template.list</c> says what each is for) with
/// <c>--param name=value</c>, as many as it needs; the rest take their defaults. This is the way
/// to build a trailer from a brief: one template for each beat of it, its text and colours filled
/// in, rather than dozens of separate commands. Everything a template makes is ordinary clips,
/// titles and effects afterwards, edited as any other. One undo.
/// </para>
/// </remarks>
/// <param name="Name">The template.</param>
/// <param name="At">The moment on the sequence it is placed.</param>
/// <param name="Params">Values as name=value.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
[Command("template.apply", Description = "Place a motion template (hero intro, feature callout, end card, countdown, coming soon, wishlist pop) with its parameters filled in: the way to build a trailer from a brief")]
public sealed record ApplyTemplateCommand(
    [property: Arg(0, "The template; template.list names them")] string Name,
    [property: Option("at", "The moment on the sequence it is placed")] Flicks At,
    [property: Option("param", "Values as name=value, several separated by commas: text=Out now,accent=#FF8800")] string[]? Params = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;

/// <summary>The motion templates, with what each is filled in with.</summary>
[Query("template.list", Description = "The motion templates, their parameters and what each makes")]
public sealed record ListTemplatesQuery : IQuery<TemplateInfo[]>;

/// <summary>One template, as <c>template.list</c> shows it.</summary>
/// <param name="Name">What <c>template.apply</c> takes.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">What it makes.</param>
/// <param name="Params">Its parameters, by name.</param>
/// <param name="Steps">How many commands it runs.</param>
/// <param name="BuiltIn">True for one that ships with the editor.</param>
/// <param name="Source">The file a person's template was read from.</param>
public sealed record TemplateInfo(string Name, string Label, string Description, ImmutableSortedDictionary<string, TemplateParam> Params, int Steps, bool BuiltIn, string Source);

/// <summary>Turns clips and their effects into a motion template, with chosen values as its parameters.</summary>
/// <remarks>
/// The clips (the selection when none are named) become the template's steps: a track for each
/// track they are on, each clip at its time from the first, its transform, opacity, own
/// parameters and effects as they are set, keyframes and all. A clip of a file makes that file a
/// parameter. <c>--promote text,fill</c> makes every parameter of those names a parameter of the
/// template, its value now the default. The template is written to
/// <c>%APPDATA%\JazzHands\templates</c>; <c>--force</c> replaces one of the same name. Masks and
/// transitions are not kept. Not undoable: it changes a file, not the project.
/// </remarks>
/// <param name="Name">What to call it, in kebab case.</param>
/// <param name="ClipIds">The clips; the selection when not given.</param>
/// <param name="Promote">Parameter names that become the template's parameters.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">What it makes.</param>
/// <param name="Force">Replace a template of the same name.</param>
[Command("template.save-selection", Description = "Save clips and their effects as a motion template, with chosen values as parameters", Undoable = false, NotUndoableReason = "It writes a template file, not the project.")]
public sealed record SaveTemplateCommand(
    [property: Arg(0, "What to call it, in kebab case")] string Name,
    [property: Option("clips", "The clips; the selection when not given")] string[]? ClipIds = null,
    [property: Option("promote", "Parameter names that become the template's parameters")] string[]? Promote = null,
    [property: Option("label", "What the editor calls it")] string? Label = null,
    [property: Option("description", "What it makes")] string? Description = null,
    [property: Option("force", "Replace a template of the same name")] bool Force = false) : ICommand;
