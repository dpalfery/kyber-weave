using System.Text.Json;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

public sealed class DiagnosticLocationRenderingTests
{
    [Fact]
    public void Diagnostic_WithOptionalRangesAndRelatedLocations_PreservesExistingConstructors()
    {
        var legacy = new Diagnostic(
            "KW-LEGACY-001",
            Severity.Info,
            "Legacy diagnostic",
            "legacy-subject",
            "docs/legacy.md",
            "Legacy hint");
        var related = new DiagnosticLocation(
            "docs/related.md",
            StartLine: 21,
            EndLine: 23,
            Message: "Matching claim");

        var ranged = new Diagnostic(
            "KW-DOC-ANALYSIS-001",
            Severity.Warning,
            "Duplicate claim",
            "claim-a",
            "docs/primary.md",
            "Choose one canonical claim",
            StartLine: 10,
            EndLine: 12,
            RelatedLocations: [related]);

        Assert.Equal("docs/legacy.md", legacy.FilePath);
        Assert.Equal("Legacy hint", legacy.Hint);
        Assert.Equal(10, ranged.StartLine);
        Assert.Equal(12, ranged.EndLine);
        Assert.Equal(related, Assert.Single(ranged.RelatedLocations));
    }

    [Fact]
    public void DiagnosticReport_WithScalarMetrics_PreservesInsertionOrderAndValues()
    {
        var report = new DiagnosticReport();

        report.AddMetric("extractedClaims", 42);
        report.AddMetric("candidateRatio", 0.25);
        report.AddMetric("truncated", false);
        report.AddMetric("searchMode", "hybrid");

        Assert.Equal(
            ["extractedClaims", "candidateRatio", "truncated", "searchMode"],
            report.Metrics.Select(metric => metric.Key));
        Assert.Equal(42, report.Metrics["extractedClaims"]);
        Assert.Equal(0.25, report.Metrics["candidateRatio"]);
        Assert.Equal(false, report.Metrics["truncated"]);
        Assert.Equal("hybrid", report.Metrics["searchMode"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DiagnosticReport_WithNonFiniteFloatingPointMetric_RejectsAtAddMetric(object value)
    {
        var report = new DiagnosticReport();

        var exception = Assert.Throws<ArgumentException>(() => report.AddMetric("score", value));

        Assert.Equal("value", exception.ParamName);
        Assert.Empty(report.Metrics);
    }

    [Fact]
    public void Render_WithFiniteFloatingPointMetrics_WritesJsonAndSarifNumbers()
    {
        var report = new DiagnosticReport();
        report.AddMetric("floatScore", 0.5f);
        report.AddMetric("doubleScore", 0.25d);

        using var json = JsonDocument.Parse(Render(OutputFormat.Json, report));
        using var sarif = JsonDocument.Parse(Render(OutputFormat.Sarif, report));

        var jsonMetrics = json.RootElement.GetProperty("metrics");
        var sarifMetrics = sarif.RootElement
            .GetProperty("runs")[0]
            .GetProperty("properties")
            .GetProperty("metrics");
        Assert.Equal(0.5, jsonMetrics.GetProperty("floatScore").GetDouble());
        Assert.Equal(0.25, jsonMetrics.GetProperty("doubleScore").GetDouble());
        Assert.Equal(0.5, sarifMetrics.GetProperty("floatScore").GetDouble());
        Assert.Equal(0.25, sarifMetrics.GetProperty("doubleScore").GetDouble());
    }

    [Fact]
    public void Render_Json_IncludesRangesRelatedLocationsAndOrderedMetrics()
    {
        using var document = JsonDocument.Parse(Render(OutputFormat.Json));
        var root = document.RootElement;
        var finding = root.GetProperty("findings")[0];
        var related = finding.GetProperty("relatedLocations")[0];

        Assert.Equal(10, finding.GetProperty("startLine").GetInt32());
        Assert.Equal(12, finding.GetProperty("endLine").GetInt32());
        Assert.Equal("docs/related.md", related.GetProperty("file").GetString());
        Assert.Equal(21, related.GetProperty("startLine").GetInt32());
        Assert.Equal(23, related.GetProperty("endLine").GetInt32());
        Assert.Equal("Matching claim", related.GetProperty("message").GetString());
        Assert.Equal(
            ["extractedClaims", "truncated"],
            root.GetProperty("metrics").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Render_Table_UsesCompactPrimaryLocationAndReportsMetrics()
    {
        var output = Render(OutputFormat.Table);

        Assert.Contains("docs/primary.md:10-12 (+1 related)", output, StringComparison.Ordinal);
        Assert.Contains("Warning", output, StringComparison.Ordinal);
        Assert.Contains("claim-a", output, StringComparison.Ordinal);
        AssertMetricsRenderedInOrder(output);
    }

    [Fact]
    public void Render_Table_OmitsInfoRowsWhenWarningsExistSoTheyStayVisible()
    {
        var report = CreateReport();
        report.Add(new Diagnostic(
            "KW-DOC-ANALYSIS-002",
            Severity.Info,
            "These related documentation claims contain potentially incompatible values or obligations.",
            "claim-b",
            "docs/other.md",
            StartLine: 4));

        var table = Render(OutputFormat.Table, report);
        var json = Render(OutputFormat.Json, report);

        Assert.Contains("1 informational findings omitted from the table", table, StringComparison.Ordinal);
        Assert.Contains("Warning", table, StringComparison.Ordinal);
        Assert.Contains("claim-a", table, StringComparison.Ordinal);
        Assert.DoesNotContain("claim-b", table, StringComparison.Ordinal);
        Assert.Contains("claim-b", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Table_RelativizesAbsolutePathsUnderTheCurrentDirectory()
    {
        var absolute = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", "primary.md"));
        var report = new DiagnosticReport();
        report.Add(new Diagnostic(
            "KW-DOC-ANALYSIS-001",
            Severity.Warning,
            "Duplicate claim",
            "claim-a",
            absolute,
            StartLine: 10,
            EndLine: 12));

        var output = Render(OutputFormat.Table, report);

        Assert.Contains("docs/primary.md:10-12", output, StringComparison.Ordinal);
        Assert.DoesNotContain(absolute, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Markdown_IncludesRangesRelatedLocationsAndMetrics()
    {
        var output = Render(OutputFormat.Markdown);

        Assert.Contains("docs/primary.md:10-12", output, StringComparison.Ordinal);
        Assert.Contains("docs/related.md:21-23", output, StringComparison.Ordinal);
        Assert.Contains("Matching claim", output, StringComparison.Ordinal);
        AssertMetricsRenderedInOrder(output);
    }

    [Fact]
    public void Render_Sarif_UsesRegionsRelatedLocationsAndRunMetrics()
    {
        using var document = JsonDocument.Parse(Render(OutputFormat.Sarif));
        var run = document.RootElement.GetProperty("runs")[0];
        var result = run.GetProperty("results")[0];
        var primaryRegion = result
            .GetProperty("locations")[0]
            .GetProperty("physicalLocation")
            .GetProperty("region");
        var related = result.GetProperty("relatedLocations")[0];
        var relatedRegion = related
            .GetProperty("physicalLocation")
            .GetProperty("region");

        Assert.Equal(10, primaryRegion.GetProperty("startLine").GetInt32());
        Assert.Equal(12, primaryRegion.GetProperty("endLine").GetInt32());
        Assert.Equal(
            "docs/related.md",
            related.GetProperty("physicalLocation")
                .GetProperty("artifactLocation")
                .GetProperty("uri")
                .GetString());
        Assert.Equal(21, relatedRegion.GetProperty("startLine").GetInt32());
        Assert.Equal(23, relatedRegion.GetProperty("endLine").GetInt32());
        Assert.Equal("Matching claim", related.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(
            ["extractedClaims", "truncated"],
            run.GetProperty("properties")
                .GetProperty("metrics")
                .EnumerateObject()
                .Select(property => property.Name));
    }

    private static DiagnosticReport CreateReport()
    {
        var report = new DiagnosticReport();
        report.Add(new Diagnostic(
            "KW-DOC-ANALYSIS-001",
            Severity.Warning,
            "Duplicate claim",
            "claim-a",
            "docs/primary.md",
            "Choose one canonical claim",
            StartLine: 10,
            EndLine: 12,
            RelatedLocations:
            [
                new DiagnosticLocation(
                    "docs/related.md",
                    StartLine: 21,
                    EndLine: 23,
                    Message: "Matching claim")
            ]));
        report.AddMetric("extractedClaims", 17);
        report.AddMetric("truncated", false);
        return report;
    }

    private static string Render(OutputFormat format, DiagnosticReport? report = null)
    {
        var execution = ProcessConsoleCapture.Run(() =>
        {
            ReportRenderer.Render(report ?? CreateReport(), format, "docs integrity-check", "Claim");
            return true;
        });
        return execution.Output;
    }

    private static void AssertMetricsRenderedInOrder(string output)
    {
        var claimsIndex = output.IndexOf("extractedClaims", StringComparison.Ordinal);
        var truncatedIndex = output.IndexOf("truncated", StringComparison.Ordinal);

        Assert.True(claimsIndex >= 0, $"Expected extractedClaims metric in output:{Environment.NewLine}{output}");
        Assert.True(truncatedIndex > claimsIndex, $"Expected ordered metrics in output:{Environment.NewLine}{output}");
        Assert.Contains("17", output, StringComparison.Ordinal);
        Assert.Contains("false", output, StringComparison.OrdinalIgnoreCase);
    }
}
