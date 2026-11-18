using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;
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

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/work");

    public FindingLinesRenderingTests()
    {
        _paths = new FakePathResolver(_files);

        // A terminal wide enough that the table draws each cell on one line; a narrower one wraps the
        // rule id and the message, which is the wrapping the Lines format exists to avoid.
        _console.Profile.Width = 200;
    }

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

    private string Render(OutputFormat format)
    {
        ReportRenderer.Render(_console, Report(), format, "docs closeout", "Plan");
        return _console.Output;
    }

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
        DocsCloseoutSettings settings = new() { Path = "no-such-plan" };

        int exit = Closeout().Execute(null!, settings, CancellationToken.None);

        string[] lines = _console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1, exit);
        Assert.Contains(lines, line => line.StartsWith("ERROR KW-DOC-CLOSEOUT-001 ", StringComparison.Ordinal)
            && line.Contains("'no-such-plan' does not exist.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("1 error", StringComparison.Ordinal));
        Assert.DoesNotContain("│", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path that climbs out of the repository reaches the closeout as the path the resolver computed, not as the text
    /// the caller typed, so the refusal names a location outside the tree instead of hiding it.
    /// </summary>
    [Fact]
    public void APathThatClimbsOutOfTheRepositoryIsRefusedByName()
    {
        DocsCloseoutSettings settings = new() { Path = "../../etc/passwd" };

        int exit = Closeout().Execute(null!, settings, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-001", _console.Output, StringComparison.Ordinal);
        Assert.Contains("passwd", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("│", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitTableFormatStillDrawsTheTable()
    {
        DocsCloseoutSettings settings = new() { Path = "no-such-plan", Format = "table" };

        int exit = Closeout().Execute(null!, settings, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("│", _console.Output, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-CLOSEOUT-001", _console.Output, StringComparison.Ordinal);
        Assert.Contains("no-such-plan", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void InformationalFindingsAreCountedNotListedWhenAnythingActionableExists()
    {
        DiagnosticReport report = Report();
        report.Add(new Diagnostic("KW-DOC-CLOSEOUT-004", Severity.Info, "A step.", "plan"));

        ReportRenderer.Render(_console, report, OutputFormat.Lines, "docs closeout", "Plan");

        Assert.Contains("1 informational findings omitted", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-DOC-CLOSEOUT-004", _console.Output, StringComparison.Ordinal);
    }

    private DocsCloseoutCommand Closeout() => new(
        new PlanCloseoutService(
            new InMemoryRepositoryFileStore(_files),
            ThrowingProcess,
            new RefusingExclusiveWriter(),
            new ThrowingDocumentSourceFactory(),
            _paths,
            _environment),
        new InMemoryRepositoryFileStore(_files),
        _configs,
        new FakeTimeProvider(),
        _console,
        new PlanArguments(
            new ArbiterRepoRoot(_files, _paths, _environment),
            _files,
            _paths,
            _environment,
            _console));

    private static ScriptedProcessExecutor ThrowingProcess { get; } =
        new(_ => throw new InvalidOperationException("The closeout runs no program for a plan that does not exist."));

    /// <summary>Refuses to write, so a run that tried to archive would fail loudly instead of reaching disk.</summary>
    private sealed class RefusingExclusiveWriter : IExclusiveFileWriter
    {
        public bool TryWriteNew(string path, string text) =>
            throw new InvalidOperationException($"The closeout wrote '{path}' before the plan was checked.");

        public bool TryWriteNew(string path, string text, Action<string> deletePartial) =>
            throw new InvalidOperationException($"The closeout wrote '{path}' before the plan was checked.");
    }

    /// <summary>Refuses to load, so a run that reached the corpus would fail loudly.</summary>
    private sealed class ThrowingDocumentSourceFactory : IDocumentSourceFactory
    {
        public IDocumentSource Create(string repoRoot, KyberWeave.Core.Configuration.OntologyConfig ontology) =>
            throw new InvalidOperationException("The corpus is not loaded before the plan is checked.");
    }
}
