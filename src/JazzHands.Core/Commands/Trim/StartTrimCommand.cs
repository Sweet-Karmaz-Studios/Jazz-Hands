namespace JazzHands.Core.Commands;

/// <summary>Makes a Quick Trim sequence for a file, keeping all of it, and shows it.</summary>
/// <remarks>
/// The sequence takes the file's frame rate and size, and has V1 for the picture and one audio
/// track per sound stream, named after the stream. Every clip sits at its source time, so cutting
/// is leaving gaps. See <see cref="Model.QuickTrim"/>.
/// </remarks>
/// <param name="MediaId">The file to trim. It must be in the project and have a picture.</param>
/// <param name="SequenceId">The id for the new sequence. A fresh one when left out.</param>
/// <param name="Name">The sequence's name. The file's name when left out.</param>
[Command("trim.start", Description = "Start a Quick Trim of a file")]
public sealed record StartTrimCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("id", "The id for the new sequence")] string? SequenceId = null,
    [property: Option("name", "The sequence's name")] string? Name = null) : ICommand;
