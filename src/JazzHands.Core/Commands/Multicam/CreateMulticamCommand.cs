using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Makes a multicam clip from recordings of the same moment.</summary>
/// <remarks>
/// Lines the recordings up (see <c>multicam.sync</c>), makes a sequence with an angle per
/// recording (its picture on a track, its sound on tracks of its own) and puts a clip of it on a
/// picture track of the active sequence. A match by sound that is not sure is refused unless
/// <c>--force</c>. One undo.
/// </remarks>
/// <param name="MediaIds">The recordings; the first is the angle shown to begin with.</param>
/// <param name="Sync">audio, timecode, in or marker.</param>
/// <param name="Name">The multicam's name; "Multicam" and a number when left out.</param>
/// <param name="TrackId">The picture track to put the clip on; the lowest unlocked one when left out.</param>
/// <param name="At">Where the clip starts; the end of that track when left out.</param>
/// <param name="ClipId">The identifier for the clip. A fresh one when left out.</param>
/// <param name="SequenceId">The identifier for the multicam sequence. A fresh one when left out.</param>
/// <param name="Force">Make it even when a match by sound is not sure.</param>
[Command("multicam.create", Description = "Make a multicam clip from recordings of the same moment")]
public sealed record CreateMulticamCommand(
    [property: Arg(0, "Comma-separated media ids")] EquatableArray<string> MediaIds,
    [property: Option("sync", "audio, timecode, in or marker. Default: audio")] MulticamSync Sync = MulticamSync.Audio,
    [property: Option("name", "The multicam's name")] string? Name = null,
    [property: Option("track", "The picture track to put the clip on")] string? TrackId = null,
    [property: Option("at", "Where the clip starts; the end of the track when left out")] Flicks? At = null,
    [property: Option("id", "The identifier for the clip")] string? ClipId = null,
    [property: Option("sequence-id", "The identifier for the multicam sequence")] string? SequenceId = null,
    [property: Option("force", "Make it even when a match by sound is not sure")] bool Force = false) : ICommand;
