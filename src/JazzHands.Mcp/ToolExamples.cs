using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Mcp;

/// <summary>
/// An example call for every tool, for <c>jazz://docs</c>: written out for the tools a model
/// reaches for most, made up from the parameters for the rest. A test builds every registry
/// example into its command, so none of them is a lie.
/// </summary>
public static class ToolExamples
{
    /// <summary>Ids that look like the real thing, one per kind, so an example reads as a call would.</summary>
    public static class Ids
    {
        /// <summary>A clip.</summary>
        public const string Clip = "01J9Z3K4M5N6P7Q8R9S0T1V2CA";

        /// <summary>Another clip.</summary>
        public const string OtherClip = "01J9Z3K4M5N6P7Q8R9S0T1V2CB";

        /// <summary>A track.</summary>
        public const string Track = "01J9Z3K4M5N6P7Q8R9S0T1V2TK";

        /// <summary>A media item.</summary>
        public const string Media = "01J9Z3K4M5N6P7Q8R9S0T1V2MD";

        /// <summary>A sequence.</summary>
        public const string Sequence = "01J9Z3K4M5N6P7Q8R9S0T1V2SQ";

        /// <summary>A marker.</summary>
        public const string Marker = "01J9Z3K4M5N6P7Q8R9S0T1V2MK";

        /// <summary>An effect.</summary>
        public const string Effect = "01J9Z3K4M5N6P7Q8R9S0T1V2EF";

        /// <summary>A transition.</summary>
        public const string Transition = "01J9Z3K4M5N6P7Q8R9S0T1V2TR";

        /// <summary>A mask.</summary>
        public const string Mask = "01J9Z3K4M5N6P7Q8R9S0T1V2MS";

        /// <summary>An export job.</summary>
        public const string Job = "01J9Z3K4M5N6P7Q8R9S0T1V2JB";

        /// <summary>Anything else.</summary>
        public const string Other = "01J9Z3K4M5N6P7Q8R9S0T1V2XX";
    }

    /// <summary>The examples written out by hand, by tool name.</summary>
    public static ImmutableDictionary<string, string> Curated { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["media_add"] = """{"paths": ["C:\\captures\\boss-fight.mp4", "C:\\captures\\music.wav"]}""",
        ["clip_add"] = $$"""{"trackId": "{{Ids.Track}}", "at": "00:00:04.000", "mediaId": "{{Ids.Media}}", "sourceIn": "00:01:10.000", "duration": "12.5s", "name": "boss hit"}""",
        ["clip_split"] = $$"""{"clipId": "{{Ids.Clip}}", "at": "00:00:02.000"}""",
        ["clip_move"] = $$"""{"clipId": "{{Ids.Clip}}", "to": "00:00:10.000", "toTrackId": "{{Ids.Track}}"}""",
        ["clip_trim"] = $$"""{"clipId": "{{Ids.Clip}}", "out": "00:00:08.000"}""",
        ["clip_remove"] = $$"""{"clipId": "{{Ids.Clip}}", "ripple": true}""",
        ["clip_set_transform"] = $$"""{"clipId": "{{Ids.Clip}}", "x": 0.25, "y": -0.1, "scale": 0.5}""",
        ["clip_set_speed"] = $$"""{"clipId": "{{Ids.Clip}}", "speed": 2.0}""",
        ["clip_set_opacity"] = $$"""{"clipId": "{{Ids.Clip}}", "opacity": 0.5}""",
        ["track_add"] = """{"kind": "video", "name": "Titles"}""",
        ["title_add"] = """{"at": "00:00:20.000", "text": "[b]Wishlist now[/b]", "preset": "lower-third", "duration": "4s"}""",
        ["title_set_text"] = $$"""{"clipId": "{{Ids.Clip}}", "text": "Out now"}""",
        ["transition_add"] = $$"""{"leftClipId": "{{Ids.Clip}}", "rightClipId": "{{Ids.OtherClip}}", "type": "transition.crossfade", "duration": "12f"}""",
        ["audio_set_gain"] = $$"""{"clipId": "{{Ids.Clip}}", "db": -6}""",
        ["audio_set_fade_in"] = $$"""{"clipId": "{{Ids.Clip}}", "duration": "0.5s"}""",
        ["audio_mute_stream"] = $$"""{"clipId": "{{Ids.Clip}}", "stream": 1}""",
        ["marker_add"] = """{"at": "00:00:30.000", "name": "Drop", "color": "red"}""",
        ["effect_add"] = $$"""{"ownerId": "{{Ids.Clip}}", "typeId": "video.blur.gaussian"}""",
        ["param_set"] = $$"""{"ownerId": "{{Ids.Clip}}", "param": "opacity", "value": "0.8", "at": "00:00:03.000"}""",
        ["keyframe_add"] = $$"""{"ownerId": "{{Ids.Clip}}", "param": "opacity", "at": "00:00:03.000", "value": "0"}""",
        ["export_enqueue"] = """{"output": "C:\\exports\\trailer.mp4", "preset": "youtube-1080p"}""",
        ["project_save"] = "{}",
        ["undo"] = "{}",
        ["redo"] = "{}",
        ["describe_timeline"] = """{"detail": "brief"}""",
        ["render_frame"] = """{"at": "00:00:12.500", "width": 960}""",
        ["contact_sheet"] = """{"columns": 4, "rows": 3, "width": 1440}""",
        ["render_proof"] = """{"out": "proof.mp4", "start": "00:00:00.000", "end": "00:00:30.000"}""",
        ["probe_media"] = """{"path": "C:\\captures\\boss-fight.mp4"}""",
        ["list_effects"] = "{}",
        ["list_presets"] = "{}",
        ["list_fonts"] = "{}",
        ["list_title_presets"] = "{}",
        ["apply_batch"] = $$$"""
            {"label": "Intro", "steps": [
              {"command": "clip.add", "args": {"trackId": "{{{Ids.Track}}}", "at": "0s", "mediaId": "{{{Ids.Media}}}", "duration": "4s"}, "as": "shot"},
              {"command": "clip.set-opacity", "args": {"clipId": "$shot.id", "opacity": 0.8}},
              {"command": "title.add", "args": {"at": "1s", "text": "Chapter one", "preset": "title-card"}}
            ]}
            """,
        ["wait_export"] = $$"""{"jobId": "{{Ids.Job}}"}""",
        ["history"] = """{"limit": 10}""",
        ["session_info"] = "{}",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>An example for a registry entry: the curated one, or one made from its parameters.</summary>
    public static JsonObject For(CommandMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Curated.TryGetValue(ToolSchema.ToolName(metadata.Name), out string? written) && Fits(metadata, written))
        {
            return JsonNode.Parse(written)!.AsObject();
        }

        var example = new JsonObject();
        foreach (ParameterMetadata parameter in metadata.Parameters.Where(parameter => parameter.IsRequired))
        {
            example[parameter.JsonName] = Sample(parameter.Type, parameter.JsonName);
        }

        return example;
    }

