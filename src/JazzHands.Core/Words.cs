using System.Globalization;

namespace JazzHands.Core;

/// <summary>Counts as people write them: "1 chapter", "3 chapters", never "3 chapter(s)".</summary>
public static class Words
{
    /// <summary>
    /// <paramref name="count"/> and the noun, singular for one: <c>Count(1, "frame")</c> is "1 frame",
    /// <c>Count(2, "frame")</c> "2 frames". <paramref name="many"/> for a plural that is not a plain s.
    /// </summary>
    public static string Count(long count, string one, string? many = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many ?? one + "s")}");
}
