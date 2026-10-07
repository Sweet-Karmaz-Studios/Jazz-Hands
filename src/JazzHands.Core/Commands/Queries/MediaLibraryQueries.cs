using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Checks every media file: there, missing, or changed since it was imported.</summary>
/// <remarks>
/// Changed means the file is there but its hash is not the one the project remembers: replaced
/// in place, re-encoded, or only touched. <c>media.reprobe</c> brings the project up to date with it.
/// </remarks>
[Query("media.check", Description = "Say which media files are there, missing, or changed")]
public sealed record CheckMediaQuery : IQuery<MediaCheckInfo[]>;

/// <summary>Looks for the missing media's files: by hash, then by name and size, then by name.</summary>
/// <param name="Search">Folders to look in, with their subfolders; the project's folder is always looked in.</param>
/// <param name="MediaId">Only this item.</param>
[Query("media.missing", Description = "Find where missing media went, with candidates for each")]
public sealed record FindMissingMediaQuery(
    [property: Option("search", "Folders to look in, with their subfolders")] EquatableArray<string> Search = default,
    [property: Option("media", "Only this media item")] string? MediaId = null) : IQuery<MissingMediaInfo[]>;

/// <summary>Says what each media item is used for: clips, sequences, how much of it.</summary>
/// <param name="MediaId">Only this item.</param>
/// <param name="Unused">Only items no clip uses.</param>
[Query("media.usage", Description = "Say which clips and sequences use each media item")]
public sealed record MediaUsageQuery(
    [property: Option("media", "Only this media item")] string? MediaId = null,
    [property: Option("unused", "Only items no clip uses")] bool Unused = false) : IQuery<MediaUsageInfo[]>;

/// <summary>Lists the folders being watched for new recordings.</summary>
[Query("media.watches", Description = "List the folders being watched for new recordings")]
public sealed record ListWatchesQuery : IQuery<MediaWatchInfo[]>;

/// <summary>Where a media item's file stands.</summary>
public enum MediaFileState
{
    /// <summary>There, and the file it was.</summary>
    Online,

    /// <summary>Not where the project says.</summary>
    Missing,

    /// <summary>There, but not the content it was: replaced, re-encoded or touched.</summary>
    Changed,
}

/// <summary>One media item's file, checked.</summary>
/// <param name="MediaId">The item.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Where the project expects the file.</param>
/// <param name="State">There, missing or changed.</param>
public sealed record MediaCheckInfo(string MediaId, string Name, string Path, MediaFileState State);

/// <summary>How a found file matched a missing one.</summary>
public enum MediaMatch
{
    /// <summary>The same hash: the same file, moved or copied.</summary>
    Hash,

    /// <summary>The same name and size: very likely the same file, touched since.</summary>
    NameAndSize,

    /// <summary>Only the same name: worth a look, not trusted on its own.</summary>
    Name,
}

/// <summary>A file that might be a missing media item's.</summary>
/// <param name="Path">The file.</param>
/// <param name="Match">How it matched.</param>
/// <param name="Bytes">Its size.</param>
public sealed record MediaCandidate(string Path, MediaMatch Match, long Bytes);

/// <summary>A missing media item and the files that might be it, best first.</summary>
/// <param name="MediaId">The item.</param>
/// <param name="Name">Its name.</param>
/// <param name="Expected">Where the project expected it.</param>
/// <param name="Clips">How many clips use it.</param>
/// <param name="Candidates">Files that might be it.</param>
public sealed record MissingMediaInfo(string MediaId, string Name, string Expected, int Clips, EquatableArray<MediaCandidate> Candidates);

/// <summary>What a media item is used for.</summary>
/// <param name="MediaId">The item.</param>
/// <param name="Name">Its name.</param>
/// <param name="Clips">How many clips use it, in every sequence.</param>
/// <param name="Sequences">The sequences they are in, by name.</param>
/// <param name="Used">How much of the file the clips play, overlaps counted once.</param>
/// <param name="Duration">How long the file is.</param>
/// <param name="Bytes">Its size on disk, when known.</param>
public sealed record MediaUsageInfo(string MediaId, string Name, int Clips, EquatableArray<string> Sequences, Flicks Used, Flicks Duration, long Bytes);

/// <summary>A folder the project watches.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Tags">The tags it gives what it brings in.</param>
/// <param name="Bin">The bin folder they go in.</param>
/// <param name="Imported">How many files it has brought in so far.</param>
/// <param name="Waiting">Files seen and still being written.</param>
/// <param name="Watching">True while this process watches it; false for a watch the project keeps that a one-off jazz process, or a missing folder, leaves idle.</param>
public sealed record MediaWatchInfo(string Folder, EquatableArray<string> Tags, string Bin, int Imported, int Waiting, bool Watching = true);
