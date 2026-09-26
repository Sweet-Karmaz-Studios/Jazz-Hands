using System.Collections.Immutable;
using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Titles;
using JazzHands.Engine.Effects;
using Serilog;

namespace JazzHands.Engine.Titles;

/// <summary>
/// The title presets: those built in, and a person's own from <c>%APPDATA%\JazzHands\titles</c>.
/// </summary>
/// <remarks>
/// The built-in presets are JSON files compiled into the engine (<c>Titles/Presets</c>), in the
/// same format a person writes, so each is an example of the format. A person's file with a
/// built-in's name replaces it. The folder is read when first asked and again whenever a file in
/// it has changed; a file that does not read is logged and left out rather than failing a command.
/// </remarks>
public static class TitlePresetLibrary
{
    /// <summary>The preset <c>title.add</c> uses when none is named.</summary>
    public const string DefaultPreset = "title-card";

    private static readonly ILogger LogFor = Log.ForContext(typeof(TitlePresetLibrary));
    private static readonly Lock Gate = new();
    private static readonly Lazy<ImmutableArray<TitlePreset>> BuiltIns = new(ReadBuiltIns);
    private static (string Stamp, ImmutableArray<TitlePreset> Presets)? _users;

    /// <summary>The folder a person's own presets are read from; a test may point it elsewhere.</summary>
    public static string UserFolder { get; set; } = Path.Combine(
        JazzHands.Core.JazzFolders.Roaming, "titles");

    /// <summary>The presets that ship with the editor.</summary>
    public static ImmutableArray<TitlePreset> BuiltIn => BuiltIns.Value;

    /// <summary>Every preset, a person's replacing a built-in of the same name, built-ins first then by name.</summary>
    public static ImmutableArray<TitlePreset> All
    {
        get
        {
            ImmutableArray<TitlePreset> users = Users();
            var byName = new Dictionary<string, TitlePreset>(StringComparer.OrdinalIgnoreCase);
            foreach (TitlePreset preset in BuiltIn.Concat(users))
            {
                byName[preset.Name] = preset;
            }

            return [.. byName.Values.OrderByDescending(preset => preset.BuiltIn).ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>A preset by name, ignoring case, or a refusal that lists them.</summary>
    public static TitlePreset Require(string? name)
    {
        string wanted = string.IsNullOrWhiteSpace(name) ? DefaultPreset : name.Trim();
        ImmutableArray<TitlePreset> all = All;
        return all.FirstOrDefault(preset => string.Equals(preset.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException(
                "unknown-preset",
                $"There is no title preset called '{wanted}'. There are {string.Join(", ", all.Select(preset => preset.Name))}.");
    }

    /// <summary>A preset as <c>title.list-presets</c> shows it.</summary>
    public static TitlePresetInfo Info(TitlePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new TitlePresetInfo(
            preset.Name,
            preset.Label.Length > 0 ? preset.Label : preset.Name,
            preset.Description,
            preset.Text,
            preset.DefaultLength,
            preset.Animations.In,
            preset.Animations.Out,
            preset.Values,
            preset.BuiltIn,
            preset.Source);
    }

    private static ImmutableArray<TitlePreset> ReadBuiltIns()
    {
        EffectDescriptor title = Title();
        Assembly assembly = typeof(TitlePresetLibrary).Assembly;
        var presets = ImmutableArray.CreateBuilder<TitlePreset>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("JazzHands.Engine.Titles.Presets.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            if (!TitlePreset.TryRead(reader.ReadToEnd(), title, out TitlePreset? preset, out string? error))
            {
                throw new InvalidOperationException($"The built-in title preset {name} does not read: {error}");
            }

            presets.Add(preset! with { BuiltIn = true });
        }

        return presets.ToImmutable();
    }

    private static ImmutableArray<TitlePreset> Users()
    {
        string folder = UserFolder;
        string[] files = Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase)] : [];
        string stamp = folder + "|" + string.Join('|', files.Select(file => $"{file}:{File.GetLastWriteTimeUtc(file).Ticks}"));

        lock (Gate)
        {
            if (_users is { } known && known.Stamp == stamp)
            {
                return known.Presets;
            }

            EffectDescriptor title = Title();
            var presets = ImmutableArray.CreateBuilder<TitlePreset>();
            foreach (string file in files)
            {
                try
                {
                    if (TitlePreset.TryRead(File.ReadAllText(file), title, out TitlePreset? preset, out string? error))
                    {
                        presets.Add(preset! with { Source = file });
                    }
                    else
                    {
                        LogFor.Warning("The title preset {File} was left out: {Error}", file, error);
                    }
                }
                catch (IOException exception)
                {
                    LogFor.Warning(exception, "The title preset {File} could not be read", file);
                }
            }

            _users = (stamp, presets.ToImmutable());
            return _users.Value.Presets;
        }
    }

    private static EffectDescriptor Title() => EffectCatalog.Registry.Find(TitleParams.GeneratorId)
        ?? throw new InvalidOperationException("The title generator is not in the effect registry.");
}
