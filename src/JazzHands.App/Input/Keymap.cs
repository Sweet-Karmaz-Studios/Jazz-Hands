using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.Input;

/// <summary>A key and the modifiers held with it.</summary>
/// <param name="Key">The key.</param>
/// <param name="Modifiers">Ctrl, Shift, Alt.</param>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    /// <summary>Reads "Ctrl+Shift+K", "Delete", "," and the like.</summary>
    public static KeyChord Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // A lone "+" would split into nothing, and "Shift++" into an empty key; neither is bound.
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;

        for (int index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "SHIFT" => ModifierKeys.Shift,
                "ALT" => ModifierKeys.Alt,
                "WIN" => ModifierKeys.Windows,
                _ => throw new FormatException($"'{parts[index]}' in '{text}' is not a modifier."),
            };
        }

        return new KeyChord(KeyOf(parts[^1], text), modifiers);
    }

    private static Key KeyOf(string name, string text) => name switch
    {
        "," => Key.OemComma,
        "." => Key.OemPeriod,
        "/" => Key.OemQuestion,
        ";" => Key.OemSemicolon,
        "'" => Key.OemQuotes,
        "[" => Key.OemOpenBrackets,
        "]" => Key.OemCloseBrackets,
        "-" => Key.OemMinus,
        "=" => Key.OemPlus,
        "`" => Key.OemTilde,
        "\\" => Key.OemBackslash,
        _ when name.Length == 1 && char.IsAsciiDigit(name[0]) => Key.D0 + (name[0] - '0'),
        _ when Enum.TryParse(name, ignoreCase: true, out Key key) => key,
        _ => throw new FormatException($"'{name}' in '{text}' is not a key."),
    };
}

/// <summary>One binding: a key, the command it sends, and that command's arguments.</summary>
/// <param name="Keys">As written, for messages.</param>
/// <param name="Gesture">The key and modifiers.</param>
/// <param name="Command">The command name, as the CLI and JSON-RPC spell it.</param>
/// <param name="Args">Its arguments, by JSON name; string values starting with $ are filled in when the key is pressed.</param>
/// <param name="Repeat">True when holding the key down repeats it.</param>
public sealed record KeymapBinding(string Keys, KeyChord Gesture, string Command, JsonObject Args, bool Repeat);

/// <summary>What a key press is resolved against: where the playhead is and what is selected.</summary>
/// <param name="Project">The project as it is now.</param>
/// <param name="Playhead">The playhead.</param>
/// <param name="Selection">The selected clip and marker ids, in the order they were selected.</param>
public sealed record KeymapContext(Project Project, Flicks Playhead, IReadOnlyList<string> Selection);

/// <summary>
/// The key bindings: which key sends which command.
/// </summary>
/// <remarks>
/// The defaults are embedded (<c>Input/keymap.json</c>); a <c>keymap.json</c> in
/// <c>%APPDATA%\JazzHands</c> with the same shape overrides them a key at a time. Every binding
/// names a command from the registry, so a key does nothing the CLI cannot, and the arguments
/// are the command's JSON arguments with a few values filled in at the moment of the press:
/// <c>$playhead</c>, <c>$in</c>, <c>$out</c>, <c>$selection</c>, <c>$clipsAtPlayhead</c> and <c>$allClips</c>. A command
/// that takes one clip given several runs once for each, as one undo step.
/// </remarks>
public sealed class Keymap
{
    private const string Resource = "JazzHands.App.Input.keymap.json";

    private readonly Dictionary<KeyChord, KeymapBinding> _bindings;

    private Keymap(Dictionary<KeyChord, KeymapBinding> bindings) => _bindings = bindings;

    /// <summary>Every binding, in no particular order.</summary>
    public IEnumerable<KeymapBinding> Bindings => _bindings.Values;

    /// <summary>Where a person's own bindings live.</summary>
    public static string UserPath { get; } = Path.Combine(
        JazzHands.Core.JazzFolders.Roaming,
        "keymap.json");

    /// <summary>The embedded defaults.</summary>
    public static Keymap Defaults()
    {
        using Stream stream = typeof(Keymap).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The default keymap '{Resource}' is not embedded.");
        using var reader = new StreamReader(stream);

        return new Keymap(Read(reader.ReadToEnd(), "the default keymap"));
    }

