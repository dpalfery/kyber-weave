using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Renders the operator report in <c>json</c>, <c>markdown</c>, or <c>text</c> form
/// (<c>kyberdash report</c>).
/// </summary>
/// <remarks>
/// The three formats share one payload: the dashboard report projection plus the harness rollups and
/// findings behind it. <c>json</c> prints that payload verbatim so a script can parse it; the other
/// two render the same numbers in a fixed column order and never invent a figure the store did not
/// report, printing <c>absent</c> instead of a misleading zero.
/// </remarks>
public class ReportService : IReportService
{
    /// <summary>An empty harness comparison row used when the store has no rollups.</summary>
    public const string AbsentCell = "absent";

    private readonly IDashboardQueryService query;
    private readonly IProjectionStore projections;
    private readonly IRelationalStoreReader reader;

    /// <summary>
    /// Initializes a new <see cref="ReportService"/>.
    /// </summary>
    /// <param name="query">Query service producing the report projection.</param>
    /// <param name="projections">Projection store supplying harness rollups and findings.</param>
    /// <param name="reader">Relational reader backing report-scope reads.</param>
    public ReportService(IDashboardQueryService query, IProjectionStore projections, IRelationalStoreReader reader)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(reader);

        this.query = query;
        this.projections = projections;
        this.reader = reader;
    }

    /// <inheritdoc />
    public (int ExitCode, string Output) GenerateReport(
        string format,
        string? harness = null,
        string? sessionId = null,
        string? runId = null,
        int limit = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        int boundedLimit = Math.Clamp(limit, 1, 200);
        JsonElement report = query.GetReport(harness, sessionId, runId, boundedLimit);
        IReadOnlyList<StoredHarnessRollup> rollups = projections.ListHarnessRollups();
        IReadOnlyList<StoredFinding> findings = projections.ListFindings(runId, sessionId);

        return format.Trim().ToUpperInvariant() switch
        {
            "JSON" => (0, RenderJson(report)),
            "MARKDOWN" => (0, RenderMarkdown(report, rollups, findings, boundedLimit)),
            "TEXT" => (0, RenderText(report, rollups, findings, boundedLimit)),
            _ => (2, string.Create(
                CultureInfo.InvariantCulture,
                $"report: unknown format \"{format}\". Expected one of: json, markdown, text.")),
        };
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Formats the report payload as indented JSON.</summary>
    /// <param name="report">Dashboard report projection.</param>
    /// <returns>Indented JSON text.</returns>
    public static string RenderJson(JsonElement report) =>
        JsonSerializer.Serialize(report, IndentedJson);

    /// <summary>Renders the report payload as GitHub-flavoured Markdown.</summary>
    /// <param name="report">Dashboard report projection.</param>
    /// <param name="rollups">Persisted harness rollups.</param>
    /// <param name="findings">Findings included in the report scope.</param>
    /// <param name="limit">Maximum findings listed.</param>
    /// <returns>Markdown text ending in a newline.</returns>
    public static string RenderMarkdown(
        JsonElement report,
        IReadOnlyList<StoredHarnessRollup> rollups,
        IReadOnlyList<StoredFinding> findings,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(findings);

        StringBuilder text = new();
        text.AppendLine("# KyberDash report");
        text.AppendLine();

        foreach ((string Label, string Value) in SummaryCells(report))
        {
            text.Append("- **").Append(Label).Append(":** ").Append(Value).AppendLine();
        }

        text.AppendLine();
        text.AppendLine("## Harnesses");
        text.AppendLine();
        if (rollups.Count == 0)
        {
            text.AppendLine("_No harness rollups recorded._");
        }
        else
        {
            text.AppendLine("| Harness | Sessions | Context p50 | Context p95 | Cache hit |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (StoredHarnessRollup rollup in rollups.OrderBy(static row => row.HarnessId, StringComparer.Ordinal))
            {
                text.Append("| ").Append(rollup.HarnessId)
                    .Append(" | ").Append(rollup.SessionCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(FormatPercent(MetricValue(rollup, "context_pressure_median")))
                    .Append(" | ").Append(FormatPercent(MetricValue(rollup, "context_pressure_p95")))
                    .Append(" | ").Append(FormatPercent(MetricValue(rollup, "cache_hit_rate")))
                    .AppendLine(" |");
            }
        }

        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        if (findings.Count == 0)
        {
            text.AppendLine("_No findings recorded._");
        }
        else
        {
            foreach (StoredFinding finding in findings.Take(limit))
            {
                text.Append("- **").Append(finding.DetectorId).Append("** (").Append(finding.Confidence).Append("): ")
                    .Append(finding.Title).AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>Renders the report payload as fixed-width plain text.</summary>
    /// <param name="report">Dashboard report projection.</param>
    /// <param name="rollups">Persisted harness rollups.</param>
    /// <param name="findings">Findings included in the report scope.</param>
    /// <param name="limit">Maximum findings listed.</param>
    /// <returns>Plain text ending in a newline.</returns>
    public static string RenderText(
        JsonElement report,
        IReadOnlyList<StoredHarnessRollup> rollups,
        IReadOnlyList<StoredFinding> findings,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(findings);

        StringBuilder text = new();
        text.AppendLine("KyberDash report");
        foreach ((string Label, string Value) in SummaryCells(report))
        {
            text.Append("  ").Append(Label).Append(": ").Append(Value).AppendLine();
        }

        text.AppendLine();
        text.AppendLine("Harnesses");
        if (rollups.Count == 0)
        {
            text.AppendLine("  (none recorded)");
        }
        else
        {
            foreach (StoredHarnessRollup rollup in rollups.OrderBy(static row => row.HarnessId, StringComparer.Ordinal))
            {
                text.Append("  ").Append(rollup.HarnessId)
                    .Append("  sessions=").Append(rollup.SessionCount.ToString(CultureInfo.InvariantCulture))
                    .Append("  context_p50=").Append(FormatPercent(MetricValue(rollup, "context_pressure_median")))
                    .Append("  context_p95=").Append(FormatPercent(MetricValue(rollup, "context_pressure_p95")))
                    .Append("  cache_hit=").Append(FormatPercent(MetricValue(rollup, "cache_hit_rate")))
                    .AppendLine();
            }
        }

        text.AppendLine();
        text.AppendLine("Findings");
        if (findings.Count == 0)
        {
            text.AppendLine("  (none recorded)");
        }
        else
        {
            foreach (StoredFinding finding in findings.Take(limit))
            {
                text.Append("  ").Append(finding.DetectorId).Append(' ')
                    .Append(finding.Confidence).Append("  ").Append(finding.Title).AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>Reads the summary numbers straight out of the report projection.</summary>
    private static IEnumerable<(string Label, string Value)> SummaryCells(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return ("schema", ReadNumber(report, "schemaVersion"));
        yield return ("sessions", ReadNumber(report, "sessionCount"));
        yield return ("findings", ReadNumber(report, "findingCount"));
        yield return ("harnesses", ReadNumber(report, "harnessCount"));
        yield return ("generated", ReadString(report, "generatedAt"));
    }

    private static string ReadNumber(JsonElement report, string property) =>
        report.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : AbsentCell;

    private static string ReadString(JsonElement report, string property) =>
        report.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? AbsentCell
            : AbsentCell;

    /// <summary>Reads one metric off a persisted harness rollup, or <see langword="null"/> when absent.</summary>
    /// <param name="rollup">Persisted harness rollup.</param>
    /// <param name="metric">Metric identifier, for example <c>context_pressure_median</c>.</param>
    /// <returns>The metric value, or <see langword="null"/> when the rollup did not record it.</returns>
    public static double? MetricValue(StoredHarnessRollup rollup, string metric)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        return CanonPipelineShared.RollupMetricValue(rollup, metric);
    }

    /// <summary>Formats a ratio as a whole percentage, or <c>absent</c> when the source did not measure it.</summary>
    /// <param name="value">Ratio in <c>0..1</c>, or <see langword="null"/>.</param>
    /// <returns>Percentage text or <see cref="AbsentCell"/>.</returns>
    public static string FormatPercent(double? value) =>
        value.HasValue
            ? (value.Value * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : AbsentCell;
}
