namespace JazzHands.Core.Commands;

/// <summary>Cuts away the edges of a clip's picture.</summary>
/// <remarks>
/// Percent of the picture per side, before any scale or rotation, so a crop stays on the same
/// part of the picture however the clip is placed. Only the sides given change. Opposite sides
/// cannot meet: something of the picture has to be left.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on a video track.</param>
/// <param name="Left">Percent of the width taken off the left.</param>
/// <param name="Top">Percent of the height taken off the top.</param>
/// <param name="Right">Percent of the width taken off the right.</param>
/// <param name="Bottom">Percent of the height taken off the bottom.</param>
[Command("clip.set-crop", Description = "Cut away the edges of a clip's picture")]
public sealed record SetClipCropCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("left", "Percent off the left")] double? Left = null,
    [property: Option("top", "Percent off the top")] double? Top = null,
    [property: Option("right", "Percent off the right")] double? Right = null,
    [property: Option("bottom", "Percent off the bottom")] double? Bottom = null) : ICommand;