    /// <summary>The defaults, with a user file laid over them when there is one.</summary>
    /// <param name="userPath">The user's keymap, or null for the usual place.</param>
    /// <param name="problems">What was wrong with the user's file, if anything; those bindings are skipped.</param>
    public static Keymap Load(string? userPath, out IReadOnlyList<string> problems)
    {
        Keymap keymap = Defaults();
        var found = new List<string>();
        string path = userPath ?? UserPath;

        if (File.Exists(path))
        {
            try
            {
                foreach (KeymapBinding binding in Read(File.ReadAllText(path), path, found).Values)
                {
                    if (binding.Command.Length == 0)
                    {
                        keymap._bindings.Remove(binding.Gesture);
                    }
                    else
                    {
                        keymap._bindings[binding.Gesture] = binding;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                found.Add($"{path} could not be read: {exception.Message}. The defaults are in use.");
            }
        }

        problems = found;
        return keymap;
    }

    /// <summary>A keymap of exactly these bindings; a later binding of a key wins.</summary>
    public static Keymap From(IEnumerable<KeymapBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var map = new Dictionary<KeyChord, KeymapBinding>();
        foreach (KeymapBinding binding in bindings.Where(binding => binding.Command.Length > 0))
        {
            map[binding.Gesture] = binding;
        }

        return new Keymap(map);
    }

    /// <summary>What a binding's command does, in a few words: the registry's description, or the editor action's name.</summary>
    public static string Describe(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (UiActions.Describe(command) is { } action)
        {
            return action;
        }

        return CommandRegistry.Find(command)?.Description ?? command;
    }

    /// <summary>
    /// Writes a person's keymap file holding only what differs from the defaults: bindings that
    /// are new or changed, and an empty command for each default key that was freed. The defaults
    /// can then change in a later version without a person's file hiding the change.
    /// </summary>
    public static void SaveUser(string path, IEnumerable<KeymapBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bindings);

        Dictionary<KeyChord, KeymapBinding> defaults = Defaults()._bindings;
        Dictionary<KeyChord, KeymapBinding> wanted = From(bindings)._bindings;
        var entries = new JsonArray();

        foreach (KeymapBinding binding in wanted.Values.OrderBy(binding => binding.Keys, StringComparer.Ordinal))
        {
            if (!defaults.TryGetValue(binding.Gesture, out KeymapBinding? original) || !Same(original, binding))
            {
                entries.Add(ToJson(binding));
            }
        }

        foreach (KeymapBinding freed in defaults.Values.Where(binding => !wanted.ContainsKey(binding.Gesture)).OrderBy(binding => binding.Keys, StringComparer.Ordinal))
        {
            entries.Add(new JsonObject { ["keys"] = freed.Keys, ["command"] = string.Empty });
        }

        Write(path, entries, "Key bindings that differ from Jazz Hands' defaults. An empty command frees a key.");
    }

    /// <summary>Writes every binding to a file another machine can import.</summary>
    public static void Export(string path, IEnumerable<KeymapBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bindings);
        Write(path, new JsonArray([.. From(bindings)._bindings.Values.OrderBy(binding => binding.Keys, StringComparer.Ordinal).Select(binding => (JsonNode?)ToJson(binding))]), "A Jazz Hands keymap.");
    }

    /// <summary>One binding from its parts, checked as a keymap file's line is.</summary>
    /// <exception cref="FormatException">The keys or the command are not ones a binding can have.</exception>
    public static KeymapBinding Bind(string keys, string command, JsonObject? args = null, bool repeat = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        KeyChord gesture = KeyChord.Parse(keys);
        if (UiActions.IsAction(command) ? !UiActions.Known.Contains(command) : CommandRegistry.Find(command) is null)
        {
            throw new FormatException($"There is no command called '{command}'.");
        }

        return new KeymapBinding(keys.Trim(), gesture, command, args is null ? [] : (JsonObject)args.DeepClone(), repeat);
    }

    /// <summary>True when two bindings send the same command with the same arguments.</summary>
    public static bool Same(KeymapBinding left, KeymapBinding right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Gesture == right.Gesture
            && string.Equals(left.Command, right.Command, StringComparison.Ordinal)
            && left.Repeat == right.Repeat
            && JsonNode.DeepEquals(left.Args, right.Args);
    }

    private static JsonObject ToJson(KeymapBinding binding)
    {
        var entry = new JsonObject { ["keys"] = binding.Keys, ["command"] = binding.Command };
        if (binding.Args.Count > 0)
        {
            entry["args"] = binding.Args.DeepClone();
        }

        if (binding.Repeat)
        {
            entry["repeat"] = true;
        }

        return entry;
    }

