namespace JazzHands.Core.Commands;

/// <summary>Asks what playback is doing: where the playhead is, the rate, the quality, the drops.</summary>
[Query("playback.state", Description = "Where the playhead is and what playback is doing")]
public sealed record GetPlaybackStateQuery : IQuery<PlaybackStateInfo>;
