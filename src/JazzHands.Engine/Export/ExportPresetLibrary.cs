using System.Collections.Immutable;
using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// The export presets: those built in, a person's own from <c>%APPDATA%\JazzHands\export-presets</c>,
/// and a project's own.
/// </summary>
/// <remarks>
/// The built-in presets are JSON files compiled into the engine (<c>Export/Presets</c>), in the
/// same format a person writes, so each is an example of it. A person's file with a built-in's
/// name replaces it, and deleting that file brings the built-in back. The folder is read when first
/// asked and again whenever a file in it has changed; a file that does not read is logged and left
/// out rather than failing an export that names another preset. A project's presets
/// (<see cref="Core.Model.Project.ExportPresets"/>) travel with it and come first: one with the name
/// of a person's or a built-in replaces it for that project, wherever a preset is named. Their
/// <see cref="ExportPreset.Source"/> is <see cref="ProjectSource"/>.
/// </remarks>
public static class ExportPresetLibrary
{
    private static readonly ILogger LogFor = Log.ForContext(typeof(ExportPresetLibrary));
    private static readonly Lock Gate = new();
    private static readonly Lazy<ImmutableArray<ExportPreset>> BuiltIns = new(ReadBuiltIns);
    private static (string Stamp, ImmutableArray<ExportPreset> Presets)? _users;

    /// <summary>The folder a person's own presets are read from and saved to; a test may point it elsewhere.</summary>
    public static string UserFolder { get; set; } = Path.Combine(
        JazzHands.Core.JazzFolders.Roaming, "export-presets");

    /// <summary>What <see cref="ExportPreset.Source"/> says for a preset kept in the project.</summary>
    public const string ProjectSource = "project";

    /// <summary>The presets that ship with the editor.</summary>
    public static ImmutableArray<ExportPreset> BuiltIn => BuiltIns.Value;

    /// <summary>Every preset, a person's replacing a built-in of the same name, in category order then by name.</summary>
    public static ImmutableArray<ExportPreset> All
    {
        get
        {
            var byName = new Dictionary<string, ExportPreset>(StringComparer.OrdinalIgnoreCase);
            foreach (ExportPreset preset in BuiltIn.Concat(Users()))
            {
                byName[preset.Name] = preset;
            }

            return [.. byName.Values
                .OrderBy(preset => IndexOf(preset.Category))
                .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Every preset for a project: its own first (those that check out, the first of a name), then
    /// the rest of <see cref="All"/> not of those names.
    /// </summary>
    public static ImmutableArray<ExportPreset> For(Core.Model.Project? project)
    {
        ImmutableArray<ExportPreset> own = Own(project);
        if (own.IsEmpty)
        {
            return All;
        }

        var names = new HashSet<string>(own.Select(preset => preset.Name), StringComparer.OrdinalIgnoreCase);
        return [.. own, .. All.Where(preset => !names.Contains(preset.Name))];
    }

    /// <summary>A project's own presets that can be used, marked as the project's.</summary>
    public static ImmutableArray<ExportPreset> Own(Core.Model.Project? project)
    {
        if (project is null || project.ExportPresets.IsEmpty)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. project.ExportPresets
            .Where(preset => ExportPresets.Check(preset) is null && names.Add(preset.Name))
            .Select(preset => preset with { BuiltIn = false, Source = ProjectSource })];
    }

    /// <summary>A preset by name, ignoring case, the project's first; or null.</summary>
    public static ExportPreset? Find(string? name, Core.Model.Project? project = null) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : For(project).FirstOrDefault(preset => string.Equals(preset.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A preset by name, the project's first, or a refusal that lists them.</summary>
    public static ExportPreset Require(string? name, Core.Model.Project? project = null)
    {
        string wanted = string.IsNullOrWhiteSpace(name) ? ExportPresets.Default : name.Trim();
        return Find(wanted, project)
            ?? throw new CommandException(
                "unknown-preset",
                $"There is no preset called '{wanted}'. The presets are {string.Join(", ", For(project).Select(preset => preset.Name))}.");
    }

    /// <summary>Writes a person's preset, replacing their own of the same name; returns the file.</summary>
    /// <exception cref="CommandException">When the preset does not check out.</exception>
    public static string Save(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (ExportPresets.Check(preset) is { } error)
        {
            throw new CommandException("invalid-preset", error);
        }

        Directory.CreateDirectory(UserFolder);
        string path = PathFor(preset.Name);
        File.WriteAllText(path, ExportPresets.ToJson(preset), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        LogFor.Information("Saved the export preset {Name} to {Path}", preset.Name, path);
        return path;
    }

    /// <summary>Deletes a person's preset. A built-in cannot be deleted; one of theirs that replaced it can, and the built-in comes back.</summary>
    /// <returns>The file deleted.</returns>
    public static string Delete(string name)
    {
        ExportPreset preset = Require(name);
        if (preset.BuiltIn)
        {
            throw new CommandException(
                "built-in-preset",
                $"'{preset.Name}' ships with Jazz Hands and cannot be deleted. Save one of your own with the same name to replace it.");
        }

        File.Delete(preset.Source);
        LogFor.Information("Deleted the export preset {Name} at {Path}", preset.Name, preset.Source);
        return preset.Source;
    }

    /// <summary>Where a person's preset of this name is kept.</summary>
    public static string PathFor(string name) => Path.Combine(UserFolder, name.Trim().ToLowerInvariant() + ".json");

    private static int IndexOf(string category)
    {
        int index = ExportPresets.Categories.ToList().IndexOf(category);
        return index < 0 ? int.MaxValue : index;
    }

    private static ImmutableArray<ExportPreset> ReadBuiltIns()
    {
        Assembly assembly = typeof(ExportPresetLibrary).Assembly;
        var presets = ImmutableArray.CreateBuilder<ExportPreset>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("JazzHands.Engine.Export.Presets.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            if (!ExportPresets.TryRead(reader.ReadToEnd(), out ExportPreset? preset, out string? error))
            {
                throw new InvalidOperationException($"The built-in export preset {name} does not read: {error}");
            }

            presets.Add(preset! with { BuiltIn = true });
        }

        return presets.ToImmutable();
    }

    private static ImmutableArray<ExportPreset> Users()
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

            var presets = ImmutableArray.CreateBuilder<ExportPreset>();
            foreach (string file in files)
            {
                try
                {
                    if (ExportPresets.TryRead(File.ReadAllText(file), out ExportPreset? preset, out string? error))
                    {
                        presets.Add(preset! with { Source = file });
                    }
                    else
                    {
                        LogFor.Warning("The export preset {File} was left out: {Error}", file, error);
                    }
                }
                catch (IOException exception)
                {
                    LogFor.Warning(exception, "The export preset {File} could not be read", file);
                }
            }

            _users = (stamp, presets.ToImmutable());
            return _users.Value.Presets;
        }
    }
}
