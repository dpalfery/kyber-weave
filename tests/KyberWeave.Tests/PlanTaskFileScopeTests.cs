using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A task's file list in the task file comes from the plan's text alone. The file is compared with the plan again on
/// every packet, so a list that also depended on which folders exist changed as soon as a worker created one, and the
/// run stopped on a task file that no longer matched its unchanged plan.
/// </summary>
public sealed class PlanTaskFileScopeTests : IDisposable
{
    private const string Plan = "6-Docs/plans/2026-11-16-cards.md";
    private const string TaskFile = "6-Docs/plans/2026-11-16-cards.tasks.json";

    private const string FrontMatter = """
        ---
        id: plans/2026-11-16-cards
        title: Cards
        doc-type: plan
        status: current
        owner: Maintainers
        last-reviewed: 2026-11-16
        ---

        # Cards

        **Status:** Ready

        ## Tasks


        """;

    /// <summary>A file in a folder that does not exist yet, a folder, a pattern, and a name whose case differs from a folder a worker creates.</summary>
    private const string Tasks = """
        ### T1: Repository method

        - **Files:** `src/Data/JobRepository.cs`
        - **Depends on:** none
        - **Capabilities:** C# repository work

        Build it. Command: `dotnet test --filter JobRepositoryTests`
        Acceptance criteria: counts per status are returned.

        ### T3: Live status cards

        - **Files:**
          - `web/tests/live-status-cards.spec.ts`: the test
          - `web/Artifacts/T3/`: the screenshots
          - `web/src/cards/*.tsx`: the cards
        - **Depends on:** T1
        - **Capabilities:** Playwright tests

        Build it. Command: `npx playwright test cards`
        Acceptance criteria: four cards show counts.
        """;

    private const string ScratchTasks = """
        ### T3: Live status cards

        - **Files:**
          - `web/tests/live-status-cards.spec.ts`: the test
          - `.agent_scratchpad/T3/live-status-cards.spec.ts`: a scratch copy of the test
          - `.agent_scratchpad/T3/artifacts/`: the screenshots
          - `./.Agent_Scratchpad/T3/trace.zip`: the trace
        - **Depends on:** none
        - **Capabilities:** Playwright tests

        Build it. Command: `npx playwright test cards`
        Acceptance criteria: four cards show counts.
        """;

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void TheSamePlanGivesTheSameTaskFileAfterAWorkerCreatesTheFoldersItNames()
    {
        WritePlan(Tasks);
        PlanTaskFileOutcome before = Run(PlanTaskFileMode.Write);
        Assert.False(before.Report.HasErrors, Describe(before.Report));

        WorkersCreateFolders();
        PlanTaskFileOutcome check = Run(PlanTaskFileMode.Check);
        PlanTaskFileOutcome after = Run(PlanTaskFileMode.Preview);

        Assert.False(check.Report.HasErrors, Describe(check.Report));
        Assert.Equal(before.Json, after.Json);
        Assert.Equal(["src/Data/JobRepository.cs"], after.File!.Tasks![0].Files);
        Assert.Equal(
            ["web/tests/live-status-cards.spec.ts", "web/Artifacts/T3/**", "web/src/cards/*.tsx"],
            after.File.Tasks[1].Files);
    }

    [Fact]
    public void DocsValidateAcceptsTheTaskFileAfterAWorkerCreatesTheFoldersItNames()
    {
        WritePlan(Tasks);
        Assert.False(Run(PlanTaskFileMode.Write).Report.HasErrors);
        Assert.DoesNotContain(Validate().Items, item => item.Code == PlanTaskFileRules.DiffersFromPlan);

        WorkersCreateFolders();

        Assert.DoesNotContain(Validate().Items, item => item.Code == PlanTaskFileRules.DiffersFromPlan);

        // The control: the same check does report a file list that really differs from the plan's.
        string path = Path.Combine(_repo.Path, TaskFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("src/Data/JobRepository.cs", "src/Data/Other.cs", StringComparison.Ordinal));
        Assert.Contains(Validate().Items, item => item.Code == PlanTaskFileRules.DiffersFromPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AScratchPathInATasksFilesIsRefusedWhetherOrNotTheScratchFolderExists(bool scratchFolderExists)
    {
        WritePlan(ScratchTasks);
        if (scratchFolderExists)
            WorkersCreateFolders();

        PlanTaskFileOutcome outcome = Run(PlanTaskFileMode.Write);

        Diagnostic finding = Assert.Single(outcome.Report.Items, item => item.Severity == Severity.Error);
        Assert.Equal("KW-DOC-TASKS-013", finding.Code);
        Assert.Equal("T3", finding.Subject);
        Assert.Contains(".agent_scratchpad/T3/live-status-cards.spec.ts", finding.Message, StringComparison.Ordinal);
        Assert.Contains(".agent_scratchpad/T3/artifacts/**", finding.Message, StringComparison.Ordinal);
        Assert.Contains("./.Agent_Scratchpad/T3/trace.zip", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("web/tests/live-status-cards.spec.ts,", finding.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Json);
        Assert.False(File.Exists(Path.Combine(_repo.Path, TaskFile)));
    }

    private void WritePlan(string tasks)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "6-Docs", "plans"));
        File.WriteAllText(Path.Combine(_repo.Path, Plan), FrontMatter + tasks + "\n");
    }

    /// <summary>
    /// What a run leaves in the working tree: another task's scratch folder, the folders and a file the plan names,
    /// and a folder whose name differs from a planned one only by case.
    /// </summary>
    private void WorkersCreateFolders()
    {
        foreach (string folder in new[] { ".agent_scratchpad/t1", ".agent_scratchpad/t3/artifacts", "src/Data", "web/tests", "web/artifacts/t3", "web/src/cards" })
            Directory.CreateDirectory(Path.Combine(_repo.Path, folder));
        File.WriteAllText(Path.Combine(_repo.Path, "src/Data/JobRepository.cs"), "// written by T1");
        File.WriteAllText(Path.Combine(_repo.Path, "web/src/cards/Card.tsx"), "// written by T3");
    }

    private static readonly KyberWeaveConfig Config = KyberWeaveConfig.ProductDefaults.WithOntology(OntologyConfig.ProductDefaults);

    private PlanTaskFileOutcome Run(PlanTaskFileMode mode) =>
        new PlanTaskFileGenerator(_repo.Path, Config, DocsPorts.Files).Run(Plan, mode);

    private DiagnosticReport Validate()
    {
        DocumentSet corpus = new DocumentLoader(_repo.Path, Config.Ontology).Load();
        return new PlanTaskFileValidator(_repo.Path, Config, DocsPorts.Files).Validate(corpus);
    }

    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(item => $"{item.Code} {item.Message}"));

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("src/../../secrets.txt")]
    [InlineData("C:/foo/a.cs")]
    [InlineData("src/./a.cs")]
    public void APathThatLeavesTheRepositoryIsNotATaskFile(string entry)
    {
        (IReadOnlyList<string> kept, _, IReadOnlyList<string> dropped) = PlanFileScope.Filter([entry]);

        Assert.Empty(kept);
        Assert.Equal([entry], dropped);
    }

    [Theory]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("src\\a.cs", "src/a.cs")]
    [InlineData("src/dir/", "src/dir")]
    public void AKeptPathIsWrittenInItsRepositoryRelativeForm(string entry, string expected)
    {
        Assert.Equal([expected], PlanFileScope.Filter([entry]).Kept);
    }
}
