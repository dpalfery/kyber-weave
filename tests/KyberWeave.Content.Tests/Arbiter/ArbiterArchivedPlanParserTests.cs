using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Kyber Arbiter plan parser (task 1.3) against the archived plans and the archived spec task file, read in
/// place from the repository: the parser must survive every label variant the archive actually contains, not only
/// the ones a hand-written fixture remembers.
/// </summary>
public sealed class ArbiterArchivedPlanParserTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ArchivePlansRoot = "docs/archive/plans";

    private const string ArchivedSpecTasksPath = "docs/archive/specs/kyberdash-context-surfaces/tasks.md";

    private static PlanDocument ParseRepositoryFile(params string[] parts)
    {
        return PlanDocumentParser.Parse(Corpus.ReadText(string.Join('/', parts)));
    }

    private static PlanTask Task(PlanDocument plan, string id)
    {
        PlanTask? match = plan.Tasks.FirstOrDefault(task => task.Id == id);
        Assert.True(match is not null, $"expected a task '{id}' in the parsed plan; found: [{string.Join(", ", plan.Tasks.Select(task => task.Id))}]");
        return match!;
    }

    private static string[] DiagnosticCodes(PlanDocument plan, string code)
    {
        return [.. plan.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Subject)];
    }

    [Fact]
    public void ArchivedPlansWithoutTasksSectionReportHasTasksFalse()
    {
        string[][] plansWithoutTasks =
        [
            ["docs", "archive", "plans", "2026-09-29-pr-158-defects.md"],
            ["docs", "archive", "plans", "2026-09-29-glib-variant-str-iter-backport.md"],
        ];
        foreach (string[] plan in plansWithoutTasks)
        {
            PlanDocument parsed = ParseRepositoryFile(plan);
            Assert.False(parsed.HasTasks, $"{plan[^1]} unexpectedly reports a Tasks section");
            Assert.Empty(parsed.Tasks);
            Assert.Empty(DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
            Assert.Equal("test-first", parsed.DevelopmentMode);
        }

        PlanDocument frontmatterMode = ParseRepositoryFile("docs", "archive", "plans", "2026-09-29-pr-158-defects.md");
        Assert.Equal("archived", frontmatterMode.Status);
    }

    [Fact]
    public void EveryArchivedPlanMarkdownParsesWithoutThrowing()
    {
        List<string> plans = [.. Corpus.EnumerateFiles(ArchivePlansRoot, "*.md", recursive: false)
            .OrderBy(path => path, StringComparer.Ordinal)];

        Assert.True(plans.Count >= 8, $"expected the archived plan corpus to hold at least 8 files, found {plans.Count}");

        foreach (string path in plans)
        {
            PlanDocument parsed = PlanDocumentParser.Parse(Corpus.ReadText(path));

            Assert.NotNull(parsed.Tasks);
            Assert.NotNull(parsed.OutOfScope);
            Assert.NotNull(parsed.ContractRows);
            Assert.NotNull(parsed.Diagnostics);
        }
    }

    [Fact]
    public void ArchivedPlanWithColonTaskHeadingsParsesIdsSkillsAndDependencies()
    {
        PlanDocument parsed = ParseRepositoryFile(
            "docs", "archive", "plans", "2026-09-29-mcp-docs-corpus-provenance.md");

        Assert.True(parsed.HasTasks);
        Assert.Equal("test-first", parsed.DevelopmentMode);

        PlanTask red = Task(parsed, "T1-RED-provenance-headers");
        Assert.Equal("Two-root provenance contract (test-dev)", red.Title);
        Assert.Contains("test-dev", red.Skills);

        PlanTask green = Task(parsed, "T5-GREEN-docs-status");
        Assert.Contains("T2-RED-docs-status-packaging", green.DependsOn);
        Assert.Contains("T4-GREEN-provenance-core", green.DependsOn);

        PlanTask core = Task(parsed, "T4-GREEN-provenance-core");
        Assert.Contains("src/KyberWeave.Mcp/**", core.Files);
        Assert.Contains("src/KyberWeave.Mcp/DocsTools.cs", core.Files);
    }

    [Fact]
    public void ArchivedPlanWithEmDashHeadingsParsesSubIdsAndContractRows()
    {
        PlanDocument parsed = ParseRepositoryFile(
            "docs", "archive", "plans", "2026-09-28-kyberdash-sea-release-integrity.md");

        Assert.True(parsed.HasTasks);
        Assert.Equal("test-first", parsed.DevelopmentMode);

        PlanTask red = Task(parsed, "T1");
        Assert.Equal("RED: absolute CLI path from a SEA", red.Title);
        Assert.Contains("dash/src/install/node-deps.test.ts", red.Files);

        PlanTask bundling = Task(parsed, "T9b");
        Assert.Contains("T2", bundling.DependsOn);
        Assert.Contains("T10", bundling.DependsOn);

        PlanTask loop = Task(parsed, "T10");
        Assert.Contains("T2", loop.DependsOn);
        Assert.Contains("T4", loop.DependsOn);
        Assert.Contains("T8", loop.DependsOn);
        Assert.Contains("T9", loop.DependsOn);
        Assert.Contains("T9b", loop.DependsOn);

        string[] rowIds = [.. parsed.ContractRows.Select(row => row.TaskId)];
        Assert.Contains("T1", rowIds);
        Assert.Contains("T9b", rowIds);
        Assert.DoesNotContain("Post-merge", rowIds);
        PlanContractRow contract = Assert.Single(parsed.ContractRows, candidate => candidate.TaskId == "T1");
        Assert.Contains("vitest run src/install/node-deps.test.ts", contract.RunnerFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivedPlanWithFilesSymbolsVariantParsesFilesAndDependencies()
    {
        PlanDocument parsed = ParseRepositoryFile(
            "docs", "archive", "plans", "2026-09-28-kyber-utilities-status-line-slice.md");

        Assert.True(parsed.HasTasks);
        Assert.Equal("test-first", parsed.DevelopmentMode);

        PlanTask docs = Task(parsed, "T1");
        Assert.Contains("T10", docs.DependsOn);
        Assert.Contains("app-docs-standard", docs.Skills);

        PlanTask red = Task(parsed, "T2");
        Assert.Contains("tests/KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs", red.Files);

        PlanTask green = Task(parsed, "T3");
        Assert.Contains("src/KyberWeave.Core/Utilities/StatusLine/*.cs", green.Files);
        Assert.Contains("T2", green.DependsOn);
    }

    [Fact]
    public void ArchivedStandardModePlanParsesModeAndVerificationContract()
    {
        PlanDocument parsed = ParseRepositoryFile(
            "docs", "archive", "plans", "2026-09-29-release-checksums-unsigned-windows.md");

        Assert.True(parsed.HasTasks);
        Assert.Equal("standard", parsed.DevelopmentMode);

        string[] rowIds = [.. parsed.ContractRows.Select(row => row.TaskId)];
        Assert.Equal(12, rowIds.Length);
        Assert.Contains("T4", rowIds);
        Assert.DoesNotContain("Post-merge", rowIds);

        PlanContractRow first = Assert.Single(parsed.ContractRows, row => row.TaskId == "T1");
        Assert.StartsWith("tests/KyberWeave.Tests/ReleaseTests.cs", first.TestProjectOrFile, StringComparison.Ordinal);
        Assert.Null(first.RedEvidenceRequired);
    }

    [Fact]
    public void ArchivedSpecTaskArtifactParsesCheckboxTasksWithInheritedPaths()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(Corpus.ReadText(ArchivedSpecTasksPath));

        Assert.True(parsed.HasTasks);
        Assert.Equal("archived", parsed.Status);
        Assert.Equal("test-first", parsed.DevelopmentMode);

        PlanTask retire = Task(parsed, "1.1");
        Assert.True(retire.Checked);
        Assert.Equal("Retire the merge-boundary rules and vendored-path exclusions", retire.Title);
        Assert.Contains("tests/KyberWeave.Tests/MergeBoundaryTests.cs", retire.Files);
        Assert.Contains("dash/kyber/tools/cost-isolation.ts", retire.Files);

        PlanTask parent = Task(parsed, "1");
        Assert.Empty(parent.Files);
        Assert.Contains("1", DiagnosticCodes(parsed, PlanDocumentParser.MissingFilesRule));
    }
}
