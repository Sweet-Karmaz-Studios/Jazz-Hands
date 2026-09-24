using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a clip on a track at a time and pushes everything after it later on every sync-locked track.</summary>
/// <remarks>Takes the same options as <c>clip.add</c>, and a movie brings its sound, linked, the same way.</remarks>
/// <param name="TrackId">Which track.</param>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="MediaId">The media item, when it plays a file.</param>
/// <param name="GeneratorId">The generator type, when it is synthetic.</param>
/// <param name="SequenceId">The nested sequence, when it is a compound.</param>
/// <param name="SourceIn">Where playback starts inside the source.</param>
/// <param name="Duration">How long it runs. Defaults to the rest of the source.</param>
/// <param name="Name">Its display name.</param>
/// <param name="SourceStreamIndex">Which stream of the media to play.</param>
/// <param name="ClipId">The identifier to give it. A fresh one when left out.</param>
/// <param name="WithAudio">Also put each audio stream of a movie on an audio track, linked.</param>
[Command("clip.insert", Description = "Put a clip in at a time, pushing the rest on")]
public sealed record InsertClipCommand(
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
    [property: Option("audio", "Also put each audio stream on an audio track, linked to the picture")] bool WithAudio = true) : ICommand
{
    /// <summary>The same clip as a plain add, which is what goes in once there is room.</summary>
    public AddClipCommand ToAdd() =>
        new(TrackId, At, MediaId, GeneratorId, SequenceId, SourceIn, Duration, Name, SourceStreamIndex, ClipId, WithAudio);
}
