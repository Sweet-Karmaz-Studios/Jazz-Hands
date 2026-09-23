using System.Text.Json.Nodes;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What a query says about the project as a whole.</summary>
/// <param name="Id">The project identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="SchemaVersion">The file format version.</param>
/// <param name="Fps">The default frame rate.</param>
/// <param name="Width">The default frame width.</param>
/// <param name="Height">The default frame height.</param>
/// <param name="SampleRate">The default audio sample rate.</param>
/// <param name="ChannelCount">The default audio channel count.</param>
/// <param name="ColorSpace">The working colour space.</param>
/// <param name="MediaCount">How many files the project references.</param>
/// <param name="SequenceCount">How many timelines it holds.</param>
/// <param name="ActiveSequenceId">Which sequence the editor is showing.</param>
/// <param name="Duration">How long the active sequence runs.</param>
/// <param name="Path">Where it was loaded from, or empty when it has never been saved.</param>
/// <param name="IsDirty">True when there are changes the file does not have.</param>
public sealed record ProjectInfo(
    string Id,
    string Name,
    int SchemaVersion,
    Rational Fps,
    int Width,
    int Height,
    int SampleRate,
    int ChannelCount,
    string ColorSpace,
    int MediaCount,
    int SequenceCount,
    string? ActiveSequenceId,
    Flicks Duration,
    string Path,
    bool IsDirty);

/// <summary>What a query says about one sequence.</summary>
/// <param name="Id">The sequence identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="TrackCount">How many tracks it has.</param>
/// <param name="ClipCount">How many clips across all its tracks.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="IsActive">True when the editor is showing it.</param>
/// <param name="Fps">The frame rate it renders at, its own or the project's.</param>
/// <param name="Width">The frame width it renders at.</param>
/// <param name="Height">The frame height it renders at.</param>
/// <param name="HasOwnSettings">True when it overrides the project's settings.</param>
public sealed record SequenceInfo(
    string Id,
    string Name,
    int TrackCount,
    int ClipCount,
    Flicks Duration,
    bool IsActive,
    Rational Fps,
    int Width,
    int Height,
    bool HasOwnSettings);

/// <summary>What a query says about one track.</summary>
/// <param name="Id">The track identifier.</param>
/// <param name="SequenceId">The sequence it belongs to.</param>
/// <param name="Kind">What it carries.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Order">Where it sits in the stack.</param>
/// <param name="ClipCount">How many clips it holds.</param>
/// <param name="Duration">Where its last clip ends.</param>
/// <param name="Locked">True when it refuses edits.</param>
/// <param name="Muted">True when it does not contribute.</param>
/// <param name="Solo">True when it is soloed.</param>
/// <param name="Audible">True when it will actually be heard or seen, given mute and solo.</param>
/// <param name="Height">How tall it is drawn.</param>
/// <param name="Color">Its colour on the timeline.</param>
public sealed record TrackInfo(
    string Id,
    string SequenceId,
    TrackKind Kind,
    string Name,
    int Order,
    int ClipCount,
    Flicks Duration,
    bool Locked,
    bool Muted,
    bool Solo,
    bool Audible,
    double Height,
    string Color);

/// <summary>What a query says about one clip.</summary>
/// <param name="Id">The clip identifier.</param>
/// <param name="TrackId">The track it is on.</param>
/// <param name="SequenceId">The sequence that track belongs to.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Start">Where it starts on the timeline.</param>
/// <param name="Duration">How long it occupies.</param>
/// <param name="End">The first position after it.</param>
/// <param name="SourceIn">Where playback starts inside the source.</param>
/// <param name="SourceOut">The first source position after it.</param>
/// <param name="MediaId">The media item, when it plays a file.</param>
/// <param name="GeneratorId">The generator type, when it is synthetic.</param>
/// <param name="NestedSequenceId">The nested sequence, when it is a compound.</param>
/// <param name="Speed">Its playback rate.</param>
/// <param name="Reverse">True when it plays backwards.</param>
/// <param name="Enabled">False when it is on the timeline but not rendering.</param>
/// <param name="BlendMode">How it combines with what is underneath.</param>
/// <param name="LinkGroupId">Clips sharing this move together.</param>
/// <param name="GroupId">Clips sharing this are selected together.</param>
/// <param name="EffectCount">How many effects it carries.</param>
/// <param name="MarkerCount">How many markers it carries.</param>
public sealed record ClipInfo(
    string Id,
    string TrackId,
    string SequenceId,
    string Name,
    Flicks Start,
    Flicks Duration,
    Flicks End,
    Flicks SourceIn,
    Flicks SourceOut,
    string? MediaId,
    string? GeneratorId,
    string? NestedSequenceId,
    Rational Speed,
    bool Reverse,
    bool Enabled,
    BlendMode BlendMode,
    string? LinkGroupId,
    string? GroupId,
    int EffectCount,
    int MarkerCount);

/// <summary>What a marker is attached to.</summary>
public enum MarkerOwner
{
    /// <summary>The marker sits on a sequence, at an absolute timeline time.</summary>
    Sequence,

    /// <summary>The marker sits on a clip, at a time relative to the clip start.</summary>
    Clip,
}

/// <summary>What a query says about one marker.</summary>
/// <param name="Id">The marker identifier.</param>
/// <param name="OwnerKind">Whether it sits on a sequence or on a clip.</param>
/// <param name="OwnerId">The sequence or clip it sits on.</param>
/// <param name="Time">Where it sits, relative to its owner.</param>
/// <param name="TimelineTime">Where it sits on the sequence timeline.</param>
/// <param name="Duration">Non-zero for a range marker.</param>
/// <param name="Name">Its label.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Note">Longer text.</param>
/// <param name="IsChapter">True when it exports as a chapter.</param>
public sealed record MarkerInfo(
    string Id,
    MarkerOwner OwnerKind,
    string OwnerId,
    Flicks Time,
    Flicks TimelineTime,
    Flicks Duration,
    string Name,
    string Color,
    string Note,
    bool IsChapter);

/// <summary>One entry in the undo history.</summary>
/// <param name="Index">Its position, counting from the oldest.</param>
/// <param name="Command">The command name.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="Label">A short description, for the undo menu.</param>
/// <param name="IsUndone">True when it has been taken back and could be redone.</param>
/// <param name="At">When it ran.</param>
public sealed record HistoryInfo(
    int Index,
    string Command,
    JsonObject Args,
    string Label,
    bool IsUndone,
    DateTimeOffset At);
