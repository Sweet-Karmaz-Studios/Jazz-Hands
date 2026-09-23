namespace JazzHands.Core.Commands;

/// <summary>Mutes one audio stream of a clip, such as the microphone of a capture.</summary>
/// <remarks>
/// A movie's audio streams are separate clips on separate tracks, linked to the picture. This
/// finds the linked audio clip playing the stream and switches it off or on, so "mute the mic on
/// this clip" does not mean finding the right clip on the right track first. The clip given can
/// be the picture or any clip linked to it; given an audio clip and no stream, it is that clip.
/// </remarks>
/// <param name="ClipId">The clip, or any clip linked to it.</param>
/// <param name="Stream">Which stream of the media, as the container numbers it.</param>
/// <param name="Muted">True to mute, false to bring it back.</param>
[Command("audio.mute-stream", Description = "Mute one audio stream of a clip")]
public sealed record MuteAudioStreamCommand(
    [property: Arg(0, "The clip id, or any clip linked to it")] string ClipId,
    [property: Option("stream", "Which stream of the media")] int? Stream = null,
    [property: Option("muted", "true to mute, false to unmute")] bool Muted = true) : ICommand;
