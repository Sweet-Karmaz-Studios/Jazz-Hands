using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Json.Schema;
using JazzHands.Core.Serialization;

namespace JazzHands.Core.Validation;

/// <summary>
/// The schema half of validation: is this document shaped like a project at all.
/// </summary>
/// <remarks>
/// This runs before deserialization, on the parsed document, so that a file with a string where a
/// number belongs is reported as "/sequences/0/tracks/0/clips/2/sourceIn: wrong-type" rather than
/// as an exception from somewhere inside System.Text.Json with no path in it. The semantic half,
/// in <see cref="Validator"/>, runs afterwards on the model.
///
/// The schema is generated from the model by <see cref="SchemaGenerator"/> rather than read from
/// disk, so validation cannot disagree with serialization even if the published copy is stale.
/// </remarks>
public static class SchemaValidator
{
    private static readonly Lazy<JsonSchema> Compiled = new(() =>
        JsonSchema.FromText(SchemaGenerator.Text));

    /// <summary>
    /// Checks a parsed document against the project schema.
    /// </summary>
    /// <remarks>
    /// Every issue is an error: a document that does not match the schema will not deserialize
    /// into anything trustworthy. An unrecognised member is not a schema failure, because the
    /// schema deliberately allows them; see <see cref="UnknownFields"/>.
    /// </remarks>
    public static ImmutableArray<ValidationIssue> Check(JsonNode? document)
    {
        // JsonSchema.Net evaluates a JsonElement, and the document arrives as a node because
        // migrations rewrite it in place before this runs.
        System.Text.Json.JsonElement element = System.Text.Json.JsonSerializer.SerializeToElement(document);

        EvaluationResults results = Compiled.Value.Evaluate(element, new EvaluationOptions
        {
            // Hierarchical, not List: the flat form loses the tree, and without it there is no way
            // to tell a failed branch of a satisfied anyOf from a real failure.
            OutputFormat = OutputFormat.Hierarchical,
            RequireFormatValidation = false,

            // Without this every failure is reported again at each level above it, so one wrong
            // frame width also accuses the sequence, the project and the root of being wrong.
            IncludeApplicatorErrors = false,
        });

        if (results.IsValid)
        {
            return [];
        }

        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        Collect(results, issues);

        // A single wrong value inside an anyOf produces one failure per branch, all saying the
        // same thing about the same place. The user wants one line per problem.
        return
        [
            .. issues
                .GroupBy(issue => issue.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(issue => issue.Path, StringComparer.Ordinal),
        ];
    }

    private static void Collect(EvaluationResults results, ImmutableArray<ValidationIssue>.Builder issues)
    {
        // A branch of an anyOf that did not match is not a problem when another branch did, and
        // an optional member is written as anyOf [something, null]. Without this, every media
        // identifier that is present would be reported as failing to be null.
        if (results.IsValid)
        {
            return;
        }

        if (results.Errors is { Count: > 0 } errors)
        {
            string path = results.InstanceLocation.ToString();

            foreach ((string keyword, string message) in errors)
            {
                issues.Add(new ValidationIssue(
                    Severity.Error,
                    CodeFor(keyword),
                    path.Length == 0 ? "/" : path,
                    message));
            }
        }

        foreach (EvaluationResults child in results.Details ?? [])
        {
            Collect(child, issues);
        }
    }

    /// <summary>
    /// Turns a schema keyword into one of the project's own stable codes.
    /// </summary>
    /// <remarks>
    /// Tools match on the code, so it has to stay the same even if the schema library renames a
    /// keyword or starts reporting one this does not know. Anything unrecognised becomes the
    /// generic code rather than leaking a library detail into the contract.
    /// </remarks>
    internal static string CodeFor(string keyword) => keyword switch
    {
        "type" => "wrong-type",
        "required" => "missing-member",
        "enum" => "not-a-member",
        "minimum" or "maximum" or "minItems" or "maxItems" => "out-of-range",
        "anyOf" or "oneOf" or "allOf" => "wrong-shape",
        _ => "schema",
    };
}
