using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Changes a media item. Only the members given are changed.</summary>
/// <param name="MediaId">Which media item.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Folder">Where it sits in the bin.</param>
/// <param name="Tags">Its tags, replacing what was there.</param>
/// <param name="Color">A colour label.</param>
/// <param name="Conform">How its picture is fitted.</param>
/// <param name="Deinterlace">Whether to deinterlace on decode.</param>
/// <param name="VfrConform">Whether to remap variable frame timing.</param>
[Command("media.set", Description = "Change a media item's name, folder, tags or conform settings")]
public sealed record SetMediaCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("name", "Its display name")] string? Name = null,
    [property: Option("folder", "Where it sits in the bin")] string? Folder = null,
    [property: Option("tags", "Comma-separated tags, replacing what was there")] EquatableArray<string>? Tags = null,
    [property: Option("color", "A colour label")] string? Color = null,
    [property: Option("conform", "fit, fill, stretch or native")] ConformPolicy? Conform = null,
    [property: Option("deinterlace", "auto, on or off")] AutoSetting? Deinterlace = null,
    [property: Option("vfr-conform", "auto, on or off")] AutoSetting? VfrConform = null) : ICommand;
