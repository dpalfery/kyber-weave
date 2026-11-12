using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Review;

/// <summary>The gate suite's result document.</summary>
/// <param name="Schema">Format identifier, for readers that must tolerate later versions.</param>
/// <param name="Gates">One entry per declared gate, in declaration order.</param>
/// <param name="Coverage">Measured coverage, when a gate produced any.</param>
/// <param name="CoverageReports">
/// Every Cobertura report the run wrote, <see cref="Coverage"/>'s own included. A suite that
/// tests more than one language writes one report each, and only one of them can be the
/// figure a coverage floor is checked against; the rest are evidence a reviewer reads here.
/// </param>
/// <param name="Base">
/// The commit the change was measured against. Absent in reports written before the
/// Arbiter started recording it; readers treat a missing base as an absent fact.
/// </param>
public sealed record GateReport(
    string Schema,
    IReadOnlyList<GateResult> Gates,
    CoverageResult? Coverage = null,
    IReadOnlyList<CoverageReport>? CoverageReports = null,
    string? Base = null)
{
    /// <summary>The current gate report format.</summary>
    public const string CurrentSchema = "kyber-weave.review-gates/v1";
}

/// <summary>The council's findings document.</summary>
/// <param name="Schema">Format identifier.</param>
/// <param name="Findings">Findings surviving the council's own confirmation pass.</param>
/// <param name="ChangedPaths">Repository-relative paths the change touches.</param>
/// <param name="ChangedLines">Total added and removed lines.</param>
/// <param name="PathLineCounts">Per-path added and removed lines, when the producer counted them.</param>
/// <param name="Coverage">Per-lens outcome of the council, when the producer recorded it.</param>
public sealed record FindingsReport(
    string Schema,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines,
    IReadOnlyDictionary<string, int>? PathLineCounts = null,
    IReadOnlyList<LensCoverage>? Coverage = null)
{
    /// <summary>The current findings format.</summary>
    public const string CurrentSchema = "kyber-weave.review-findings/v1";

    /// <summary>
    /// The scope metadata this report describes. The verdict engine does not consume it; this is
    /// a convenience accessor for reporting the change's size and paths.
    /// </summary>
    public ReviewScope ToScope()
    {
        return new ReviewScope(ChangedPaths, ChangedLines, PathLineCounts);
    }
}

/// <summary>One council lens's outcome in the findings document.</summary>
/// <param name="Lens">The lens name, as listed in the review lens catalogue.</param>
/// <param name="Outcome">How the lens ended, such as findings, no findings, or skipped.</param>
/// <param name="Reason">Why the lens ended that way, when it did not produce findings.</param>
public sealed record LensCoverage(string Lens, string Outcome, string? Reason);

