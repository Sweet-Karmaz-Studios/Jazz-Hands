using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Works out how recordings of the same moment line up, without making anything.</summary>
/// <remarks>What <c>multicam.create</c> would do: each recording's start and the confidence of its match, and the weakest.</remarks>
/// <param name="MediaIds">The recordings, the first the one the others are matched to.</param>
/// <param name="Sync">audio, timecode, in or marker.</param>
[Query("multicam.sync", Description = "Line up recordings of the same moment")]
public sealed record SyncMulticamQuery(
    [property: Arg(0, "Comma-separated media ids")] EquatableArray<string> MediaIds,
    [property: Option("sync", "audio, timecode, in or marker. Default: audio")] MulticamSync Sync = MulticamSync.Audio) : IQuery<MulticamSyncInfo>;
