using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Input;
using JazzHands.App.Services;
using JazzHands.Core.Commands;

namespace JazzHands.App.ViewModels.Keys;

/// <summary>One binding in the keymap editor: a key, what it sends, and whether it clashes.</summary>
public sealed partial class KeymapRowViewModel : ObservableObject
{
    private readonly KeymapEditorViewModel _owner;

    [ObservableProperty]
    private string _keys;

    [ObservableProperty]
    private string _conflict = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    internal KeymapRowViewModel(KeymapEditorViewModel owner, KeymapBinding binding, KeymapBinding? original)
    {
        _owner = owner;
        _keys = binding.Keys;
        Command = binding.Command;
        Args = (JsonObject)binding.Args.DeepClone();
        Repeat = binding.Repeat;
        Original = original;
    }

    /// <summary>The command it sends.</summary>
    public string Command { get; }

    /// <summary>The command's arguments, <c>$</c> values filled in at the press.</summary>
    public JsonObject Args { get; }

    /// <summary>The arguments as compact JSON, empty when there are none.</summary>
    public string ArgsText => Args.Count > 0 ? Args.ToJsonString() : string.Empty;

    /// <summary>Whether holding the key repeats it.</summary>
    public bool Repeat { get; }

    /// <summary>What the command does.</summary>
    public string Description => Input.Keymap.Describe(Command);

    /// <summary>The default binding this row started as, or null for one a person added.</summary>
    public KeymapBinding? Original { get; }

    /// <summary>True when the row is not as the defaults have it.</summary>
    public bool IsChanged => Original is null || !string.Equals(Normal(Keys), Normal(Original.Keys), StringComparison.OrdinalIgnoreCase);

    /// <summary>The row as a binding, or null while its keys do not read.</summary>
    public KeymapBinding? Binding()
    {
        try
        {
            return Input.Keymap.Bind(Keys, Command, Args, Repeat);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    partial void OnKeysChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
        _owner.Recheck();
    }

    private static string Normal(string keys) => string.Join('+', keys.Split('+', StringSplitOptions.TrimEntries));
}

/// <summary>
/// Settings, Keymap: every binding, searchable, with the key editable, clashes shown, and a way
/// back to the defaults. Saving writes only what differs from the defaults and puts the new keys
/// in use at once.
/// </summary>
public sealed partial class KeymapEditorViewModel : ObservableObject
{
    private readonly KeymapService _service;
    private readonly IFileDialogService? _files;
    private readonly string _userPath;
    private bool _rechecking;

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasProblems;

    [ObservableProperty]
    private string _newKeys = string.Empty;

    [ObservableProperty]
    private string _newCommand = string.Empty;

    [ObservableProperty]
    private string _newArgs = string.Empty;

    /// <summary>An editor over the keys in use.</summary>
    /// <param name="service">The keys in use, which a save replaces.</param>
    /// <param name="files">Where import and export ask for files.</param>
    /// <param name="userPath">The person's keymap file, or null for the usual one.</param>
    public KeymapEditorViewModel(KeymapService service, IFileDialogService? files = null, string? userPath = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _files = files;
        _userPath = userPath ?? Input.Keymap.UserPath;
        Commands = [.. UiActions.Known.Concat(CommandRegistry.Commands.Select(command => command.Name)).Order(StringComparer.Ordinal)];
        Load(service.Keymap.Bindings);
    }

    /// <summary>Every binding, in key order.</summary>
    public ObservableCollection<KeymapRowViewModel> Rows { get; } = [];

    /// <summary>The rows the search lets through.</summary>
    public ObservableCollection<KeymapRowViewModel> Visible { get; } = [];

    /// <summary>Every command a key can send, for adding a binding.</summary>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>Writes what differs from the defaults and puts the keys in use. False when something clashes.</summary>
    public bool Save()
    {
        Recheck();
        if (HasProblems)
        {
            Status = "Not saved: two bindings share a key, or a key does not read. Fix the rows marked in red.";
            return false;
        }

        KeymapBinding[] bindings = [.. Rows.Select(row => row.Binding()!)];
        if (bindings.Length == _service.Keymap.Bindings.Count() && bindings.All(binding => _service.Keymap.Find(binding.Gesture.Key, binding.Gesture.Modifiers) is { } current && Input.Keymap.Same(current, binding)))
        {
            return true;
        }

        Input.Keymap.SaveUser(_userPath, bindings);
        _service.Replace(Input.Keymap.From(bindings));
        Status = string.Create(CultureInfo.InvariantCulture, $"Saved {bindings.Length} bindings; they are in use now.");
        return true;
    }

