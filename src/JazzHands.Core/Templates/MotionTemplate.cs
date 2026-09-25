using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Templates;

/// <summary>A value a template is filled in with.</summary>
/// <param name="Type">text, color, number, time, media or clip.</param>
/// <param name="Default">What it is when not given; empty for one that has to be.</param>
/// <param name="Description">What it is for, for a person or Claude filling it in.</param>
public sealed record TemplateParam(string Type = "text", string Default = "", string Description = "");

/// <summary>
/// A motion template: a reusable thing made of commands (a hero intro, an end card, a button pop),
/// with the values that change from one use to the next as its parameters.
/// </summary>
/// <remarks>
/// <para>
/// The steps are the same <c>{ "command", "args" }</c> objects <c>jazz apply</c> runs, with
/// variables in their strings: <c>${at}</c> is the moment the template is placed, <c>${at+1.5}</c>
/// and <c>${at-0.5}</c> that moment moved by seconds, <c>${id:card}</c> a new id made once per use
/// and the same wherever the key is, and <c>${name}</c> a parameter. Everything is written as text,
/// as the command line types it, and read as whatever the command takes: a number, a switch, a time.
/// </para>
/// <para>
/// A template is the unit Claude Code works in when it builds a trailer from a brief: it fills in
/// the parameters rather than issuing the commands one by one. Applying one is one undo.
/// </para>
/// </remarks>
/// <param name="Name">What <c>template.apply</c> takes, in kebab case.</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="Description">What it makes and when to use it.</param>
/// <param name="Params">The values it is filled in with, by name.</param>
/// <param name="Steps">The commands that make it.</param>
public sealed partial record MotionTemplate(
    string Name,
    string Label = "",
    string Description = "",
    ImmutableSortedDictionary<string, TemplateParam>? Params = null,
    JsonArray? Steps = null)
{
    /// <summary>The kinds of value a parameter can be.</summary>
    public static readonly ImmutableArray<string> Types = ["text", "color", "number", "time", "media", "clip", "switch"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The parameters, never null.</summary>
    [JsonIgnore]
    public ImmutableSortedDictionary<string, TemplateParam> Values => Params ?? ImmutableSortedDictionary<string, TemplateParam>.Empty;

    /// <summary>True for one that ships with the editor.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }

    /// <summary>The file a person's template was read from; empty for a built-in.</summary>
    [JsonIgnore]
    public string Source { get; init; } = string.Empty;

    /// <summary>Reads a template, or says what is wrong with it.</summary>
    public static bool TryRead(string json, out MotionTemplate? template, out string? error)
    {
        template = null;
        try
        {
            MotionTemplate? read = JsonSerializer.Deserialize<MotionTemplate>(json, Json);
            error = read is null ? "A template needs a name." : Check(read);
            template = error is null ? read : null;
            return template is not null;
        }
        catch (JsonException exception)
        {
            error = $"It is not a template: {exception.Message}";
            return false;
        }
    }

    /// <summary>What is wrong with a template, or null.</summary>
    public static string? Check(MotionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.Name) || !NamePattern().IsMatch(template.Name))
        {
            return "A template needs a name in kebab case, like hero-intro.";
        }

        foreach ((string name, TemplateParam parameter) in template.Values)
        {
            if (!NamePattern().IsMatch(name) || name is "at")
            {
                return $"'{template.Name}' has a parameter called '{name}'; parameters are named in kebab case and 'at' is the moment it is placed.";
            }

            if (!Types.Contains(parameter.Type))
            {
                return $"'{template.Name}' says '{name}' is a {parameter.Type}; it can be {string.Join(", ", Types)}.";
            }
        }

        if (template.Steps is not { Count: > 0 } steps)
        {
            return $"'{template.Name}' has no steps.";
        }

        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index] is not JsonObject step || step["command"] is not JsonValue value || !value.TryGetValue(out string? command))
            {
                return $"Step {index + 1} of '{template.Name}' is not a {{ \"command\", \"args\" }} object.";
            }

            if (CommandRegistry.Find(command) is not { } metadata || metadata.IsQuery || !metadata.Undoable || command.StartsWith("template.", StringComparison.Ordinal))
            {
                return $"Step {index + 1} of '{template.Name}' runs '{command}', which is not an edit a template can make.";
            }

            foreach (Match match in Strings(step["args"]).SelectMany(text => VariablePattern().Matches(text)))
            {
                string variable = match.Groups[1].Value;
                if (!IsTime(variable) && !variable.StartsWith("id:", StringComparison.Ordinal) && !template.Values.ContainsKey(variable))
                {
                    return $"Step {index + 1} of '{template.Name}' uses ${{{variable}}}, which is not a parameter, a time or an id.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The commands for one use: the values given (or the defaults) checked and put in, the
    /// moment placed, and a new id for each key.
    /// </summary>
    /// <param name="given">Values by parameter name.</param>
    /// <param name="at">Where on the sequence it is placed.</param>
    /// <param name="rate">The sequence's frame rate, which times are read at.</param>
    public IReadOnlyList<ICommand> Expand(IReadOnlyDictionary<string, string> given, Flicks at, Rational rate)
    {
        ArgumentNullException.ThrowIfNull(given);
        foreach (string name in given.Keys)
        {
            if (!Values.ContainsKey(name))
            {
                throw new CommandException("unknown-param", $"'{Name}' has no parameter '{name}'. It has {string.Join(", ", Values.Keys)}.", "param");
            }
        }

        var values = new Dictionary<string, (TemplateParam Param, string Value)>(StringComparer.Ordinal);
        foreach ((string name, TemplateParam parameter) in Values)
        {
            string value = given.TryGetValue(name, out string? text) ? text : parameter.Default;
            if (value.Length == 0 && parameter.Type != "text")
            {
                throw new CommandException("missing-param", $"'{Name}' needs '{name}': {parameter.Description}", "param");
            }

            values[name] = (parameter, CheckValue(name, parameter, value, rate));
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var commands = new List<ICommand>(Steps!.Count);
        foreach (JsonNode? node in Steps!)
        {
            var step = (JsonObject)node!;
            string command = step["command"]!.GetValue<string>();
            JsonObject args = Typed(command, rate, Fill(step["args"]?.DeepClone() as JsonObject ?? [], values, ids, at));
            commands.Add((ICommand)CommandRegistry.FromJson(command, args, rate));
        }

        return commands;
    }

    /// <summary>The template as a file writes it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static string CheckValue(string name, TemplateParam parameter, string value, Rational rate)
    {
        bool fine = parameter.Type switch
        {
            "color" => CommandValues.TryParseColor(value, out _, out _),
            "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "time" => Timecode.TryParse(value, rate, out _),
            "switch" => bool.TryParse(value, out _),
            "media" or "clip" => value.Length > 0,
            _ => true,
        };

        return fine ? value : throw new CommandException("invalid-value", $"'{value}' is not a {parameter.Type} for '{name}'.", "param");
    }

    private static JsonObject Fill(JsonObject args, Dictionary<string, (TemplateParam Param, string Value)> values, Dictionary<string, string> ids, Flicks at)
    {
        foreach ((string key, JsonNode? value) in args.ToArray())
        {
            args[key] = FillNode(value, values, ids, at);
        }

        return args;
    }

    private static JsonNode? FillNode(JsonNode? node, Dictionary<string, (TemplateParam Param, string Value)> values, Dictionary<string, string> ids, Flicks at)
    {
        switch (node)
        {
            case JsonObject inner:
                return Fill(inner, values, ids, at);
            case JsonArray list:
                return new JsonArray([.. list.Select(item => FillNode(item?.DeepClone(), values, ids, at))]);
            case JsonValue text when text.TryGetValue(out string? value):
                return JsonValue.Create(VariablePattern().Replace(value, match => Resolve(match.Groups[1].Value, values, ids, at)));
            default:
                return node;
        }
    }

    /// <summary>Every string in a node, however deep.</summary>
    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject inner => inner.SelectMany(member => Strings(member.Value)),
        JsonArray list => list.SelectMany(Strings),
        JsonValue value when value.TryGetValue(out string? text) => [text],
        _ => [],
    };

    /// <summary>Text given for a number, a switch or a choice, read as one: templates write everything as the command line does.</summary>
    private static JsonObject Typed(string command, Rational rate, JsonObject args)
    {
        CommandMetadata metadata = CommandRegistry.Find(command)!;
        foreach (ParameterMetadata parameter in metadata.Parameters)
        {
            if (args[parameter.JsonName] is not JsonValue value || !value.TryGetValue(out string? text))
            {
                continue;
            }

            Type bare = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
            if (bare.IsEnum)
            {
                // ease-out as the command line writes it, easeOut as the file does.
                args[parameter.JsonName] = JsonSerializer.SerializeToNode(CommandValues.Parse(bare, text, rate, parameter.JsonName), bare, Serialization.JazzJson.Options);
                continue;
            }

            if (bare == typeof(bool) && bool.TryParse(text, out bool flag))
            {
                args[parameter.JsonName] = flag;
            }
            else if ((bare == typeof(int) || bare == typeof(long)) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
            {
                args[parameter.JsonName] = whole;
            }
            else if ((bare == typeof(double) || bare == typeof(float)) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                args[parameter.JsonName] = number;
            }
        }

        return args;
    }

    private static string Resolve(string variable, Dictionary<string, (TemplateParam Param, string Value)> values, Dictionary<string, string> ids, Flicks at)
    {
        if (variable.StartsWith("id:", StringComparison.Ordinal))
        {
            if (!ids.TryGetValue(variable, out string? id))
            {
                id = Id.New();
                ids[variable] = id;
            }

            return id;
        }

        if (IsTime(variable))
        {
            double offset = variable.Length > 2 ? double.Parse(variable[2..], NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
            Flicks moment = at + Flicks.FromSeconds(offset);
            return Timecode.FormatClock(moment < Flicks.Zero ? Flicks.Zero : moment);
        }

        return values[variable].Value;
    }

    private static bool IsTime(string variable) => variable == "at" || TimePattern().IsMatch(variable);

    [GeneratedRegex(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^at[+-][0-9]+(\.[0-9]+)?$")]
    private static partial Regex TimePattern();
}
