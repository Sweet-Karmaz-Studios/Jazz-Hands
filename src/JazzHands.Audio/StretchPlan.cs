using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace JazzHands.Audio;

/// <summary>
/// A clip's sound played at its speed with its pitch kept (Phase 36): which source samples each
/// clip sample stands for, and the key its stretched sound is cached under.
/// </summary>
/// <remarks>
/// The mapping is the one <see cref="ClipMix"/> reads tape-style: forwards from
/// <see cref="SourceIn"/>, backwards from <see cref="SourceOut"/>, or along a speed curve, as a
/// numerator over <see cref="SpeedDen"/>. The graph does not stretch; it names the plan in its
/// source (<see cref="AudioSourceRef.Stretch"/>) and reads clip samples straight, and the engine
/// renders them through a time stretcher off the audio thread. Two clips with the same plan share
/// the render. Positions are clip samples and may be negative or past the clip's length, for the
/// lead in and tail of a crossfade.
/// </remarks>
public sealed class StretchPlan : IEquatable<StretchPlan>
{
    /// <summary>Makes a plan.</summary>
    public StretchPlan(long sourceIn, long sourceOut, long speedNum, long speedDen, bool reverse, long[]? remap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speedNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speedDen);

        SourceIn = sourceIn;
        SourceOut = sourceOut;
        SpeedNum = speedNum;
        SpeedDen = speedDen;
        Reverse = reverse;
        Remap = remap;
        Key = string.Create(
            CultureInfo.InvariantCulture,
            $"stretch:{sourceIn}:{sourceOut}:{speedNum}/{speedDen}:{(reverse ? 'r' : 'f')}:{(remap is null ? "0" : XxHash64.HashToUInt64(MemoryMarshal.AsBytes(remap.AsSpan())).ToString("x16", CultureInfo.InvariantCulture))}");
    }

    /// <summary>The slowest a clip is stretched; slower plays like tape. Matches the engine's stretcher.</summary>
    public const double MinTempo = 0.05;

    /// <summary>The fastest a clip is stretched; faster plays like tape.</summary>
    public const double MaxTempo = 16.0;

    /// <summary>The first source sample the clip plays, at the mix rate.</summary>
    public long SourceIn { get; }

    /// <summary>One past the last.</summary>
    public long SourceOut { get; }

    /// <summary>Speed numerator; for a speed curve, the fastest speed over the fixed denominator.</summary>
    public long SpeedNum { get; }

    /// <summary>Speed denominator: positions are numerators over it.</summary>
    public long SpeedDen { get; }

    /// <summary>True when the clip plays backwards.</summary>
    public bool Reverse { get; }

    /// <summary>For a speed curve, the source position every <see cref="ClipMix.RemapStep"/> clip samples; null at one speed.</summary>
    public long[]? Remap { get; }

    /// <summary>What the stretched sound is cached under: every value above, the curve by its hash.</summary>
    public string Key { get; }

    /// <summary>The source position of a clip sample, as a numerator over <see cref="SpeedDen"/>; the same as <see cref="ClipMix.SourcePosition"/>.</summary>
    public long SourcePosition(long clipSample)
    {
        if (Remap is { Length: > 1 } table)
        {
            long index = Math.Clamp(Dsp.FloorDiv(clipSample, ClipMix.RemapStep), 0, table.Length - 2);
            long offset = clipSample - (index * ClipMix.RemapStep);
            return table[index] + ((table[index + 1] - table[index]) * offset / ClipMix.RemapStep);
        }

        return Reverse
            ? (SourceOut * SpeedDen) - ((clipSample + 1) * SpeedNum)
            : (SourceIn * SpeedDen) + (clipSample * SpeedNum);
    }

    /// <inheritdoc />
    public bool Equals(StretchPlan? other) => other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as StretchPlan);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
}
