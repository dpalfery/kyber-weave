using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// When output is not a terminal the findings are written one per line and never wrapped, so an agent can grep a rule id
/// and read the whole message and its fix on the same line. A terminal, and an explicit <c>--format</c>, keep the table.
/// </summary>
public sealed class FindingLinesRenderingTests
{
    private const string LongMessage =
        "'2026-11-13-job-status-cards.md' does not exist under the plans folder and no active plan has that name, " +
        "so there is nothing to archive and nothing was moved or changed in this repository.";

    private static DiagnosticReport Report()
    {
        DiagnosticReport report = new();
        report.Add(new Diagnostic(
            "KW-DOC-CLOSEOUT-001",
            Severity.Error,
            LongMessage,
            "2026-11-13-job-status-cards",
            "6-Docs/plans/2026-11-13-job-status-cards.md",
            "Name a plan file directly under the plans folder, not its index and not an archived plan."));
        return report;
    }

    private static string Render(OutputFormat format) =>
        ProcessConsoleCapture.Run(() =>
        {
            ReportRenderer.Render(Report(), format, "docs closeout", "Plan");
            return true;
        }).Output;

    [Fact]
    public void ALongMessageAndItsRuleIdAppearWholeOnOneLine()
    {
        string output = Render(OutputFormat.Lines);

        string line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("ERROR KW-DOC-CLOSEOUT-001 6-Docs/plans/2026-11-13-job-status-cards.md: ", line, StringComparison.Ordinal);
        Assert.Contains(LongMessage, line, StringComparison.Ordinal);
        Assert.EndsWith(" → Name a plan file directly under the plans folder, not its index and not an archived plan.", line.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAFormatARedirectedCommandWritesLinesAndTheSummary()
    {
        DocsCloseoutSettings settings = new() { Path = "no-such-plan", WorkingDirectory = Path.GetTempPath() };

        string output = ProcessConsoleCapture.Run(
            () => new DocsCloseoutCommand().Execute(null!, settings, CancellationToken.None)).Output;

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.StartsWith("ERROR KW-DOC-CLOSEOUT-001 ", StringComparison.Ordinal) && line.Contains("no-such-plan", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 error", StringComparison.Ordinal));
        Assert.DoesNotContain("│", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitTableFormatStillDrawsTheTable()
    {
        DocsCloseoutSettings settings = new() { Path = "no-such-plan", Format = "table", WorkingDirectory = Path.GetTempPath() };

        string output = ProcessConsoleCapture.Run(
            () => new DocsCloseoutCommand().Execute(null!, settings, CancellationToken.None)).Output;

        Assert.Contains("│", output, StringComparison.Ordinal);
    }

    [Fact]
    public void InformationalFindingsAreCountedNotListedWhenAnythingActionableExists()
    {
        DiagnosticReport report = Report();
        report.Add(new Diagnostic("KW-DOC-CLOSEOUT-004", Severity.Info, "A step.", "plan"));

        string output = ProcessConsoleCapture.Run(() =>
        {
            ReportRenderer.Render(report, OutputFormat.Lines, "docs closeout", "Plan");
            return true;
        }).Output;

        Assert.Contains("1 informational findings omitted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-DOC-CLOSEOUT-004", output, StringComparison.Ordinal);
    }
}