    /// <summary>A plausible value of a type for a parameter of a name.</summary>
    public static JsonNode? Sample(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(name);
        Type bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(string))
        {
            return JsonValue.Create(Text(name));
        }

        if (bare == typeof(bool))
        {
            return JsonValue.Create(true);
        }

        if (bare == typeof(int) || bare == typeof(long) || bare == typeof(short) || bare == typeof(uint))
        {
            return JsonValue.Create(name.Contains("stream", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        }

        if (bare == typeof(double) || bare == typeof(float))
        {
            return JsonValue.Create(0.5);
        }

        if (bare == typeof(Flicks))
        {
            return JsonValue.Create(name.Contains("dur", StringComparison.OrdinalIgnoreCase) || name.Contains("by", StringComparison.OrdinalIgnoreCase) ? "2s" : "00:00:02.000");
        }

        if (bare == typeof(TimeRange))
        {
            return JsonValue.Create("00:00:10.000-00:00:25.000");
        }

        if (bare == typeof(Rational))
        {
            return JsonValue.Create("30000/1001");
        }

        if (bare == typeof(FrameSize))
        {
            return JsonValue.Create("1920x1080");
        }

        if (bare.IsEnum)
        {
            return JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(Enum.GetNames(bare)[0]));
        }

        if (bare == typeof(ICommand[]))
        {
            return new JsonArray(new JsonObject
            {
                ["command"] = "clip.set-opacity",
                ["args"] = new JsonObject { ["clipId"] = Ids.Clip, ["opacity"] = 0.5 },
            });
        }

        if (bare.IsArray || (bare.IsGenericType && (bare.GetGenericTypeDefinition() == typeof(EquatableArray<>) || bare.GetGenericTypeDefinition() == typeof(ImmutableArray<>))))
        {
            Type item = bare.IsArray ? bare.GetElementType()! : bare.GetGenericArguments()[0];
            string single = name.EndsWith("Ids", StringComparison.Ordinal) ? name[..^1] : name.TrimEnd('s');
            return new JsonArray(Sample(item, single));
        }

        return new JsonObject();
    }

    private static bool Fits(CommandMetadata metadata, string written)
    {
        JsonObject example = JsonNode.Parse(written)!.AsObject();
        HashSet<string> known = [.. metadata.Parameters.Select(parameter => parameter.JsonName)];
        return example.All(pair => known.Contains(pair.Key))
            && metadata.Parameters.Where(parameter => parameter.IsRequired).All(parameter => example.ContainsKey(parameter.JsonName));
    }

    private static string Text(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower switch
        {
            "clipid" or "fromclipid" or "leftclipid" => Ids.Clip,
            "toclipid" or "rightclipid" or "otherclipid" => Ids.OtherClip,
            "trackid" or "totrackid" => Ids.Track,
            "mediaid" => Ids.Media,
            "sequenceid" => Ids.Sequence,
            "markerid" => Ids.Marker,
            "effectid" => Ids.Effect,
            "transitionid" => Ids.Transition,
            "maskid" => Ids.Mask,
            "jobid" => Ids.Job,
            "target" or "targetid" or "ownerid" => Ids.Clip,
            "typeid" => "video.blur.gaussian",
            "output" => "C:\\exports\\trailer.mp4",
            "path" or "file" or "source" => "C:\\captures\\boss-fight.mp4",
            "name" or "label" => "Intro",
            "text" => "Wishlist now",
            "color" => "#FFCC00",
            "font" => "Segoe UI",
            "language" => "eng",
            "preset" => "youtube-1080p",
            "type" => "transition.crossfade",
            "kind" => "video",
            "param" => "opacity",
            "value" => "0.5",
            "json" => "{}",
            "find" => "colour",
            "replace" or "with" => "color",
            _ when lower.EndsWith("id", StringComparison.Ordinal) => Ids.Other,
            _ => "example",
        };
    }
}