/// <summary>Reads and writes the review documents that pass between layers.</summary>
/// <remarks>
/// The documents are the seam between an agentic layer and a deterministic one, so they are
/// strict in one direction and forgiving in the other: unknown properties are tolerated on
/// read, because a lens may carry more than the engine consumes, but a missing required
/// field is a parse failure rather than a silently defaulted value. A finding that arrives
/// without its evidence must reach the engine as a finding without evidence, so that
/// <see cref="VerdictEngine.IncompleteFinding"/> can drop it and say so.
/// </remarks>
public static class ReviewJson
{
    private const string SnakeCaseFailureScenario = "failure_scenario";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Serializes a gate report.</summary>
    public static string Write(GateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    /// <summary>Serializes a findings report.</summary>
    public static string Write(FindingsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    /// <summary>Serializes a duplicates report.</summary>
    public static string Write(DuplicateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    /// <summary>Reads a gate report.</summary>
    public static GateReport ReadGates(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonNode? root = JsonNode.Parse(json);
        RejectNullListEntries(root, typeof(GateReport), "$");
        GateReport report = root.Deserialize<GateReport>(Options)
                            ?? throw new JsonException("The gate report is empty.");
        if (report.Gates is null)
            throw new JsonException("The gate report's 'gates' must be a list of gate results.");

        if (report.Gates.FirstOrDefault(gate => string.IsNullOrWhiteSpace(gate.Id) || gate.Summary is null) is { } bad)
        {
            throw new JsonException(
                $"Gate result {report.Gates.ToList().IndexOf(bad) + 1} in the gate report has no 'id' or no 'summary'.");
        }

        return report;
    }

    /// <summary>Reads a findings report.</summary>
    /// <remarks>
    /// A seat writes its failure scenario as <c>failure_scenario</c>, the spelling its prompt uses, while the
    /// document's own spelling is <c>failureScenario</c>. Both are read, so a finding is never dropped as
    /// incomplete for the spelling of a field it has. The document spelling wins when both are present.
    /// </remarks>
    public static FindingsReport ReadFindings(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonNode? root = JsonNode.Parse(json);
        RejectNullListEntries(root, typeof(FindingsReport), "$");
        if (root is JsonObject document && document["findings"] is JsonArray findings)
        {
            for (int index = 0; index < findings.Count; index++)
            {
                if (findings[index] is not JsonObject finding)
                    continue;

                CheckRanges(finding, $"$.findings[{index}]");
                BindSnakeCaseFailureScenario(finding);
            }
        }

        FindingsReport report = root.Deserialize<FindingsReport>(Options)
                                ?? throw new JsonException("The findings report is empty.");
        if (report.Findings is null)
            throw new JsonException("The findings report's 'findings' must be a list of findings.");

        // The serializer fills a JSON null into a string without complaint, so a finding with no id, lens or file
        // would reach the engine with no identity. Every required text field is checked here, in one place.
        for (int index = 0; index < report.Findings.Count; index++)
        {
            ReviewFinding finding = report.Findings[index];
            (string Name, string? Value)[] required =
                [("id", finding.Id), ("lens", finding.Lens), ("file", finding.File)];
            if (required.FirstOrDefault(field => string.IsNullOrWhiteSpace(field.Value)) is { Name: { } missing })
            {
                throw new JsonException(
                    $"Finding {index + 1} in the findings report has no '{missing}'; id, lens and file are required.");
            }
        }

        return report;
    }

    /// <summary>
    /// Refuses a <c>null</c> element in any list the reader binds, at any depth, naming its JSON path.
    /// </summary>
    /// <remarks>
    /// The lists are found from the model types themselves (<see cref="FindingsReport"/>, <see cref="GateReport"/> and
    /// the records they hold), so a list the model gains is covered without a name list to keep in step. A null in an
    /// unknown property is left alone: unknown properties are tolerated on read, and nothing binds that list.
    /// </remarks>
    /// <param name="node">The node to walk.</param>
    /// <param name="type">The model type the node binds to.</param>
    /// <param name="path">The node's JSON path, named in the refusal.</param>
    private static void RejectNullListEntries(JsonNode? node, Type type, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        switch (node)
        {
            case JsonArray array when ListElementType(type) is { } element:
                for (int index = 0; index < array.Count; index++)
                {
                    string entry = $"{path}[{index}]";
                    if (array[index] is null)
                        throw new JsonException($"The list at '{path}' has a null entry at '{entry}'.");

                    RejectNullListEntries(array[index], element, entry);
                }

                break;
            case JsonObject obj when IsModel(type):
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    // Web defaults read property names case-insensitively, so the match here does too.
                    PropertyInfo? bound = type.GetProperty(
                        property.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (bound is not null)
                        RejectNullListEntries(property.Value, bound.PropertyType, $"{path}.{property.Key}");
                }

                break;
        }
    }

    /// <summary>The element type when <paramref name="type"/> is a list (not a string or a dictionary), else null.</summary>
    private static Type? ListElementType(Type type)
    {
        if (type == typeof(string) || type.IsAssignableTo(typeof(System.Collections.IDictionary)))
            return null;

        if (type.IsArray)
            return type.GetElementType();

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is null)
            return null;

        Type element = enumerable.GetGenericArguments()[0];
        bool isDictionary = element.IsGenericType && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        return isDictionary ? null : element;
    }

    /// <summary>Whether <paramref name="type"/> is one of the review model records, whose properties are walked.</summary>
    private static bool IsModel(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(ReviewJson).Namespace;

    /// <summary>
    /// Holds a finding read from JSON to the ranges a seat's finding is held to. The serializer would bind a number as
    /// a severity (any integer becomes an enum value) and any integer as a confidence, so both are checked on the raw
    /// value: severity must be one of the severity names, confidence a whole number from 1 to 10.
    /// </summary>
    /// <remarks>
    /// Every property whose name matches ignoring case is checked, not only the first: the serializer reads names
    /// case-insensitively, so a second <c>"Severity": 7</c> beside a valid <c>"severity"</c> would otherwise be the
    /// value that binds.
    /// </remarks>
    private static void CheckRanges(JsonObject finding, string path)
    {
        foreach (KeyValuePair<string, JsonNode?> property in finding)
        {
            if (string.Equals(property.Key, "severity", StringComparison.OrdinalIgnoreCase))
            {
                bool named = property.Value is JsonValue text
                             && text.GetValueKind() == JsonValueKind.String
                             && Enum.GetNames<ReviewSeverity>().Any(name =>
                                 string.Equals(name, text.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
                if (!named)
                {
                    throw new JsonException(
                        $"'{path}.{property.Key}' is {Describe(property.Value)}; it must be \"critical\", \"major\" or " +
                        "\"minor\".");
                }
            }
            else if (string.Equals(property.Key, "confidence", StringComparison.OrdinalIgnoreCase))
            {
                bool inRange = property.Value is JsonValue number
                               && number.GetValueKind() == JsonValueKind.Number
                               && number.TryGetValue(out int value)
                               && value is >= 1 and <= 10;
                if (!inRange)
                {
                    throw new JsonException(
                        $"'{path}.{property.Key}' is {Describe(property.Value)}; it must be a whole number from 1 to 10.");
                }
            }
        }
    }

    private static string Describe(JsonNode? node)
    {
        const int limit = 60;
        string text = node?.ToJsonString() ?? "null";
        if (text.Length <= limit)
            return text;

        // Back off one character rather than split a surrogate pair at the cut.
        return text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)] + "…";
    }

    private static void BindSnakeCaseFailureScenario(JsonObject finding)
    {
        if (!finding.TryGetPropertyValue(SnakeCaseFailureScenario, out JsonNode? value))
            return;

        bool documentSpelling = finding.Any(property =>
            string.Equals(property.Key, nameof(ReviewFinding.FailureScenario), StringComparison.OrdinalIgnoreCase));
        finding.Remove(SnakeCaseFailureScenario);
        if (!documentSpelling)
            finding["failureScenario"] = value;
    }

    /// <summary>Reads a duplicates report.</summary>
    public static DuplicateReport ReadDuplicates(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<DuplicateReport>(json, Options)
               ?? throw new JsonException("The duplicates report is empty.");
    }
}
