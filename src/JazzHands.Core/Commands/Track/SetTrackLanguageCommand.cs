namespace JazzHands.Core.Commands;

/// <summary>Sets the language of what a track says.</summary>
/// <remarks>
/// Written to the stream an export makes of it: a subtitle track's soft subtitles, an audio
/// track's sound when it is exported as a stream of its own. An ISO 639-2 code (<c>eng</c>,
/// <c>fra</c>, <c>deu</c>, <c>jpn</c>); <c>und</c> or nothing clears it.
/// </remarks>
/// <param name="TrackId">The track.</param>
/// <param name="Language">The code.</param>
[Command("track.set-language", Description = "Set the language of what a track says")]
public sealed record SetTrackLanguageCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Arg(1, "An ISO 639-2 code such as eng, or und")] string Language) : ICommand;
