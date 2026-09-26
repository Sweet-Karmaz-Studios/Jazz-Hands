using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using JazzHands.Render.Shaders;
using Serilog;

namespace JazzHands.Render.Effects.Transitions;

/// <summary>A transition file that could not be loaded, and why.</summary>
/// <param name="File">The shader or manifest.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record CustomTransitionProblem(string File, string Message);

/// <summary>
/// Transitions written outside the build: HLSL files in a folder, each with an optional JSON
/// manifest beside it, loaded into <see cref="VideoEffects.Registry"/> like the built-in ones.
/// </summary>
/// <remarks>
/// <para>
/// <c>heart.hlsl</c> defines <c>float4 PsMain(FullScreenVertex input) : SV_TARGET</c> after
/// <c>#include "Transition.hlsli"</c>, which gives it the outgoing picture at t0, the incoming
/// one at t1, <c>Progress</c> and the rest. <c>heart.json</c>, if there is one, names it and
/// declares its parameters:
/// </para>
/// <code>
/// { "id": "transition.user.heart", "name": "Heart", "category": "Custom", "description": "...",
///   "entry": "PsMain",
///   "params": [ { "name": "softness", "type": "float", "default": "20", "min": 0, "max": 200, "unit": "px",
///                 "description": "How soft the edge is." } ] }
/// </code>
/// <para>
/// Everything in the manifest is optional; without one the transition is called after its file
/// and has only the easing parameters every transition has. Parameters reach the shader in order
/// through <c>Values</c> then <c>More</c>, one float each: a number (a <c>px</c> one multiplied by
/// the quality, so it is in texels), a whole number, a switch as 0 or 1, a choice as its index; a
/// pair or a point takes two, a point as texels from the top left. A colour goes to <c>Tint</c>,
/// premultiplied; there can be one. Eight floats at most.
/// </para>
/// <para>
/// A shader is compiled on first use and again whenever its file changes, so editing one while the
/// editor runs shows the change on the next frame. A shader that does not compile cuts in the
/// middle and says why in the log. With <c>watch</c>, a new file or an edited manifest reloads
/// the registry too; the hosts watch in debug builds.
/// </para>
/// </remarks>
public static class CustomTransitions
{
    /// <summary>The prefix of the id a transition gets from its file name.</summary>
    public const string UserPrefix = "transition.user.";

    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(CustomTransitions));
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lock Gate = new();
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;
    private static ImmutableDictionary<string, Loaded> _loaded = ImmutableDictionary<string, Loaded>.Empty;

    /// <summary><c>%APPDATA%\JazzHands\transitions</c>.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        JazzHands.Core.JazzFolders.Roaming, "transitions");

    /// <summary>True in a debug build, where the hosts watch the folder for new and edited files.</summary>
    public static bool WatchByDefault =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>What went wrong at the last load.</summary>
    public static ImmutableArray<CustomTransitionProblem> Problems { get; private set; } = [];

    /// <summary>The folder loaded from, or null before any load.</summary>
    public static string? Folder { get; private set; }

    /// <summary>
    /// Loads every transition in a folder into the registry, replacing what was loaded before,
    /// and optionally watches it. A missing folder loads none.
    /// </summary>
    /// <returns>What could not be loaded.</returns>
    public static ImmutableArray<CustomTransitionProblem> Load(string folder, bool watch = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        lock (Gate)
        {
            Folder = folder;
            var problems = ImmutableArray.CreateBuilder<CustomTransitionProblem>();
            var loaded = ImmutableDictionary.CreateBuilder<string, Loaded>(StringComparer.Ordinal);

            if (Directory.Exists(folder))
            {
                foreach (string shader in Directory.EnumerateFiles(folder, "*.hlsl").Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        Loaded one = Read(shader);
                        if (VideoEffects.BuiltIn.Find(one.Descriptor.TypeId) is not null || loaded.ContainsKey(one.Descriptor.TypeId))
                        {
                            problems.Add(new CustomTransitionProblem(shader, $"The id '{one.Descriptor.TypeId}' is already taken."));
                            continue;
                        }

                        loaded[one.Descriptor.TypeId] = one;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                    {
                        problems.Add(new CustomTransitionProblem(shader, exception.Message));
                    }
                }
            }

            foreach (CustomTransitionProblem problem in problems)
            {
                Log.Warning("Transition {File} was not loaded: {Message}", problem.File, problem.Message);
            }

            _loaded = loaded.ToImmutable();
            Problems = problems.ToImmutable();
            VideoEffects.Include(_loaded.Values.Select(item => item.Descriptor));
            ShaderLibrary.Invalidate();
            Log.Information("Loaded {Count} transitions from {Folder}", _loaded.Count, folder);

            if (watch)
            {
                Watch(folder);
            }

            return Problems;
        }
    }

    /// <summary>Stops watching and forgets what was loaded, leaving the built-in types.</summary>
    public static void Unload()
    {
        lock (Gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
            _loaded = ImmutableDictionary<string, Loaded>.Empty;
            Problems = [];
            Folder = null;
            VideoEffects.Include([]);
        }
    }

    /// <summary>The entry point a loaded transition's shader starts at.</summary>
    internal static string EntryOf(EffectDescriptor descriptor) =>
        _loaded.TryGetValue(descriptor.TypeId, out Loaded? loaded) ? loaded.Entry : "PsMain";

    /// <summary>A transition's parameters packed into the shader's constants, as the remarks say.</summary>
    internal static TransitionValues Pack(EffectContext context, ParameterSet parameters)
    {
        Span<float> slots = stackalloc float[8];
        int used = 0;
        var values = new TransitionValues();

        foreach (ParamDescriptor parameter in parameters.Descriptor.Params)
        {
            if (parameter.Name is TransitionEasing.EasingParam or TransitionEasing.CurveParam)
            {
                continue;
            }

            switch (parameter.Type)
            {
                case ParamType.Color:
                    Vector4 colour = parameters.Color(parameter.Name);
                    values.Tint = new Vector4(colour.X * colour.W, colour.Y * colour.W, colour.Z * colour.W, colour.W);
                    continue;

                case ParamType.Float2:
                    Add(slots, ref used, parameters.Float2(parameter.Name).X);
                    Add(slots, ref used, parameters.Float2(parameter.Name).Y);
                    continue;

                case ParamType.Point:
                    Vector2 point = IrisTransition.CentreTexels(context, parameters.Float2(parameter.Name));
                    Add(slots, ref used, point.X);
                    Add(slots, ref used, point.Y);
                    continue;

                case ParamType.Int:
                    Add(slots, ref used, parameters.Int(parameter.Name));
                    continue;

                case ParamType.Bool:
                    Add(slots, ref used, parameters.Bool(parameter.Name) ? 1.0f : 0.0f);
                    continue;

                case ParamType.Enum:
                    Add(slots, ref used, Math.Max(0, parameter.Choices.IndexOf(choice => choice == parameters.Enum(parameter.Name))));
                    continue;

                default:
                    float number = parameters.Float(parameter.Name);
                    Add(slots, ref used, parameter.Unit == "px" ? number * context.QualityScale : number);
                    continue;
            }
        }

        values.Values = new Vector4(slots[0], slots[1], slots[2], slots[3]);
        values.More = new Vector4(slots[4], slots[5], slots[6], slots[7]);
        return values;
    }

    private static void Add(Span<float> slots, ref int used, float value)
    {
        if (used < slots.Length)
        {
            slots[used++] = value;
        }
    }

    /// <summary>Reads one shader and its manifest into a descriptor.</summary>
    private static Loaded Read(string shader)
    {
        string stem = Path.GetFileNameWithoutExtension(shader);
        string manifestPath = Path.ChangeExtension(shader, ".json");
        Manifest manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Json) ?? new Manifest()
            : new Manifest();

        string id = manifest.Id is { Length: > 0 } given
            ? given.StartsWith("transition.", StringComparison.Ordinal) ? given : UserPrefix + given
            : UserPrefix + Slug(stem);

        var parameters = ImmutableArray.CreateBuilder<ParamDescriptor>();
        int floats = 0;
        int colours = 0;
        foreach (ManifestParam param in manifest.Params ?? [])
        {
            ParamType type = TypeOf(param.Type, param.Name);
            floats += type switch { ParamType.Color => 0, ParamType.Float2 or ParamType.Point => 2, _ => 1 };
            colours += type == ParamType.Color ? 1 : 0;

            var attribute = new ParamAttribute(param.Name ?? throw new InvalidOperationException("A parameter has no name."), type)
            {
                Default = param.Default ?? string.Empty,
                Min = param.Min ?? double.NaN,
                Max = param.Max ?? double.NaN,
                SliderMax = param.SliderMax ?? double.NaN,
                Unit = param.Unit ?? (type == ParamType.Point ? "px" : string.Empty),
                Label = param.Label ?? string.Empty,
                Description = param.Description ?? string.Empty,
                Animatable = false,
                Choices = param.Choices ?? string.Empty,
            };
            parameters.Add(EffectRegistry.Describe(id, attribute));
        }

        if (floats > 8)
        {
            throw new InvalidOperationException($"Its parameters need {floats} numbers and a transition has eight.");
        }

        if (colours > 1)
        {
            throw new InvalidOperationException("It has more than one colour parameter; a transition has one, Tint.");
        }

        parameters.AddRange(TransitionEasing.Params);
        if (parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
        {
            throw new InvalidOperationException("It declares a parameter name twice, or one of easing and curve, which every transition has.");
        }

        var descriptor = new EffectDescriptor(
            id,
            EffectKind.Transition,
            manifest.Name is { Length: > 0 } name ? name : stem,
            manifest.Category is { Length: > 0 } category ? category : "Custom",
            manifest.Description is { Length: > 0 } description ? description : $"A transition from {Path.GetFileName(shader)}.",
            parameters.ToImmutable())
        {
            Implementation = typeof(ScriptedTransition),
            SourceFile = Path.GetFullPath(shader),
        };

        return new Loaded(descriptor, manifest.Entry is { Length: > 0 } entry ? entry : "PsMain");
    }

    private static ParamType TypeOf(string? type, string? name) => type?.ToLowerInvariant() switch
    {
        "float" or "number" or null => ParamType.Float,
        "int" or "integer" => ParamType.Int,
        "bool" or "switch" => ParamType.Bool,
        "enum" or "choice" => ParamType.Enum,
        "color" or "colour" => ParamType.Color,
        "float2" or "pair" => ParamType.Float2,
        "point" => ParamType.Point,
        _ => throw new InvalidOperationException($"Parameter '{name}' has type '{type}'; a transition takes float, int, bool, enum, colour, float2 or point."),
    };

    private static string Slug(string name) =>
        new string([.. name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-')]).Trim('-');

    private static void Watch(string folder)
    {
        if (_watcher is not null || !Directory.Exists(folder))
        {
            return;
        }

        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _watcher.Filters.Add("*.hlsl");
        _watcher.Filters.Add("*.json");
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        Log.Information("Watching {Folder} for transitions", folder);
    }

    private static void OnChanged(object sender, FileSystemEventArgs e)
    {
        // An editor saves in bursts (write, rename, touch): reload once when it settles.
        lock (Gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, 250, Timeout.Infinite);
        }
    }

    private static void Reload()
    {
        if (Folder is { } folder)
        {
            Load(folder, watch: false);
        }
    }

    private sealed record Loaded(EffectDescriptor Descriptor, string Entry);

    private sealed class Manifest
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Category { get; set; }

        public string? Description { get; set; }

        public string? Entry { get; set; }

        [JsonPropertyName("params")]
        public List<ManifestParam>? Params { get; set; }
    }

    private sealed class ManifestParam
    {
        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? Default { get; set; }

        public double? Min { get; set; }

        public double? Max { get; set; }

        public double? SliderMax { get; set; }

        public string? Unit { get; set; }

        public string? Label { get; set; }

        public string? Description { get; set; }

        public string? Choices { get; set; }
    }
}