    private static void Write(string path, JsonArray entries, string comment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var document = new JsonObject { ["comment"] = comment, ["bindings"] = entries };
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The binding for a key, when there is one.</summary>
    public KeymapBinding? Find(Key key, ModifierKeys modifiers) =>
        _bindings.GetValueOrDefault(new KeyChord(key, modifiers));

    /// <summary>
    /// Turns a binding into the command to send, filling in its <c>$</c> values.
    /// </summary>
    /// <returns>The command, or null with a reason when there is nothing to do.</returns>
    public static ICommand? Resolve(KeymapBinding binding, KeymapContext context, out string? nothing)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(context);

        nothing = null;
        CommandMetadata metadata = CommandRegistry.Require(binding.Command);
        var args = new JsonObject();
        ImmutableArray<string>? fanOut = null;
        string? fanOutName = null;

        foreach ((string name, JsonNode? value) in binding.Args)
        {
            ParameterMetadata? parameter = metadata.Parameters.FirstOrDefault(candidate => string.Equals(candidate.JsonName, name, StringComparison.Ordinal));

            if (value is JsonValue text && text.TryGetValue(out string? word) && word.StartsWith('$'))
            {
                switch (word)
                {
                    case "$playhead":
                        args[name] = JsonSerializer.SerializeToNode(context.Playhead, JazzJson.Options);
                        continue;

                    case "$in" or "$out":
                        if (context.Project.ActiveSequence?.InOut is not { } marked)
                        {
                            nothing = "Mark an in point and an out point first, with I and O.";
                            return null;
                        }

                        args[name] = JsonSerializer.SerializeToNode(word == "$in" ? marked.Start : marked.End, JazzJson.Options);
                        continue;

                    case "$selection" or "$clipsAtPlayhead" or "$allClips":
                        ImmutableArray<string> ids = Ids(word, context);
                        if (ids.IsEmpty)
                        {
                            nothing = word == "$clipsAtPlayhead" ? "There is no clip under the playhead." : "Nothing is selected.";
                            return null;
                        }

                        if (parameter is not null && parameter.Type == typeof(string))
                        {
                            fanOut = ids;
                            fanOutName = name;
                        }
                        else
                        {
                            args[name] = new JsonArray([.. ids.Select(id => JsonValue.Create(id))]);
                        }

                        continue;

                    default:
                        throw new FormatException($"'{word}' in the binding for {binding.Keys} is not something a key can fill in.");
                }
            }

            args[name] = value?.DeepClone();
        }

        if (fanOut is not { } many)
        {
            return (ICommand)CommandRegistry.FromJson(binding.Command, args);
        }

        ICommand[] commands =
        [
            .. many.Select(id =>
            {
                var one = (JsonObject)args.DeepClone();
                one[fanOutName!] = id;
                return (ICommand)CommandRegistry.FromJson(binding.Command, one);
            }),
        ];

        return commands.Length == 1 ? commands[0] : new BatchCommand(commands, metadata.Description);
    }

    private static ImmutableArray<string> Ids(string word, KeymapContext context)
    {
        Sequence? sequence = context.Project.ActiveSequence;
        if (sequence is null)
        {
            return [];
        }

        // Only clips: a selected marker is not something Delete or Ctrl+D knows what to do with.
        var clips = sequence.Tracks.SelectMany(track => track.Clips.Select(clip => (Track: track, Clip: clip))).ToList();
        var selected = new HashSet<string>(context.Selection, StringComparer.Ordinal);

        switch (word)
        {
            case "$allClips":
                return [.. clips.Select(entry => entry.Clip.Id)];

            case "$selection":
                return [.. context.Selection.Where(id => clips.Any(entry => string.Equals(entry.Clip.Id, id, StringComparison.Ordinal)))];

            default:
                var under = clips
                    .Where(entry => !entry.Track.Locked && entry.Clip.Start < context.Playhead && context.Playhead < entry.Clip.End)
                    .ToList();
                var chosen = under.Where(entry => selected.Contains(entry.Clip.Id)).ToList();
                return [.. (chosen.Count > 0 ? chosen : under).Select(entry => entry.Clip.Id)];
        }
    }

    private static Dictionary<KeyChord, KeymapBinding> Read(string json, string source, List<string>? problems = null)
    {
        var bindings = new Dictionary<KeyChord, KeymapBinding>();
        JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (root?["bindings"] is not JsonArray entries)
        {
            throw new JsonException($"{source} has no 'bindings' list.");
        }

        foreach (JsonNode? entry in entries)
        {
            string keys = entry?["keys"]?.GetValue<string>() ?? string.Empty;
            string command = entry?["command"]?.GetValue<string>() ?? string.Empty;

            try
            {
                KeyChord gesture = KeyChord.Parse(keys);

                if (UiActions.IsAction(command) ? !UiActions.Known.Contains(command) : command.Length > 0 && CommandRegistry.Find(command) is null)
                {
                    throw new FormatException($"there is no command called '{command}'");
                }

                bindings[gesture] = new KeymapBinding(
                    keys,
                    gesture,
                    command,
                    entry?["args"] as JsonObject is { } args ? (JsonObject)args.DeepClone() : [],
                    entry?["repeat"]?.GetValue<bool>() ?? false);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
            {
                // A bad line in a person's own file skips that binding; in the embedded defaults it
                // is a bug, and the test that loads them says so.
                string problem = $"The binding '{keys}' in {source} was skipped: {exception.Message}.";
                if (problems is null)
                {
                    throw new InvalidOperationException(problem, exception);
                }

                problems.Add(problem);
            }
        }

        return bindings;
    }
}
