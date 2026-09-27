using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Control;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.App.ViewModels.Remote;

/// <summary>What a console line records.</summary>
public enum ConsoleEntryKind
{
    /// <summary>A command, from anyone.</summary>
    Command,

    /// <summary>A question or a built-in a client asked, or the console did.</summary>
    Request,

    /// <summary>A line the console could not read.</summary>
    Error,
}

/// <summary>One line of the Command Console's log.</summary>
/// <param name="At">When it finished.</param>
/// <param name="Origin">Who did it: <c>gui</c>, <c>console</c>, <c>rpc:cli</c>, <c>serve</c>.</param>
/// <param name="Kind">What it was.</param>
/// <param name="Title">The line as a person would type it.</param>
/// <param name="Method">The method, or empty for a line that was not understood.</param>
/// <param name="Args">Its params, for replay and scripts.</param>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Result">What came back, or why it failed.</param>
public sealed record ConsoleEntry(
    DateTimeOffset At,
    string Origin,
    ConsoleEntryKind Kind,
    string Title,
    string Method,
    JsonObject? Args,
    bool Ok,
    string Result)
{
    /// <summary>The time, as the log shows it.</summary>
    public string Time => At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>The params as compact JSON, for the tooltip and the detail.</summary>
    public string ArgsJson => Args?.ToJsonString() ?? "{}";
}

/// <summary>
/// One event from the server as the feed shows it: a line that fits the pane, and the whole JSON
/// when it is opened.
/// </summary>
/// <param name="Time">When it arrived, as the feed shows it.</param>
/// <param name="Name">The event, <c>project.changed</c> say.</param>
/// <param name="Payload">What it carried; read on the UI thread only.</param>
public sealed record ConsoleEvent(string Time, string Name, JsonObject Payload)
{
    private const int LongestValue = 40;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private string? _json;

    /// <summary>The payload's top level in a line: plain values as they are, lists as a count.</summary>
    public string Summary { get; } = Summarize(Payload);

    /// <summary>The payload, indented, for the opened line.</summary>
    public string Json => _json ??= Payload.ToJsonString(Indented);

    /// <summary>The payload's top level in a line, <c>origin rpc:claude, changed [3], sequence {...}</c>.</summary>
    public static string Summarize(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return string.Join(", ", payload.Select(pair => pair.Value switch
        {
            JsonArray list => $"{pair.Key} [{list.Count}]",
            JsonObject => $"{pair.Key} {{...}}",
            JsonValue value when value.TryGetValue(out string? text) => $"{pair.Key} {(text.Length > LongestValue ? text[..(LongestValue - 3)] + "..." : text)}",
            JsonValue value => $"{pair.Key} {value.ToJsonString()}",
            _ => $"{pair.Key} null",
        }));
    }
}

