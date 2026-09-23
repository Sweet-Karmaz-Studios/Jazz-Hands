namespace JazzHands.Core.Commands;

/// <summary>Takes a file out of the project.</summary>
/// <remarks>
/// Refused while a clip still plays it, because removing it would leave the clip pointing at
/// nothing. Pass <c>--with-clips</c> to take those clips out too.
/// </remarks>
/// <param name="MediaId">Which media item.</param>
/// <param name="WithClips">Remove the clips that play it as well.</param>
[Command("media.remove", Description = "Take a file out of the project")]
public sealed record RemoveMediaCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("with-clips", "Remove the clips that play it too")] bool WithClips = false) : ICommand;
