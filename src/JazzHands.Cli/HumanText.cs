using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Cli;

/// <summary>
/// A query's answer as a person reads it: indented <c>name: value</c> lines, lists as dashes,
/// times as clocks, empty things left out.
/// </summary>
/// <remarks>
/// Every query answers with records, so one writer serves them all and a query written this
/// afternoon reads well this afternoon. <c>--json</c> is the form for scripts; this is the default
/// because a person, or Claude Code reading a terminal, wants <c>duration: 00:01:22.500</c> rather
/// than <c>"duration": 58212000000</c>. A description answers with its text.
/// </remarks>
public static class HumanText
{
    private const int MaxDepth = 6;

    /// <summary>The answer as text.</summary>
    public static string Render(object? answer)
    {
        switch (answer)
        {
            case null:
                return "nothing";
            case string words:
                return words.TrimEnd('\n');
            case ProjectDescription description:
                return description.Text.TrimEnd('\n');
        }

        var written = new StringBuilder();
        if (Scalar(answer) is { } single)
        {
            return single;
        }

        if (answer is IEnumerable list)
        {
            object?[] items = [.. list.Cast<object?>()];
            if (items.Length == 0)
            {
                return "none";
            }

            WriteList(written, items, 0);
        }
        else
        {
            WriteRecord(written, answer, 0);
        }

        return written.ToString().TrimEnd('\n');
    }

    private static void WriteList(StringBuilder text, object?[] items, int depth)
    {
        foreach (object? item in items)
        {
            if (Scalar(item) is { } single)
            {
                text.Append(' ', depth * 2).Append("- ").Append(single).Append('\n');
                continue;
            }

            // The first field goes on the dash's line, the rest under it.
            var record = new StringBuilder();
            WriteRecord(record, item!, depth + 1);
            string written = record.ToString();
            text.Append(' ', depth * 2).Append("- ").Append(written.TrimStart()).Append(written.Length == 0 ? "\n" : string.Empty);
        }
    }

    private static void WriteRecord(StringBuilder text, object record, int depth)
    {
        if (depth > MaxDepth)
        {
            text.Append(' ', depth * 2).Append("...\n");
            return;
        }

        foreach (PropertyInfo property in Properties(record.GetType()))
        {
            object? value = property.GetValue(record);
            if (Empty(value))
            {
                continue;
            }

            string name = JsonNamingPolicy.KebabCaseLower.ConvertName(property.Name);
            if (Scalar(value) is { } single)
            {
                text.Append(' ', depth * 2).Append(name).Append(": ").Append(single).Append('\n');
            }
            else if (value is IEnumerable list)
            {
                text.Append(' ', depth * 2).Append(name).Append(":\n");
                WriteList(text, [.. list.Cast<object?>()], depth + 1);
            }
            else
            {
                text.Append(' ', depth * 2).Append(name).Append(":\n");
                WriteRecord(text, value!, depth + 1);
            }
        }
    }

    /// <summary>A value that fits on one line, or null for a record or a list.</summary>
    private static string? Scalar(object? value) => value switch
    {
        null => "none",
        string text => text.Contains('\n', StringComparison.Ordinal) ? $"\"{text.Replace("\n", "\\n", StringComparison.Ordinal)}\"" : text,
        bool flag => flag ? "yes" : "no",
        Flicks time => Timecode.FormatClock(time),
        TimeRange range => $"{Timecode.FormatClock(range.Start)} to {Timecode.FormatClock(range.End)}",
        Rational rate => rate.ToString(),
        FrameSize size => size.ToString(),
        Enum member => JsonNamingPolicy.KebabCaseLower.ConvertName(member.ToString()),
        DateTimeOffset moment => moment.ToString("u", CultureInfo.InvariantCulture),
        double number => number.ToString("0.####", CultureInfo.InvariantCulture),
        float number => number.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable formattable when value.GetType().IsPrimitive || value is decimal => formattable.ToString(null, CultureInfo.InvariantCulture),
        System.Text.Json.Nodes.JsonNode node => node.ToJsonString(),
        _ => null,
    };

    /// <summary>Nothing worth a line: absent, empty, or an empty list.</summary>
    private static bool Empty(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        IEnumerable list when value is not string => !list.Cast<object?>().Any(),
        _ => false,
    };

    /// <summary>The public properties a record was declared with, in declaration order.</summary>
    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.Name != "EqualityContract")
            .OrderBy(property => property.MetadataToken);
}
