using System.Collections.Immutable;
using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Core.Templates;
using Serilog;

namespace JazzHands.Engine.Templates;

/// <summary>
/// The motion templates: those built in, and a person's own from <c>%APPDATA%\JazzHands\templates</c>,
/// where <c>template.save-selection</c> writes them. One with a built-in's name replaces it.
/// </summary>
public static class TemplateLibrary
{
    private static readonly ILogger LogFor = Log.ForContext(typeof(TemplateLibrary));
    private static readonly Lock Gate = new();
    private static readonly Lazy<ImmutableArray<MotionTemplate>> BuiltIns = new(ReadBuiltIns);
    private static (string Stamp, ImmutableArray<MotionTemplate> Templates)? _users;

    /// <summary>The folder a person's own templates are read from and saved to; a test may point it elsewhere.</summary>
    public static string UserFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JazzHands", "templates");

    /// <summary>The templates that ship with the editor.</summary>
    public static ImmutableArray<MotionTemplate> BuiltIn => BuiltIns.Value;

    /// <summary>Every template, a person's replacing a built-in of the same name, built-ins first then by name.</summary>
    public static ImmutableArray<MotionTemplate> All
    {
        get
        {
            var byName = new Dictionary<string, MotionTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (MotionTemplate template in BuiltIn.Concat(Users()))
            {
                byName[template.Name] = template;
            }

            return [.. byName.Values.OrderByDescending(template => template.BuiltIn).ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>A template by name, ignoring case, or a refusal that lists them.</summary>
    public static MotionTemplate Require(string name)
    {
        ImmutableArray<MotionTemplate> all = All;
        return all.FirstOrDefault(template => string.Equals(template.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException("unknown-template", $"There is no template called '{name}'. There are {string.Join(", ", all.Select(template => template.Name))}.");
    }

    /// <summary>A template as <c>template.list</c> shows it.</summary>
    public static TemplateInfo Info(MotionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new TemplateInfo(
            template.Name,
            template.Label.Length > 0 ? template.Label : template.Name,
            template.Description,
            template.Values,
            template.Steps?.Count ?? 0,
            template.BuiltIn,
            template.Source);
    }

    private static ImmutableArray<MotionTemplate> ReadBuiltIns()
    {
        Assembly assembly = typeof(TemplateLibrary).Assembly;
        var templates = ImmutableArray.CreateBuilder<MotionTemplate>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("JazzHands.Engine.Templates.Builtin.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            if (!MotionTemplate.TryRead(reader.ReadToEnd(), out MotionTemplate? template, out string? error))
            {
                throw new InvalidOperationException($"The built-in template {name} does not read: {error}");
            }

            templates.Add(template! with { BuiltIn = true });
        }

        return templates.ToImmutable();
    }

    private static ImmutableArray<MotionTemplate> Users()
    {
        string folder = UserFolder;
        string[] files = Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase)] : [];
        string stamp = folder + "|" + string.Join('|', files.Select(file => $"{file}:{File.GetLastWriteTimeUtc(file).Ticks}"));

        lock (Gate)
        {
            if (_users is { } known && known.Stamp == stamp)
            {
                return known.Templates;
            }

            var templates = ImmutableArray.CreateBuilder<MotionTemplate>();
            foreach (string file in files)
            {
                try
                {
                    if (MotionTemplate.TryRead(File.ReadAllText(file), out MotionTemplate? template, out string? error))
                    {
                        templates.Add(template! with { Source = file });
                    }
                    else
                    {
                        LogFor.Warning("The template {File} was left out: {Error}", file, error);
                    }
                }
                catch (IOException exception)
                {
                    LogFor.Warning(exception, "The template {File} could not be read", file);
                }
            }

            _users = (stamp, templates.ToImmutable());
            return _users.Value.Templates;
        }
    }
}
