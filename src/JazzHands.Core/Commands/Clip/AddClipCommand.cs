using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a clip on a track.</summary>
/// <remarks>
/// A clip plays exactly one source: a media item, a generator such as a title, or another
/// sequence. Giving none or more than one is refused.
/// </remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="MediaId">The media item, when it plays a file.</param>
/// <param name="GeneratorId">The generator type, when it is synthetic.</param>
/// <param name="SequenceId">The nested sequence, when it is a compound.</param>
/// <param name="SourceIn">Where playback starts inside the source.</param>
/// <param name="Duration">How long it runs. Defaults to the rest of the source.</param>
/// <param name="Name">Its display name.</param>
/// <param name="SourceStreamIndex">
/// Which stream of the media to play. Left out, the first video stream on a video track and the
/// first audio stream on an audio track.
/// </param>
/// <param name="ClipId">The identifier to give it. A fresh one when left out.</param>
/// <param name="WithAudio">
/// When a movie goes on a video track, also put each of its audio streams on an audio track of
/// its own, named from the stream title (Game, Mic), and link them all to the picture.
/// </param>
[Command("clip.add", Description = "Put a clip on a track")]
public sealed record AddClipCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("at", "Where it starts on the timeline")] Flicks At,
    [property: Option("media", "The media item to play")] string? MediaId = null,
    [property: Option("generator", "The generator type, for example text.title")] string? GeneratorId = null,
    [property: Option("sequence", "The sequence to nest")] string? SequenceId = null,
    [property: Option("in", "Where playback starts inside the source")] Flicks? SourceIn = null,
    [property: Option("dur", "How long it runs")] Flicks? Duration = null,
    [property: Option("name", "Its display name")] string? Name = null,
    [property: Option("stream", "Which stream of the media to play; the first of the right kind when left out")] int? SourceStreamIndex = null,
    [property: Option("id", "The identifier to give it")] string? ClipId = null,
    [property: Option("audio", "Also put each audio stream on an audio track, linked to the picture")] bool WithAudio = true) : ICommand;
