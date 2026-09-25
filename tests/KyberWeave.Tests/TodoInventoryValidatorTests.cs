using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
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

    /// <summary>
    /// A host that wants archive content retrievable drops the archive segment from the
    /// ontology exclusions, so the corpus loads documents under 6-Docs/archive/.
    /// </summary>
    private static readonly OntologyConfig ArchiveInCorpus =
        OntologyConfig.ProductDefaults.Clone(excludedPathSegments: ["node_modules", "obj", "bin"]);

    /// <summary>
    /// The same host overrides todo-index to the repository-root index, so the folder the
    /// validator derives spans the whole docs root, archive subtree included.
    /// </summary>
    private static readonly KyberWeaveConfig HostOverrides = KyberWeaveConfig.ProductDefaults
        .WithConfigReg(new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry(ConfigRegConfig.TodoIndexProperty, "6-Docs/README.md")]
        })
        .WithOntology(ArchiveInCorpus);

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

    /// <summary>
    /// A host can lift the loader's archive exclusion and override todo-index to an index
    /// above the archive subtree; the folder the validator derives then spans the archive.
    /// Closed work must still not fail the inventory as unlisted live work.
    /// </summary>
    [Fact]
    public void ATodoUnderTheArchiveSegmentIsNotUnlistedWhenTheIndexSitsAboveTheArchiveSubtree()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/README.md", Index("None.", "docs/index"))
            .Write("6-Docs/archive/todo/closed-spike.md", Todo("todo/closed-spike"));

        DocumentSet set = new DocumentLoader(fixture.Root, ArchiveInCorpus).Load();
        DiagnosticReport report = new TodoInventoryValidator(HostOverrides).Validate(set);

        Assert.True(report.Items.Count == 0, Describe(report));
    }

    /// <summary>
    /// Listed means reachable from the index through live documents. An archived document
    /// is closed work, so when the index links into it, its links out do not list the
    /// active todos they point at — otherwise a closed document could vouch live work into
    /// the inventory.
    /// </summary>
    [Fact]
    public void AnActiveTodoLinkedOnlyFromAnArchivedDocumentIsStillUnlisted()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/README.md", Index("- [Closed spike](archive/todo/closed-spike.md)", "docs/index"))
            .Write(
                "6-Docs/archive/todo/closed-spike.md",
                Todo("todo/closed-spike", "- [Spike: cleanup](../../todo/spike-cleanup.md)"))
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));

        DocumentSet set = new DocumentLoader(fixture.Root, ArchiveInCorpus).Load();
        DiagnosticReport report = new TodoInventoryValidator(HostOverrides).Validate(set);

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(TodoInventoryValidator.UnlistedTodo, finding.Code);
        Assert.Equal("6-Docs/todo/spike-cleanup.md", finding.FilePath);
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
        DocsValidateSettings settings = new DocsValidateSettings { Path = fixture.Root, Format = "json" };

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));

        Assert.Contains(TodoInventoryValidator.UnlistedTodo, execution.Output, StringComparison.Ordinal);
        Assert.Equal(1, execution.Result);
    }
}
