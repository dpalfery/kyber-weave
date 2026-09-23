using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The todo index is how a reader learns which deferred work is open, so a todo left in the
/// active folder without being listed is live to retrieval and invisible to that reader.
/// </summary>
public sealed class TodoInventoryValidatorTests
{
    private const string TodoIndex = "6-Docs/todo/README.md";

    private static string Index(string body, string id = "todo/index") =>
        $"""
        ---
        id: {id}
        title: Todo index
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-09-13
        ---

        # Todo index

        {body}
        """;

    private static string Todo(string id, string body = "") =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: todo
        status: needs-review
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-09-13
        ---

        # {id}

        {body}
        """;

    private static DiagnosticReport Validate(DocFixture fixture) =>
        new TodoInventoryValidator(KyberWeaveConfig.ProductDefaults).Validate(fixture.Load());

    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(i => $"{i.Code} {i.FilePath}"));

    /// <summary>
    /// The regression: a todo sits in the active todo folder while the inventory says "None",
    /// so the work is deferred but nothing surfaces it. The index must account for it or the
    /// document must not be there.
    /// </summary>
    [Fact]
    public void ATodoLeftInTheTodoFolderWhenItsIndexListsNoneIsKWDOCLIFECYCLE002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(TodoIndex, Index("None."))
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));

        DiagnosticReport report = Validate(fixture);

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(TodoInventoryValidator.UnlistedTodo, finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("6-Docs/todo/spike-cleanup.md", finding.FilePath);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void ATodoItsIndexLinksIsListed()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(TodoIndex, Index("- [Spike: cleanup](spike-cleanup.md)"))
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    [Fact]
    public void TheTodoIndexItselfIsNeverUnlisted()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(TodoIndex, Index("None."));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    [Fact]
    public void ATodoUnderTheArchiveSegmentIsNotUnlisted()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(TodoIndex, Index("None."))
            .Write("6-Docs/archive/todo/closed-spike.md", Todo("todo/closed-spike"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    [Fact]
    public void ACorpusWithoutATodoIndexIsSilent()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));

        DiagnosticReport report = Validate(fixture);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>
    /// The validator only protects anything if <c>docs validate</c> actually runs it, so the
    /// wiring is pinned end to end: an unindexed todo fails the command and names the rule.
    /// </summary>
    [Fact]
    public void DocsValidateReportsAnUnlistedTodoUnderKWDOCLIFECYCLE002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write(TodoIndex, Index("None."))
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));
        DocsSettings settings = new DocsSettings { Path = fixture.Root, Format = "json" };

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));

        Assert.Contains(TodoInventoryValidator.UnlistedTodo, execution.Output, StringComparison.Ordinal);
        Assert.Equal(1, execution.Result);
    }
}
