using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Points media items at files that have moved: one by hand, or every missing one found.</summary>
/// <remarks>
/// <para>
/// By hand, the new file is hashed and probed. A different hash is not refused, because relinking
/// to a re-encode or a proxy is a normal thing to do, but the duration is checked: a file of a
/// different length would silently change where every cut lands.
/// </para>
/// <para>
/// With <c>--auto</c>, every missing item (or the one named) is looked for in the folders given,
/// the project's folder and the folder it used to be in: first by hash, which is the file itself
/// wherever it went, then by name and size when exactly one file fits. Nothing is asked. Items
/// that were not found are left as they were and listed in the answer.
/// </para>
/// </remarks>
/// <param name="MediaId">Which media item; with <paramref name="Auto"/>, only this one.</param>
/// <param name="Path">Where the file is now; not with <paramref name="Auto"/>.</param>
/// <param name="Force">Accept a file of a different duration.</param>
/// <param name="Auto">Find the missing files by hash, then by name and size.</param>
/// <param name="Search">Folders to look in, below them too.</param>
[Command("media.relink", Description = "Point media at files that have moved, by hand or found by hash")]
public sealed record RelinkMediaCommand(
    [property: Arg(0, "The media id")] string? MediaId = null,
    [property: Arg(1, "Where the file is now")] string? Path = null,
    [property: Option("force", "Accept a file of a different duration")] bool Force = false,
    [property: Option("auto", "Find every missing file by hash, then by name and size")] bool Auto = false,
    [property: Option("search", "Folders to look in, with their subfolders")] EquatableArray<string> Search = default) : ICommand;