/// <summary>
/// The Command Console: type any command or RPC method, see every command from every origin as it
/// happens with its JSON and result, watch the event feed, replay a line, save lines as a script.
/// </summary>
/// <remarks>
/// <para>
/// What is typed goes through <see cref="ControlServer.CallLocalAsync"/>, the path a pipe client's
/// request takes, so the console answers exactly what <c>jazz rpc call</c> would, and its commands
/// are recorded in history as <c>console</c>. The log hears the session's
/// <see cref="Session.CommandCompleted"/> for commands from the GUI and remote clients, and the
/// server's <see cref="ControlServer.RequestHandled"/> for everything else remote clients ask.
/// </para>
/// <para>
/// Lines arrive on engine and connection threads, perhaps a thousand in a second from a script;
/// they queue and reach the UI thread in one post per burst, and the log keeps the latest
/// <see cref="MaxEntries"/>.
/// </para>
/// </remarks>
public sealed partial class CommandConsoleViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "console";

    /// <summary>The filter that shows every origin.</summary>
    public const string AllOrigins = "all";

    /// <summary>The most log lines kept.</summary>
    public const int MaxEntries = 2000;

    /// <summary>The most event lines kept.</summary>
    public const int MaxEvents = 500;

    /// <summary>The most characters of a result shown.</summary>
    private const int MaxResultChars = 20_000;

    private static readonly JsonSerializerOptions Pretty = new(JazzJson.Options) { WriteIndented = true };

    private readonly ControlServer _server;
    private readonly Session _session;
    private readonly IUiDispatcher _ui;
    private readonly IFileDialogService? _files;
    private readonly ConcurrentQueue<ConsoleEntry> _incoming = new();
    private readonly ConcurrentQueue<ConsoleEvent> _incomingEvents = new();
    private readonly List<string> _history = [];
    private int _historyIndex;
    private int _flushQueued;
    private bool _completing;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _selectedOrigin = AllOrigins;

    /// <summary>Shows the lines every client sends to connect and poll; off, so the commands stand out.</summary>
    [ObservableProperty]
    private bool _showHandshakes;

    [ObservableProperty]
    private ConsoleEntry? _selectedEntry;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _networkWarning = string.Empty;

    /// <summary>Creates the panel.</summary>
    /// <param name="server">The control server; the console is one of its clients.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="files">Where a script is saved, or null in a host without dialogs.</param>
    public CommandConsoleViewModel(ControlServer server, IUiDispatcher ui, IFileDialogService? files = null)
        : base(PanelId, "Command Console")
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(ui);

        _server = server;
        _session = server.Target.Session;
        _ui = ui;
        _files = files;

        _session.CommandCompleted += OnCommandCompleted;
        _server.RequestHandled += OnRequestHandled;
        _server.EventRaised += OnEvent;

        Origins.Add(AllOrigins);
        UpdateStatus();
    }

    /// <summary>Every line, oldest first.</summary>
    public ObservableCollection<ConsoleEntry> Entries { get; } = [];

    /// <summary>The lines the origin filter lets through.</summary>
    public ObservableCollection<ConsoleEntry> Visible { get; } = [];

    /// <summary>The origins seen so far, and <see cref="AllOrigins"/>.</summary>
    public ObservableCollection<string> Origins { get; } = [];

    /// <summary>The events the server sent, newest last.</summary>
    public ObservableCollection<ConsoleEvent> Events { get; } = [];

    /// <summary>What Tab would complete the input to, best first.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    /// <summary>Runs what was typed.</summary>
    [RelayCommand]
    public async Task SubmitAsync()
    {
        string line = Input.Trim();
        if (line.Length == 0)
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != line)
        {
            _history.Add(line);
        }

        _historyIndex = _history.Count;
        Input = string.Empty;

        string method;
        JsonObject? parameters;
        try
        {
            (method, parameters) = ConsoleInput.Parse(line, FrameRate());
        }
        catch (CommandException error)
        {
            Add(new ConsoleEntry(DateTimeOffset.Now, "console", ConsoleEntryKind.Error, line, string.Empty, null, false, error.Message));
            return;
        }

        await RunAsync(line, method, parameters).ConfigureAwait(true);
    }

    /// <summary>Runs a line again, as the console, with the params it had.</summary>
    [RelayCommand]
    public Task ReplayAsync(ConsoleEntry? entry)
    {
        entry ??= SelectedEntry;
        return entry is null || entry.Method.Length == 0
            ? Task.CompletedTask
            : RunAsync(entry.Title, entry.Method, entry.Args);
    }

    /// <summary>The previous line typed.</summary>
    [RelayCommand]
    public void HistoryBack()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        Recall(_history[_historyIndex]);
    }

    /// <summary>The next line typed, or an empty line after the last.</summary>
    [RelayCommand]
    public void HistoryForward()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Recall(_historyIndex < _history.Count ? _history[_historyIndex] : string.Empty);
    }

    /// <summary>Takes the best suggestion: a method name, or the next option.</summary>
    [RelayCommand]
    public void Complete()
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        string choice = Suggestions[0];
        string text = Input;
        _completing = true;
        try
        {
            if (choice.StartsWith("--", StringComparison.Ordinal))
            {
                int last = text.LastIndexOf(' ');
                Input = (last < 0 ? string.Empty : text[..(last + 1)]) + choice + " ";
            }
            else
            {
                Input = (text.TrimStart().StartsWith("jazz ", StringComparison.Ordinal) ? "jazz " : string.Empty) + choice + " ";
            }
        }
        finally
        {
            _completing = false;
        }

        UpdateSuggestions();
    }

    /// <summary>Empties the log and the event feed.</summary>
    [RelayCommand]
    public void Clear()
    {
        Entries.Clear();
        Visible.Clear();
        Events.Clear();
        UpdateStatus();
    }

    /// <summary>
    /// Writes the commands in view that worked as a <c>jazz apply</c> script, so what was done
    /// here, by hand or remotely, can be done again: <c>jazz apply other.jazz script.json</c>.
    /// </summary>
    [RelayCommand]
    public void SaveScript()
    {
        JsonArray script = Script();
        if (script.Count == 0)
        {
            Status = "No commands in view worked, so there is nothing to save.";
            return;
        }

        string folder = _session.ProjectPath.Length > 0
            ? Path.GetDirectoryName(_session.ProjectPath) ?? string.Empty
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (_files?.SaveScript(Path.Combine(folder, "commands.json")) is not { } path)
        {
            return;
        }

        File.WriteAllText(path, script.ToJsonString(Pretty));
        Status = string.Create(CultureInfo.InvariantCulture, $"Saved {Services.Words.Count(script.Count, "command")} to {path}.");
    }

    /// <summary>The commands in view that worked, as <c>jazz apply</c> steps.</summary>
    public JsonArray Script() =>
        [.. Visible
            .Where(entry => entry is { Kind: ConsoleEntryKind.Command, Ok: true })
            .Select(entry => (JsonNode?)new JsonObject { ["command"] = entry.Method, ["args"] = entry.Args?.DeepClone() ?? new JsonObject() })];

    /// <summary>
    /// Says what the server listens on, once it has started: the status line, and the warning the
    /// window keeps showing while other machines can reach it.
    /// </summary>
    public void ServerStarted()
    {
        NetworkWarning = _server.IsReachableFromNetwork
            ? $"Remote control is open to the network on {_server.TcpEndPoint}. Anyone with the token can edit this project; turn allowRemote off in settings.json when you are done."
            : string.Empty;
        UpdateStatus();
    }

    /// <summary>Stops hearing the session and the server.</summary>
    public void Detach()
    {
        _session.CommandCompleted -= OnCommandCompleted;
        _server.RequestHandled -= OnRequestHandled;
        _server.EventRaised -= OnEvent;
    }

    partial void OnInputChanged(string value)
    {
        if (!_completing)
        {
            UpdateSuggestions();
        }
    }

    partial void OnSelectedOriginChanged(string value)
    {
        Visible.Clear();
        foreach (ConsoleEntry entry in Entries.Where(Passes))
        {
            Visible.Add(entry);
        }

        UpdateStatus();
    }

    private async Task RunAsync(string title, string method, JsonObject? parameters)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        JsonObject response;
        try
        {
            response = await _server.CallLocalAsync(method, parameters).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            Status = "The editor is closing.";
            return;
        }

        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        bool ok = response["error"] is null;
        bool command = CommandRegistry.Find(method) is { IsQuery: false } || method is "session.open" or "session.save";
        string result = ok
            ? Describe(response["result"], elapsed)
            : response["error"]?["message"]?.GetValue<string>() ?? "It failed.";

        Add(new ConsoleEntry(DateTimeOffset.Now, "console", command ? ConsoleEntryKind.Command : ConsoleEntryKind.Request, title, method, parameters, ok, result));
    }

    private void OnCommandCompleted(object? sender, CommandCompletedEventArgs args)
    {
        // The console writes its own lines when the answer comes back, with the answer.
        if (args.Issuer == "console")
        {
            return;
        }

        string result = args.Result.Ok
            ? string.Create(CultureInfo.InvariantCulture, $"ok, version {args.Result.Version}, {args.Result.ChangedIds.Length} changed, {args.Elapsed.TotalMilliseconds:0.0} ms")
            : $"{args.Result.Code}: {args.Result.Error}";
        Queue(new ConsoleEntry(args.At, args.Issuer.Length > 0 ? args.Issuer : "local", ConsoleEntryKind.Command, CommandLine(args.Name, args.Args), args.Name, args.Args, args.Result.Ok, result));
    }

    private void OnRequestHandled(object? sender, RemoteRequest request)
    {
        if (request.Client == "console")
        {
            return;
        }

        string args = request.Params?.ToJsonString() ?? string.Empty;
        Queue(new ConsoleEntry(
            DateTimeOffset.Now,
            request.Client,
            ConsoleEntryKind.Request,
            args.Length > 0 && args != "{}" ? $"{request.Method} {args}" : request.Method,
            request.Method,
            request.Params as JsonObject,
            request.Ok,
            request.Ok ? string.Create(CultureInfo.InvariantCulture, $"ok, {request.Elapsed.TotalMilliseconds:0.0} ms") : request.Error ?? "It failed."));
    }

    private void OnEvent(object? sender, ControlEvent raised)
    {
        _incomingEvents.Enqueue(new ConsoleEvent(DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), raised.Name, raised.Payload));
        ScheduleFlush();
    }

    private void Queue(ConsoleEntry entry)
    {
        _incoming.Enqueue(entry);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
        {
            _ui.Post(Flush);
        }
    }

    private void Flush()
    {
        Volatile.Write(ref _flushQueued, 0);

        while (_incoming.TryDequeue(out ConsoleEntry? entry))
        {
            Add(entry);
        }

        while (_incomingEvents.TryDequeue(out ConsoleEvent? line))
        {
            Events.Add(line);
        }

        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(0);
        }
    }

    private void Add(ConsoleEntry entry)
    {
        Entries.Add(entry);
        if (!Origins.Contains(entry.Origin))
        {
            Origins.Add(entry.Origin);
        }

        if (Passes(entry))
        {
            Visible.Add(entry);
        }

        while (Entries.Count > MaxEntries)
        {
            ConsoleEntry oldest = Entries[0];
            Entries.RemoveAt(0);
            if (Visible.Count > 0 && ReferenceEquals(Visible[0], oldest))
            {
                Visible.RemoveAt(0);
            }
        }

        UpdateStatus();
    }

    /// <summary>The methods a client sends to connect and to poll, not to do anything.</summary>
    public static IReadOnlySet<string> Handshakes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "session.hello", "session.info", "session.subscribe", "session.unsubscribe", "session.project", "playback.state",
    };

    /// <summary>
    /// Whether a line shows: from the origin picked (or any), and not another client's handshake
    /// unless those are asked for. Lines typed here always show.
    /// </summary>
    public static bool Shows(ConsoleEntry entry, string origin, bool handshakes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (origin == AllOrigins || entry.Origin == origin)
            && (handshakes || entry.Origin == "console" || !Handshakes.Contains(entry.Method));
    }

    private bool Passes(ConsoleEntry entry) => Shows(entry, SelectedOrigin, ShowHandshakes);

    partial void OnShowHandshakesChanged(bool value) => OnSelectedOriginChanged(SelectedOrigin);

    private void Recall(string line)
    {
        _completing = true;
        try
        {
            Input = line;
        }
        finally
        {
            _completing = false;
        }

        Suggestions.Clear();
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        string text = Input.TrimStart();
        if (text.StartsWith("jazz ", StringComparison.Ordinal))
        {
            text = text[5..];
        }

        if (text.Length == 0)
        {
            return;
        }

        IEnumerable<string> found;
        if (ConsoleInput.MethodOf(text) is { } method && (text.EndsWith(' ') || text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].StartsWith('-')))
        {
            string last = text.EndsWith(' ') ? string.Empty : text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
            found = ConsoleInput.OptionsLeft(method, text).Where(option => option.StartsWith(last, StringComparison.Ordinal));
        }
        else
        {
            found = (text.Contains(' ', StringComparison.Ordinal) ? ConsoleInput.Verbs : ConsoleInput.Methods.Concat(ConsoleInput.Verbs))
                .Where(candidate => candidate.StartsWith(text, StringComparison.OrdinalIgnoreCase) && candidate.Length > text.Length);
        }

        foreach (string suggestion in found.Distinct(StringComparer.Ordinal).Take(12))
        {
            Suggestions.Add(suggestion);
        }
    }

    private void UpdateStatus()
    {
        string listening = _server.PipeName is { } pipe ? $"listening on \\\\.\\pipe\\{pipe}" : "not listening";
        if (_server.TcpEndPoint is { } tcp)
        {
            listening += $" and {tcp}";
        }

        Status = string.Create(CultureInfo.InvariantCulture, $"{Services.Words.Count(Visible.Count, "line")}; {listening}");
    }

    private Rational FrameRate()
    {
        Project project = _session.Project;
        return project.ActiveSequence is { } active ? project.SettingsFor(active).FrameRate : project.Settings.FrameRate;
    }

    private string CommandLine(string name, JsonObject args)
    {
        try
        {
            string line = CommandRegistry.ToCommandLine(CommandRegistry.FromJson(name, args, FrameRate()), FrameRate());
            return line.StartsWith("jazz ", StringComparison.Ordinal) ? line[5..] : line;
        }
        catch (Exception error) when (error is CommandException or JsonException or ArgumentException or InvalidOperationException)
        {
            return $"{name} {args.ToJsonString()}";
        }
    }

    private static string Describe(JsonNode? result, TimeSpan elapsed)
    {
        string text = result is JsonObject { } answer && answer["data"] is { } data
            ? data.ToJsonString(Pretty)
            : result?.ToJsonString(Pretty) ?? "ok";
        if (text.Length > MaxResultChars)
        {
            text = string.Concat(text.AsSpan(0, MaxResultChars), $"{Environment.NewLine}... {text.Length - MaxResultChars} more characters");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:0.0} ms{Environment.NewLine}{text}");
    }
}
