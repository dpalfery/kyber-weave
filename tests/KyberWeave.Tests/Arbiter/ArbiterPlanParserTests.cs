using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for the Kyber Arbiter plan parser (task 1.3). The conformance set is
/// read in place from the repository — the archived plans are parsed where they live and
/// are never copied — because the parser must survive every label variant the archive
/// actually contains, not only the ones a hand-written fixture remembers.
/// </summary>
public sealed class ArbiterPlanParserTests
{
    private const string FixtureRootRelativePath = "tests/KyberWeave.Tests/Fixtures/arbiter-plans";

    private static readonly string ArchivePlansRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "archive", "plans");

    private static readonly string ArchivedSpecTasksPath = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, "docs", "archive", "specs", "kyberdash-context-surfaces", "tasks.md");

    private static PlanDocument ParseRepositoryFile(params string[] parts)
    {
        return PlanDocumentParser.Parse(File.ReadAllText(Path.Combine([KyberWeaveTestPaths.ToolRoot, .. parts])));
    }

    private static PlanDocument ParseFixture(string name)
    {
        return PlanDocumentParser.Parse(
            File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, FixtureRootRelativePath, name)));
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
        List<string> plans = [.. Directory.EnumerateFiles(ArchivePlansRoot, "*.md")
            .OrderBy(path => path, StringComparer.Ordinal)];

        Assert.True(plans.Count >= 8, $"expected the archived plan corpus to hold at least 8 files, found {plans.Count}");

        foreach (string path in plans)
        {
            PlanDocument parsed = PlanDocumentParser.Parse(File.ReadAllText(path));

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
        PlanDocument parsed = PlanDocumentParser.Parse(File.ReadAllText(ArchivedSpecTasksPath));

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

    [Fact]
    public void LabelFamilyVariantsAcrossBothGrammarsAreParsed()
    {
        PlanDocument parsed = ParseFixture("plan-label-variants.md");

        PlanTask bold = Task(parsed, "T1");
        Assert.Equal(
            ["src/KyberWeave.Core/Arbiter/Plans/PlanDocument.cs", "src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs"],
            bold.Files);
        Assert.Equal(["test-dev"], bold.Skills);
        Assert.Empty(bold.DependsOn);

        PlanTask slash = Task(parsed, "T2");
        Assert.Equal(["tests/KyberWeave.Tests/Alpha.cs", "tests/KyberWeave.Tests/Beta.cs"], slash.Files);
        Assert.Equal(["T1"], slash.DependsOn);

        PlanTask owned = Task(parsed, "T3");
        Assert.Equal(["docs/plans/**", "*/settings.yml"], owned.Files);

        PlanTask scope = Task(parsed, "T4");
        Assert.Equal(["templates/*/README.md"], scope.Files);
        Assert.Equal(["T1", "T2"], scope.DependsOn);

        PlanTask suffix = Task(parsed, "T5");
        Assert.Equal(["README.md"], suffix.Files);
        Assert.Equal(["github-cli", "github-devops"], suffix.Skills);

        PlanTask lowercase = Task(parsed, "T6");
        Assert.Equal(["products/README.md"], lowercase.Files);
        Assert.Equal(["test-dev"], lowercase.Skills);

        Assert.Equal("test-first", parsed.DevelopmentMode);
    }

    [Fact]
    public void BothTitleSeparatorsAndIdFormsAreParsed()
    {
        PlanDocument parsed = ParseFixture("plan-title-separators.md");

        Assert.Equal("RED, colon separator contract", Task(parsed, "T1").Title);
        Assert.Equal("Establish the failing em-dash contract", Task(parsed, "T2").Title);
        Assert.Equal("sub and suffix forms", Task(parsed, "T3a-sub-suffix-id").Title);
        Assert.Equal("two digit number", Task(parsed, "T14").Title);
        Assert.Equal("standard", parsed.DevelopmentMode);

        // T14 depends on T99, which names no task in the document.
        Assert.Equal(["T14"], DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
    }

    [Fact]
    public void ContractTableRowsKeepCellsByHeaderName()
    {
        PlanDocument parsed = ParseFixture("plan-contract-and-out-of-scope.md");

        Assert.Equal(2, parsed.ContractRows.Count);
        PlanContractRow first = parsed.ContractRows[0];
        Assert.Equal("T1", first.TaskId);
        Assert.Equal("tests/One.cs", first.TestProjectOrFile);
        Assert.Equal("dotnet test --filter One", first.RunnerFilter);
        Assert.Equal("One behaves.", first.ObservableBehaviour);
        Assert.Equal("Fails before One exists.", first.RedEvidenceRequired);
        Assert.Equal("One passes.", first.GreenAcceptance);

        Assert.Equal(["docs/generated/**", "artifacts/**"], parsed.OutOfScope);
        Assert.Equal("standard", parsed.DevelopmentMode);
    }

    [Fact]
    public void SpecTaskWithoutFilesRaisesMissingFilesDiagnostic()
    {
        PlanDocument parsed = ParseFixture("spec-tasks.md");

        PlanTask body = Task(parsed, "1.1");
        Assert.False(body.Checked);
        Assert.Equal(["src/KyberWeave.Core/Arbiter/engine.ts", "src/KyberWeave.Core/Arbiter/helpers.ts"], body.Files);

        PlanTask labelled = Task(parsed, "1.2");
        Assert.True(labelled.Checked);
        Assert.Equal(["src/one.ts", "src/two.ts"], labelled.Files);
        Assert.Equal(["1.1"], labelled.DependsOn);

        PlanTask silent = Task(parsed, "2");
        Assert.Empty(silent.Files);
        Assert.Equal(["1", "2"], [.. DiagnosticCodes(parsed, PlanDocumentParser.MissingFilesRule).OrderBy(id => id, StringComparer.Ordinal)]);

        Assert.Equal(["3"], DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
    }
}
