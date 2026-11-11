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
        return JsonSerializer.Deserialize<GateReport>(json, Options)
               ?? throw new JsonException("The gate report is empty.");
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
        if (root is JsonObject document && document["findings"] is JsonArray findings)
        {
            foreach (JsonObject finding in findings.OfType<JsonObject>())
                BindSnakeCaseFailureScenario(finding);
        }

        return root.Deserialize<FindingsReport>(Options)
               ?? throw new JsonException("The findings report is empty.");
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
