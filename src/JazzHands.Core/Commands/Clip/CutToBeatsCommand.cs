using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Lays clips along the beat markers: the trailer montage in one command.</summary>
/// <remarks>
/// Takes the sequence's <c>beat</c> markers between <c>--from</c> and <c>--to</c> (downbeats only
/// with <c>--downbeats</c>), keeps every <c>--every</c>th, and fills each stretch between two of
/// them with the next clip, overwriting what the track had there. The clips come from
/// <c>--clips</c> (clips already on a timeline, each played from where it starts in its source) or
/// from the media in a <c>--bin</c>, by name, each from its start. It stops when the stretches or
/// the clips run out. Their own sound is left off unless <c>--with-audio</c>, since the music is
/// what cuts them. One undo.
/// </remarks>
/// <param name="TrackId">The video track to lay them on.</param>
/// <param name="Every">Cut on every this many beats: 1 on every beat, 4 on every bar in four.</param>
/// <param name="Downbeats">Cut only on downbeats.</param>
/// <param name="From">The first cut is at or after this, on the sequence; the sequence start when left out.</param>
/// <param name="To">The last cut is at or before this; the last beat when left out.</param>
/// <param name="Clips">Clips to lay, in order: their media and where they start.</param>
/// <param name="Bin">A media folder whose items to lay, by name, instead.</param>
/// <param name="WithAudio">Bring each clip's sound along, linked, on audio tracks.</param>
[Command("edit.cut-to-beats", Description = "Lay clips along the beat markers, one to each stretch between cuts")]
public sealed record CutToBeatsCommand(
    [property: Option("track", "The video track to lay them on")] string TrackId,
    [property: Option("every", "Cut on every this many beats. Default: 1")] int Every = 1,
    [property: Option("downbeats", "Cut only on downbeats")] bool Downbeats = false,
    [property: Option("from", "The first cut is at or after this")] Flicks? From = null,
    [property: Option("to", "The last cut is at or before this")] Flicks? To = null,
    [property: Option("clips", "Clips to lay, in order")] EquatableArray<string> Clips = default,
    [property: Option("bin", "A media folder to lay instead, by name")] string? Bin = null,
    [property: Option("with-audio", "Bring each clip's sound along")] bool WithAudio = false) : ICommand;
