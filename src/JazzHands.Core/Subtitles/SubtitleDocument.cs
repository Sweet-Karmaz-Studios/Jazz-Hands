using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Subtitles;

/// <summary>The subtitle file formats Jazz Hands reads and writes.</summary>
public enum SubtitleFormat
{
    /// <summary>SubRip: numbered cues and a few HTML tags.</summary>
    Srt,

    /// <summary>WebVTT, the web's format.</summary>
    Vtt,

    /// <summary>Advanced SubStation Alpha: styles and override tags.</summary>
    Ass,
}

/// <summary>One subtitle: what it says and when.</summary>
/// <param name="Start">When it appears.</param>
/// <param name="End">When it goes.</param>
/// <param name="Text">What it says, as title markup (<c>[b]</c>, <c>[i]</c>, <c>[color=#FFCC00]</c>, <c>\n</c>).</param>
/// <param name="Align">Where on the frame; bottom centre unless the file said otherwise.</param>
/// <param name="Style">The ASS style it named, kept for writing ASS back.</param>
/// <param name="Name">A WebVTT cue id or an ASS speaker name, kept for writing back.</param>
/// <param name="Raw">
/// What the file said that the markup cannot hold, kept for writing the same format back: an ASS
/// line's text with its override tags, a WebVTT cue's settings.
/// </param>
public sealed record SubtitleCue(
    Flicks Start,
    Flicks End,
    string Text,
    SubtitleAlign Align = SubtitleAlign.Bottom,
    string? Style = null,
    string? Name = null,
    string? Raw = null)
{
    /// <summary>How long it shows.</summary>
    public Flicks Duration => End - Start;
}

/// <summary>A subtitle file read into cues, with the style it declared and what could not be kept.</summary>
/// <param name="Cues">The cues, in the order of their start times.</param>
/// <param name="Style">The file's main style, when it declared one (an ASS <c>Default</c> style, a WebVTT <c>::cue</c> block).</param>
/// <param name="Warnings">What was skipped or will not show as written, once each.</param>
/// <param name="Header">What an ASS file said before its events, kept for writing it back.</param>
public sealed record SubtitleDocument(
    ImmutableArray<SubtitleCue> Cues,
    SubtitleStyle? Style = null,
    ImmutableArray<string> Warnings = default,
    string? Header = null)
{
    /// <summary>The warnings, empty rather than default.</summary>
    public ImmutableArray<string> AllWarnings => Warnings.IsDefault ? [] : Warnings;
}

/// <summary>A subtitle file that could not be read at all.</summary>
public sealed class SubtitleFormatException(string message) : Exception(message);

/// <summary>Reads and writes subtitle files by format.</summary>
public static class SubtitleFiles
{
    /// <summary>The format a file name says, or null.</summary>
    public static SubtitleFormat? FormatOf(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".SRT" => SubtitleFormat.Srt,
            ".VTT" => SubtitleFormat.Vtt,
            ".ASS" or ".SSA" => SubtitleFormat.Ass,
            _ => null,
        };

    /// <summary>The format a file's text says, for text with no name: a WebVTT header, an ASS section, or SubRip.</summary>
    public static SubtitleFormat Sniff(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string start = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (start.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return SubtitleFormat.Vtt;
        }

        return start.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) || text.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
            ? SubtitleFormat.Ass
            : SubtitleFormat.Srt;
    }

    /// <summary>The usual file extension for a format, with its dot.</summary>
    public static string Extension(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Vtt => ".vtt",
        SubtitleFormat.Ass => ".ass",
        _ => ".srt",
    };

    /// <summary>Reads a file's text in a format.</summary>
    public static SubtitleDocument Parse(string text, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Vtt => VttFormat.Parse(text),
        SubtitleFormat.Ass => AssFormat.Parse(text),
        _ => SrtFormat.Parse(text),
    };

    /// <summary>Writes a document in a format.</summary>
    public static string Write(SubtitleDocument document, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Vtt => VttFormat.Write(document),
        SubtitleFormat.Ass => AssFormat.Write(document),
        _ => SrtFormat.Write(document),
    };

    /// <summary>Line breaks made one kind and a byte order mark taken off.</summary>
    internal static string[] Lines(string text) =>
        text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Cues put in start order, keeping the file's order for cues that start together.</summary>
    internal static ImmutableArray<SubtitleCue> Ordered(IEnumerable<SubtitleCue> cues) =>
        [.. cues.Select((cue, index) => (cue, index)).OrderBy(entry => entry.cue.Start).ThenBy(entry => entry.index).Select(entry => entry.cue)];

    /// <summary>
    /// A time as <c>HH:MM:SS,mmm</c> (SubRip, with a comma), <c>HH:MM:SS.mmm</c> (WebVTT) or
    /// <c>H:MM:SS.cc</c> (ASS, in hundredths).
    /// </summary>
    internal static string Clock(Flicks time, SubtitleFormat format)
    {
        bool hundredths = format == SubtitleFormat.Ass;
        long perSecond = hundredths ? 100 : 1000;
        long ticks = (long)Math.Round((double)Math.Max(0, time.Value) * perSecond / Flicks.PerSecond, MidpointRounding.AwayFromZero);
        long seconds = ticks / perSecond;
        long part = ticks % perSecond;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        return format switch
        {
            SubtitleFormat.Ass => string.Create(invariant, $"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}.{part:D2}"),
            SubtitleFormat.Vtt => string.Create(invariant, $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}.{part:D3}"),
            _ => string.Create(invariant, $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2},{part:D3}"),
        };
    }

    /// <summary>
    /// Reads a clock time leniently: <c>1:02:03,5</c>, <c>01:02:03.500</c>, <c>02:03.500</c>; the
    /// fraction is read as a decimal whatever its length. Null when it is not a time.
    /// </summary>
    internal static Flicks? ReadClock(string text)
    {
        string trimmed = text.Trim();
        string[] parts = trimmed.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        string last = parts[^1].Replace(',', '.');
        if (!long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long first)
            || (parts.Length == 3 && !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            || !decimal.TryParse(last, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out decimal seconds))
        {
            return null;
        }

        long hours = parts.Length == 3 ? first : 0;
        long minutes = parts.Length == 3 ? long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : first;
        decimal total = (hours * 3600m) + (minutes * 60m) + seconds;
        return new Flicks((long)Math.Round(total * Flicks.PerMillisecond * 1000m, MidpointRounding.AwayFromZero));
    }
}