    /// <summary>Checks every row: keys that do not read, keys bound twice.</summary>
    internal void Recheck()
    {
        if (_rechecking)
        {
            return;
        }

        _rechecking = true;
        try
        {
            var byGesture = new Dictionary<KeyChord, List<KeymapRowViewModel>>();
            foreach (KeymapRowViewModel row in Rows)
            {
                row.Conflict = string.Empty;
                try
                {
                    KeyChord gesture = KeyChord.Parse(row.Keys);
                    row.Error = string.Empty;
                    if (!byGesture.TryGetValue(gesture, out List<KeymapRowViewModel>? sharing))
                    {
                        byGesture[gesture] = sharing = [];
                    }

                    sharing.Add(row);
                }
                catch (Exception error) when (error is FormatException or ArgumentException)
                {
                    row.Error = error.Message;
                }
            }

            foreach (List<KeymapRowViewModel> sharing in byGesture.Values.Where(rows => rows.Count > 1))
            {
                foreach (KeymapRowViewModel row in sharing)
                {
                    row.Conflict = $"{row.Keys} is also {string.Join(" and ", sharing.Where(other => other != row).Select(other => other.Description))}.";
                }
            }

            HasProblems = Rows.Any(row => row.Error.Length > 0 || row.Conflict.Length > 0);
            Status = HasProblems ? "Some keys clash or do not read." : string.Empty;
        }
        finally
        {
            _rechecking = false;
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>Puts every key back as Jazz Hands ships it.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        Load(Input.Keymap.Defaults().Bindings);
        Status = "Back to the defaults. Save to use them.";
    }

    /// <summary>Puts one row's key back.</summary>
    [RelayCommand]
    private void ResetRow(KeymapRowViewModel? row)
    {
        if (row?.Original is { } original)
        {
            row.Keys = original.Keys;
        }
    }

    /// <summary>Frees a key: the binding goes.</summary>
    [RelayCommand]
    private void Remove(KeymapRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Rows.Remove(row);
        Visible.Remove(row);
        Recheck();
    }

    /// <summary>Adds the binding typed in the new row.</summary>
    [RelayCommand]
    private void Add()
    {
        try
        {
            JsonObject? args = NewArgs.Trim().Length > 0
                ? JsonNode.Parse(NewArgs) as JsonObject ?? throw new FormatException("The arguments are an object: {\"name\": value}.")
                : null;
            KeymapBinding binding = Input.Keymap.Bind(NewKeys, NewCommand.Trim(), args);
            var row = new KeymapRowViewModel(this, binding, null);
            Rows.Add(row);
            NewKeys = string.Empty;
            NewCommand = string.Empty;
            NewArgs = string.Empty;
            Filter();
            Recheck();
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            Status = error.Message;
        }
    }

    /// <summary>Writes every binding to a file, for another machine.</summary>
    [RelayCommand]
    private void Export()
    {
        if (_files?.SaveKeymap(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "jazz-keymap.json")) is not { } path)
        {
            return;
        }

        Input.Keymap.Export(path, Rows.Select(row => row.Binding()).OfType<KeymapBinding>());
        Status = $"Exported to {path}.";
    }

    /// <summary>Reads a keymap file over the defaults, to save if it looks right.</summary>
    [RelayCommand]
    private void Import()
    {
        if (_files?.OpenKeymap() is not { } path)
        {
            return;
        }

        Input.Keymap imported = Input.Keymap.Load(path, out IReadOnlyList<string> problems);
        Load(imported.Bindings);
        Status = problems.Count > 0
            ? $"Imported, with {Services.Words.Count(problems.Count, "line")} skipped: {problems[0]}"
            : "Imported. Save to use it.";
    }

    private void Load(IEnumerable<KeymapBinding> bindings)
    {
        Dictionary<KeyChord, KeymapBinding> defaults = Input.Keymap.Defaults().Bindings.ToDictionary(binding => binding.Gesture);
        Rows.Clear();
        foreach (KeymapBinding binding in bindings.OrderBy(binding => binding.Command, StringComparer.Ordinal).ThenBy(binding => binding.Keys, StringComparer.Ordinal))
        {
            // A row remembers the default it came from: the same key, or failing that the same command and arguments.
            KeymapBinding? original = defaults.GetValueOrDefault(binding.Gesture) is { } sameKey && sameKey.Command == binding.Command && JsonNode.DeepEquals(sameKey.Args, binding.Args)
                ? sameKey
                : defaults.Values.FirstOrDefault(candidate => candidate.Command == binding.Command && JsonNode.DeepEquals(candidate.Args, binding.Args));
            Rows.Add(new KeymapRowViewModel(this, binding, original));
        }

        Filter();
        Recheck();
    }

    private void Filter()
    {
        Visible.Clear();
        string wanted = Search.Trim();
        foreach (KeymapRowViewModel row in Rows)
        {
            if (wanted.Length == 0
                || row.Keys.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || row.Command.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                Visible.Add(row);
            }
        }
    }
}
