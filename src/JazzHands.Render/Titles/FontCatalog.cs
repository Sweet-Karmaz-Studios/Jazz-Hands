using System.Collections.Immutable;
using System.Globalization;
using Serilog;
using Vortice.DirectWrite;

namespace JazzHands.Render.Titles;

/// <summary>One font family a title can use.</summary>
/// <param name="Family">Its name, as the <c>font</c> parameter takes it.</param>
/// <param name="IsProject">True for one from the project's fonts folder.</param>
/// <param name="Faces">How many weights and styles it has.</param>
public sealed record FontFamilyInfo(string Family, bool IsProject, int Faces);

/// <summary>
/// The fonts titles are drawn with: those installed, and a project's own in a <c>fonts</c> folder
/// beside its file.
/// </summary>
/// <remarks>
/// A project's fonts and the installed ones make one DirectWrite collection, the project's added
/// first so a family in both is the project's. Collections are made once per folder and made again
/// when a file in the folder is added, removed or saved. The factory is DirectWrite's shared one,
/// which is safe from any thread, so the engine can list fonts and the compositor lay text out at
/// once. A family that is in neither is drawn in <see cref="Fallback"/>, and validation says which
/// clips asked for it.
/// </remarks>
public static class FontCatalog
{
    /// <summary>The family a title falls back to when its own is not on this machine.</summary>
    public const string Fallback = "Segoe UI";

    /// <summary>The folder beside a project file its own fonts are read from.</summary>
    public const string FolderName = "fonts";

    private static readonly ILogger LogFor = Log.ForContext(typeof(FontCatalog));
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (string Stamp, IDWriteFontCollection1 Collection, ImmutableHashSet<string> Project)> Collections = new(StringComparer.OrdinalIgnoreCase);
    private static IDWriteFactory5? _factory;
    private static IDWriteFontCollection1? _system;

    /// <summary>DirectWrite's shared factory, at the level font sets need.</summary>
    public static IDWriteFactory5 Factory
    {
        get
        {
            lock (Gate)
            {
                return _factory ??= DWrite.DWriteCreateFactory<IDWriteFactory5>(FactoryType.Shared);
            }
        }
    }

    /// <summary>The fonts folder for a project folder, which may not exist.</summary>
    public static string FolderFor(string projectFolder) =>
        string.IsNullOrEmpty(projectFolder) ? string.Empty : Path.Combine(projectFolder, FolderName);

    /// <summary>
    /// The collection text is laid out from: the project's fonts then the installed ones, or only
    /// the installed ones when the project has none.
    /// </summary>
    /// <param name="projectFolder">The folder the project file is in; empty for none.</param>
    public static IDWriteFontCollection1 Collection(string projectFolder) => Entry(projectFolder).Collection;

    /// <summary>True when a family can be drawn: installed, or in the project's fonts folder.</summary>
    public static bool Has(string family, string projectFolder)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        IDWriteFontCollection1 collection = Collection(projectFolder);
        lock (Gate)
        {
            return collection.FindFamilyName(family, out _);
        }
    }

    /// <summary>The family to draw with: the one asked for when there is one, the fallback otherwise.</summary>
    public static string Resolve(string family, string projectFolder) => Has(family, projectFolder) ? family : Fallback;

    /// <summary>Every family, the project's first, then by name.</summary>
    public static ImmutableArray<FontFamilyInfo> Families(string projectFolder)
    {
        (_, IDWriteFontCollection1 collection, ImmutableHashSet<string> project) = Entry(projectFolder);
        var families = new Dictionary<string, FontFamilyInfo>(StringComparer.OrdinalIgnoreCase);

        lock (Gate)
        {
            uint count = collection.FontFamilyCount;
            for (uint index = 0; index < count; index++)
            {
                using IDWriteFontFamily1 family = collection.GetFontFamily(index);
                using IDWriteLocalizedStrings names = family.FamilyNames;
                string name = Name(names);
                if (name.Length == 0)
                {
                    continue;
                }

                int faces = (int)family.FontCount;
                families[name] = families.TryGetValue(name, out FontFamilyInfo? known)
                    ? known with { Faces = known.Faces + faces }
                    : new FontFamilyInfo(name, project.Contains(name), faces);
            }
        }

        return [.. families.Values.OrderByDescending(family => family.IsProject).ThenBy(family => family.Family, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The families the fonts in a project's folder have, whether or not they are also installed.</summary>
    public static ImmutableHashSet<string> ProjectFamilies(string projectFolder) => Entry(projectFolder).Project;

    private static (string Stamp, IDWriteFontCollection1 Collection, ImmutableHashSet<string> Project) Entry(string projectFolder)
    {
        string folder = FolderFor(projectFolder);
        string[] files = folder.Length > 0 && Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder).Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)]
            : [];

        IDWriteFactory5 factory = Factory;
        lock (Gate)
        {
            if (files.Length == 0)
            {
                _system ??= ((IDWriteFactory3)factory).GetSystemFontCollection(false, false);
                return (string.Empty, _system, ImmutableHashSet<string>.Empty);
            }

            string stamp = string.Join('|', files.Select(file => string.Create(CultureInfo.InvariantCulture, $"{file}:{File.GetLastWriteTimeUtc(file).Ticks}")));
            if (Collections.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
            {
                return cached;
            }

            using IDWriteFontSetBuilder1 builder = factory.CreateFontSetBuilder();
            var projectBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                try
                {
                    using IDWriteFontFile font = factory.CreateFontFileReference(file, null);
                    builder.AddFontFile(font);
                }
                catch (SharpGen.Runtime.SharpGenException exception)
                {
                    LogFor.Warning(exception, "The font file {File} could not be read", file);
                }
            }

            using (IDWriteFontSet projectSet = builder.CreateFontSet())
            using (IDWriteFontCollection1 projectOnly = factory.CreateFontCollectionFromFontSet(projectSet))
            {
                for (uint index = 0; index < projectOnly.FontFamilyCount; index++)
                {
                    using IDWriteFontFamily1 family = projectOnly.GetFontFamily(index);
                    using IDWriteLocalizedStrings names = family.FamilyNames;
                    projectBuilder.Add(Name(names));
                }
            }

            using IDWriteFontSet system = factory.SystemFontSet;
            builder.AddFontSet(system);
            using IDWriteFontSet all = builder.CreateFontSet();
            IDWriteFontCollection1 collection = factory.CreateFontCollectionFromFontSet(all);

            if (Collections.TryGetValue(folder, out var stale))
            {
                stale.Collection.Dispose();
            }

            var entry = (stamp, collection, projectBuilder.ToImmutable());
            Collections[folder] = entry;
            LogFor.Information("Read {Count} font files from {Folder}: {Families}", files.Length, folder, string.Join(", ", entry.Item3));
            return entry;
        }
    }

    /// <summary>A family's name in US English, or its first name when it has none in that.</summary>
    private static string Name(IDWriteLocalizedStrings names)
    {
        uint index = names.FindLocaleName("en-us", out uint at) ? at : 0;
        return names.Count > 0 ? names.GetString(index) : string.Empty;
    }
}
