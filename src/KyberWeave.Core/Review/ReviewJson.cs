using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Review;

/// <summary>The gate suite's result document.</summary>
/// <param name="Schema">Format identifier, for readers that must tolerate later versions.</param>
/// <param name="Gates">One entry per declared gate, in declaration order.</param>
/// <param name="Coverage">Measured coverage, when a gate produced any.</param>
public sealed record GateReport(
    string Schema,
    IReadOnlyList<GateResult> Gates,
    CoverageResult? Coverage = null)
{
    /// <summary>The current gate report format.</summary>
    public const string CurrentSchema = "kyber-weave.review-gates/v1";
}

/// <summary>The council's findings document.</summary>
/// <param name="Schema">Format identifier.</param>
/// <param name="Findings">Findings surviving the council's own confirmation pass.</param>
/// <param name="ChangedPaths">Repository-relative paths the change touches.</param>
/// <param name="ChangedLines">Total added and removed lines.</param>
public sealed record FindingsReport(
    string Schema,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines)
{
    /// <summary>The current findings format.</summary>
    public const string CurrentSchema = "kyber-weave.review-findings/v1";

    /// <summary>The scope this report describes.</summary>
    public ReviewScope ToScope() => new(ChangedPaths, ChangedLines);
}

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
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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

    /// <summary>Reads a gate report.</summary>
    public static GateReport ReadGates(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<GateReport>(json, Options)
            ?? throw new JsonException("The gate report is empty.");
    }

    /// <summary>Reads a findings report.</summary>
    public static FindingsReport ReadFindings(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<FindingsReport>(json, Options)
            ?? throw new JsonException("The findings report is empty.");
    }
}
