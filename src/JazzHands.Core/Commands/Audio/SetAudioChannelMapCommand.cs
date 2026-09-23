using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Chooses which of a source's channels an audio clip plays.</summary>
/// <remarks>
/// For a microphone recorded on one side of a stereo stream: left or right plays that channel
/// alone as a mono signal, centred by the clip's pan; mono averages every channel into one; auto
/// plays the stream as it is.
/// </remarks>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Map">auto, left, right or mono.</param>
[Command("audio.set-channel-map", Description = "Play one channel of a stereo clip as mono")]
public sealed record SetAudioChannelMapCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "auto, left, right or mono")] AudioChannelMap Map) : ICommand;
