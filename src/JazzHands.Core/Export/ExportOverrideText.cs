using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Export;

/// <summary>
/// Turns the override options a command takes (<c>--size</c>, <c>--bitrate 8M</c>,
/// <c>--target-size 8MB</c>) into <see cref="ExportOverrides"/>, and lays overrides over a preset.
/// </summary>
/// <remarks>
/// <c>export.enqueue</c>, <c>export.plan</c> and <c>presets.save</c> take the same options, so a set
/// that worked for one export can be saved as a preset and means the same thing there.
/// </remarks>
public static class ExportOverrideText
{
    /// <summary>The overrides some options ask for, or null when they ask for none.</summary>
    /// <exception cref="CommandException">When a size or bitrate does not read.</exception>
    public static ExportOverrides? Parse(
        FrameSize? size,
        Rational? frameRate,
        int? quality,
        string? bitrate,
        EquatableArray<string> encoders,
        string? audioEncoder,
        string? audioBitrate,
        int? channels,
        double? loudness,
        string? targetSize)
    {
        var overrides = new ExportOverrides(
            size?.Width ?? 0,
            size?.Height ?? 0,
            frameRate,
            quality,
            Bitrate(bitrate, "bitrate"),
            encoders,
            string.IsNullOrWhiteSpace(audioEncoder) ? null : audioEncoder.Trim(),
            Bitrate(audioBitrate, "audio-bitrate"),
            channels ?? 0,
            loudness,
            Bytes(targetSize));

        if (overrides.Channels is not (0 or 1 or 2 or 6))
        {
            throw new CommandException("invalid-value", $"'channels' is 1, 2 or 6, not {overrides.Channels}.");
        }

        if (overrides.Quality is < 0)
        {
            throw new CommandException("invalid-value", "'quality' is a CRF or CQ of zero or more.");
        }

        if (overrides.FrameRate is { } rate && (rate.Num <= 0 || rate.Den <= 0))
        {
            throw new CommandException("invalid-value", $"'fps' is a frame rate, not {rate}.");
        }

        if (overrides.Loudness is { } lufs && (lufs < -70 || lufs > 0))
        {
            throw new CommandException("invalid-value", string.Create(CultureInfo.InvariantCulture, $"'loudness' is between -70 and 0 LUFS, not {lufs}."));
        }

        return overrides.IsEmpty ? null : overrides;
    }

    /// <summary>A stretch of the sequence from optional start and end times, or null for all of it.</summary>
    public static TimeRange? Range(Flicks? start, Flicks? end)
    {
        if (start is null && end is null)
        {
            return null;
        }

        Flicks from = start ?? Flicks.Zero;
        Flicks to = end ?? Flicks.FromSeconds(1_000_000);
        if (to <= from)
        {
            throw new CommandException("invalid-range", $"The export ends at {Timecode.FormatClock(to)}, before it starts at {Timecode.FormatClock(from)}.");
        }

        return TimeRange.FromBounds(from, to);
    }

    /// <summary>A preset with overrides laid over it.</summary>
    public static ExportPreset Apply(ExportPreset preset, ExportOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (overrides is null || overrides.IsEmpty)
        {
            return preset;
        }

        ExportPresetVideo? video = preset.Video;
        if (video is not null)
        {
            video = video with
            {
                MaxWidth = overrides.MaxWidth > 0 ? overrides.MaxWidth : overrides.MaxHeight > 0 ? 0 : video.MaxWidth,
                MaxHeight = overrides.MaxHeight > 0 ? overrides.MaxHeight : overrides.MaxWidth > 0 ? 0 : video.MaxHeight,
                MaxFrameRate = overrides.FrameRate ?? video.MaxFrameRate,
                Quality = overrides.Quality ?? video.Quality,
                Bitrate = overrides.Bitrate > 0 ? overrides.Bitrate : overrides.Quality is not null ? 0 : video.Bitrate,
                Encoders = overrides.Encoders.IsEmpty ? video.Encoders : overrides.Encoders,
            };
        }

        ExportPresetAudio? audio = preset.Audio;
        if (audio is not null)
        {
            audio = audio with
            {
                Encoder = overrides.AudioEncoder ?? audio.Encoder,
                Bitrate = overrides.AudioBitrate > 0 ? overrides.AudioBitrate : overrides.AudioEncoder is not null ? 0 : audio.Bitrate,
                Channels = overrides.Channels > 0 ? overrides.Channels : audio.Channels,
            };
        }

        return preset with
        {
            Video = video,
            Audio = audio,
            TargetBytes = overrides.TargetBytes > 0 ? overrides.TargetBytes : overrides.Bitrate > 0 || overrides.Quality is not null ? 0 : preset.TargetBytes,
            Loudness = overrides.Loudness ?? preset.Loudness,
        };
    }

    private static long Bitrate(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return ExportPresets.TryParseBitrate(text, out long bits)
            ? bits
            : throw new CommandException("invalid-value", $"'{name}' takes a bitrate like 8M, 2500k or 320000, not '{text}'.");
    }

    private static long Bytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return ExportPresets.TryParseBytes(text, out long bytes)
            ? bytes
            : throw new CommandException("invalid-value", $"'target-size' takes a size like 8MB, 25MB or 1.5GB, not '{text}'.");
    }
}
