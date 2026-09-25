using System.Globalization;

namespace JazzHands.Render;

/// <summary>Which adapter to render on: the best GPU, WARP, or a GPU by number.</summary>
/// <param name="Warp">Render on the software rasterizer.</param>
/// <param name="Adapter">A hardware adapter by number, fastest first, or null for the best one.</param>
public sealed record GpuChoice(bool Warp = false, uint? Adapter = null)
{
    /// <summary>The best GPU there is, or WARP when there is none.</summary>
    public static GpuChoice Auto { get; } = new();

    /// <summary>Reads <c>auto</c>, <c>warp</c> or an adapter number; empty is auto.</summary>
    /// <exception cref="FormatException">When the text is none of those.</exception>
    public static GpuChoice Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" => Auto,
        "warp" => new GpuChoice(Warp: true),
        string number when uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out uint index) => new GpuChoice(Adapter: index),
        string other => throw new FormatException($"--gpu takes auto, warp or an adapter number, not '{other}'."),
    };

    /// <summary>Reads <c>auto</c>, <c>warp</c> or an adapter number, or says it could not.</summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GpuChoice? choice)
    {
        try
        {
            choice = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            choice = null;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => Warp ? "warp" : Adapter is { } number ? number.ToString(CultureInfo.InvariantCulture) : "auto";
}
