using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.Core.Effects;

/// <summary>
/// Every effect type there is, found by their attributes.
/// </summary>
/// <remarks>
/// Nothing registers an effect by hand. The render and audio layers each scan their own assembly
/// for <see cref="EffectAttribute"/> classes and the engine joins the two, so an effect written
/// with its attributes is in the browser, the inspector, <c>jazz effect list</c> and the MCP
/// tools the moment it compiles. Core cannot see either layer, which is why it is handed
/// assemblies rather than finding them.
/// </remarks>
public sealed class EffectRegistry
{
    private readonly FrozenDictionary<string, EffectDescriptor> _byId;

    /// <summary>Creates a registry from descriptors.</summary>
    /// <exception cref="InvalidOperationException">Two descriptors share a type identifier.</exception>
    public EffectRegistry(IEnumerable<EffectDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var byId = new Dictionary<string, EffectDescriptor>(StringComparer.Ordinal);
        foreach (EffectDescriptor descriptor in descriptors)
        {
            if (!byId.TryAdd(descriptor.TypeId, descriptor))
            {
                throw new InvalidOperationException($"Two effects are called '{descriptor.TypeId}'.");
            }
        }

        _byId = byId.ToFrozenDictionary(StringComparer.Ordinal);
        All = [.. byId.Values.OrderBy(descriptor => descriptor.Kind).ThenBy(descriptor => descriptor.Category, StringComparer.Ordinal).ThenBy(descriptor => descriptor.Name, StringComparer.Ordinal)];
    }

    /// <summary>A registry with nothing in it.</summary>
    public static EffectRegistry Empty { get; } = new([]);

    /// <summary>Every effect type, by kind, then category, then name.</summary>
    public ImmutableArray<EffectDescriptor> All { get; }

    /// <summary>Scans assemblies for effect classes.</summary>
    public static EffectRegistry FromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return new EffectRegistry(assemblies.SelectMany(Scan));
    }

    /// <summary>The descriptors of every attributed class in an assembly.</summary>
    public static IEnumerable<EffectDescriptor> Scan(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (Type type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<EffectAttribute>(inherit: false) is { } effect)
            {
                yield return Describe(type, effect);
            }
        }
    }

    /// <summary>Joins registries, for the engine, which sees both the render and the audio layers.</summary>
    public static EffectRegistry Combine(params EffectRegistry[] registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        return new EffectRegistry(registries.SelectMany(registry => registry.All));
    }

    /// <summary>Reads one class's attributes into a descriptor.</summary>
    /// <exception cref="InvalidOperationException">A default, a limit or a choice does not make sense.</exception>
    public static EffectDescriptor Describe(Type type, EffectAttribute effect)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(effect);

        var parameters = ImmutableArray.CreateBuilder<ParamDescriptor>();
        foreach (ParamAttribute param in type.GetCustomAttributes<ParamAttribute>(inherit: false))
        {
            parameters.Add(Describe(effect.Id, param));
        }

        if (parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
        {
            throw new InvalidOperationException($"Effect '{effect.Id}' declares a parameter name twice.");
        }

        var video = effect as VideoEffectAttribute;
        return new EffectDescriptor(
            effect.Id,
            effect.Kind,
            effect.Name.Length > 0 ? effect.Name : effect.Id,
            effect.Category,
            effect.Description,
            parameters.ToImmutable())
        {
            Implementation = type,
            FramesBefore = video?.FramesBefore ?? 0,
            FramesAfter = video?.FramesAfter ?? 0,
            Seeded = video?.Seeded ?? false,
        };
    }

    /// <summary>The descriptor for a type identifier, or null.</summary>
    public EffectDescriptor? Find(string? typeId) =>
        typeId is not null && _byId.TryGetValue(typeId, out EffectDescriptor? descriptor) ? descriptor : null;

    /// <summary>The descriptor for a type identifier, or a coded refusal that suggests the nearest.</summary>
    public EffectDescriptor Require(string typeId)
    {
        if (Find(typeId) is { } descriptor)
        {
            return descriptor;
        }

        string? near = All
            .Select(candidate => candidate.TypeId)
            .OrderBy(candidate => Distance(candidate, typeId ?? string.Empty))
            .FirstOrDefault();

        throw new CommandException(
            "unknown-effect",
            near is null
                ? $"There is no effect called '{typeId}'."
                : $"There is no effect called '{typeId}'. Did you mean '{near}'? 'jazz effect list' shows them all.");
    }

    private static ParamDescriptor Describe(string typeId, ParamAttribute param)
    {
        EquatableArray<string> choices = param.Choices.Length == 0
            ? EquatableArray<string>.Empty
            : new EquatableArray<string>(param.Choices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (param.Type == ParamType.Enum && choices.IsEmpty)
        {
            throw new InvalidOperationException($"Enum parameter '{param.Name}' of '{typeId}' lists no choices.");
        }

        var descriptor = new ParamDescriptor(
            param.Name,
            param.Type,
            new ParamValue.Float(0),
            param.Label,
            param.Description,
            double.IsNaN(param.Min) ? null : param.Min,
            double.IsNaN(param.Max) ? null : param.Max,
            double.IsNaN(param.SliderMax) ? null : param.SliderMax,
            param.Unit,
            param.Animatable,
            choices);

        string text = param.Default.Length > 0 ? param.Default : EmptyDefault(param.Type, choices);
        if (!ParamValues.TryParse(descriptor, text, out ParamValue? value, out string? error))
        {
            throw new InvalidOperationException($"The default of '{param.Name}' on '{typeId}' does not parse: {error}");
        }

        return descriptor with { Default = value! };
    }

    private static string EmptyDefault(ParamType type, EquatableArray<string> choices) => type switch
    {
        ParamType.Float or ParamType.Int => "0",
        ParamType.Float2 or ParamType.Point => "0, 0",
        ParamType.Float4 => "0, 0, 0, 0",
        ParamType.Color => "#FFFFFF",
        ParamType.Bool => "false",
        ParamType.Enum => choices[0],
        _ => string.Empty,
    };

    /// <summary>Edit distance, for suggesting the effect somebody probably meant.</summary>
    private static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
