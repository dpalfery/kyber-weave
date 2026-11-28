using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Reflection;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The docs server's two plan lifecycle tools, which the conductor calls instead of dispatching a model: finalize an
/// approved Draft plan, and archive a plan whose closeout evidence is complete. Each refusal changes nothing, and the
/// archive refusal sends its findings to docs-dev.
/// </summary>
public sealed class McpPlanLifecycleToolsTests
{
    private const string ClosedPlan = "6-Docs/plans/2026-10-20-widget.md";
    private const string ArchivedPlan = "6-Docs/archive/plans/2026-10-20-widget.md";

    private static DocsTools Tools(string root, IServiceProvider? services = null)
    {
        IServiceProvider provider = services ?? DocsTestCommands.Services();
        return DocsRootRegistry.CreateTools(root, codeGraphRefreshEnabled: false,
            DocsTestCommands.Registry(root, path => DocsRootRegistry.CreateTools(path, codeGraphRefreshEnabled: false, registry: null, provider)),
            provider);
    }

    private static async Task<DocFixture> GitRepository(DocFixture fixture)
    {
        string git = Path.Combine(fixture.Root, ".git");
        if (Directory.Exists(git))
            Directory.Delete(git, recursive: true);
        await Git(fixture.Root, "init", "-q", "-b", "main");
        return fixture;
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData(DocsTools.FinalizeToolName)]
    [InlineData(DocsTools.ArchiveToolName)]
    public void TheLifecycleToolsChangeFilesInAClosedWorldAndRequireAProjectPath(string toolName)
    {
        MethodInfo method = typeof(DocsTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.False(attribute.ReadOnly);
        Assert.False(attribute.OpenWorld);
        Assert.False(attribute.Destructive);
        ParameterInfo projectPath = method.GetParameters()[0];
        Assert.Equal("projectPath", projectPath.Name);
        Assert.False(projectPath.HasDefaultValue);
    }

    [Fact]
    public async Task FinalizeSetsTheDraftPlanReadyAndNamesTheTaskFile()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());

        string response = await Tools(fixture.Root).FinalizePlanAsync(fixture.Root, PlanTaskFileProbe.PlanPath, "autonomous",
            "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: finalized", response, StringComparison.Ordinal);
        Assert.Contains($"TASK_FILE: {PlanTaskFileProbe.TaskFilePath}", response, StringComparison.Ordinal);
        Assert.True(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
        Assert.Contains("**Status: Ready,", Read(fixture, PlanTaskFileProbe.PlanPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizeCommitsOnlyThePlanFilesAndListsTheReadyQueue()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        await Git(fixture.Root, "config", "user.name", "t");
        await Git(fixture.Root, "config", "user.email", "t@example.com");
        await Git(fixture.Root, "add", "-A");
        await Git(fixture.Root, "commit", "-q", "-m", "init");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "stray.txt"), "not part of the plan");

        string response = await Tools(fixture.Root).FinalizePlanAsync(fixture.Root, PlanTaskFileProbe.PlanPath, "autonomous",
            "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: finalized", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit the plan", response, StringComparison.Ordinal);
        Assert.Contains("ready queue", response, StringComparison.Ordinal);
        Assert.Contains("T1: ", response, StringComparison.Ordinal);
        Assert.Contains("| needs: ", response, StringComparison.Ordinal);
        Assert.Contains("dispatch the first ready tasks now", response, StringComparison.Ordinal);
        string committed = await Git(fixture.Root, "show", "--name-only", "--format=%s", "HEAD");
        Assert.StartsWith("docs(plan): finalize ", committed, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.PlanPath, committed, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.TaskFilePath, committed, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.PlanIndexPath, committed, StringComparison.Ordinal);
        Assert.DoesNotContain("stray.txt", committed, StringComparison.Ordinal);
        Assert.Contains("?? stray.txt", await Git(fixture.Root, "status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizeWithAConfiguredModel_WritesThePickAndTheReadyQueueNamesTheAgent()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        await Git(fixture.Root, "config", "user.name", "t");
        await Git(fixture.Root, "config", "user.email", "t@example.com");
        await Git(fixture.Root, "add", "-A");
        await Git(fixture.Root, "commit", "-q", "-m", "init");
        IServiceProvider picking = DocsTestCommands.Services(services => services.AddSingleton<IPlanTaskAgentPickerFactory>(
            new DocsTestCommands.FixedPickerFactory(() => new PlanTaskAgentPick(
                new PickingProvider(),
                PlanTaskAgentPick.ImplementationAgents(),
                new FakeEmbeddedResources().Add(PlanTaskAgentPick.RubricResourceName, "Rate the task by the rubric.")))));
        string response = await Tools(fixture.Root, picking).FinalizePlanAsync(fixture.Root, PlanTaskFileProbe.PlanPath, "autonomous",
            "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: finalized", response, StringComparison.Ordinal);
        Assert.Contains("| agent=dev-medium |", response, StringComparison.Ordinal);
        PlanTaskFile written = Assert.IsType<PlanTaskFile>(PlanTaskFileFormat.Deserialize(
            PlanTaskFileProbe.TaskFilePath,
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath))));
        Assert.Equal(
            "dev-medium",
            Assert.Single(written.Tasks!, task => task.Id == PlanTaskFileProbe.FirstTask).Agent);
    }

    private sealed class PickingProvider : KyberWeave.Core.Arbiter.Providers.IArbiterProvider
    {
        public Task<KyberWeave.Core.Arbiter.Providers.ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<KyberWeave.Core.Arbiter.Rules.ArbiterRule> rules,
            KyberWeave.Core.Arbiter.ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new KyberWeave.Core.Arbiter.Providers.ArbiterStep1BatchResult(
                rules.ToDictionary(rule => rule.Id, _ => "dev-medium"),
                "stub",
                null,
                true,
                rules.ToDictionary(rule => rule.Id, _ => new KyberWeave.Core.Arbiter.Providers.ArbiterStep1Score(null, 0.95, 0.8))));
    }

    [Fact]
    public async Task FinalizeRefusesAndChangesNothingWhenOtherFilesAreStaged()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "stray.txt"), "staged by someone else");
        await Git(fixture.Root, "add", "stray.txt");
        string before = Read(fixture, PlanTaskFileProbe.PlanPath);

        string response = await Tools(fixture.Root).FinalizePlanAsync(fixture.Root, PlanTaskFileProbe.PlanPath, "autonomous",
            "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-FINALIZE-008", response, StringComparison.Ordinal);
        Assert.Equal(before, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    /// <summary>A linked worktree named by projectPath is finalized and archived in that worktree, not refused.</summary>
    [Fact]
    public async Task BothToolsWorkInALinkedWorktree()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        await Git(fixture.Root, "add", "-A");
        await Git(fixture.Root, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        string worktree = fixture.Root + "-wt";
        await Git(fixture.Root, "worktree", "add", "-q", worktree);
        try
        {
            DocsTools tools = Tools(fixture.Root);

            string finalized = await tools.FinalizePlanAsync(worktree, PlanTaskFileProbe.PlanPath, "autonomous", "AUTONOMOUS_APPROVAL: approve-and-execute");
            string archive = await tools.ArchivePlanAsync(worktree, PlanTaskFileProbe.PlanPath);

            Assert.Contains("result: finalized", finalized, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(worktree, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
            Assert.Contains("result: refused", archive, StringComparison.Ordinal);
            Assert.DoesNotContain("projectPath was refused", archive, StringComparison.Ordinal);
        }
        finally
        {
            await Git(fixture.Root, "worktree", "remove", "--force", worktree);
        }
    }

    [Theory]
    [InlineData("", "autonomous")]
    [InlineData("ROOT", "approve")]
    public async Task FinalizeWithoutAProjectPathOrAModeIsRefusedAndChangesNothing(string projectPath, string mode)
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);

        string response = await Tools(fixture.Root).FinalizePlanAsync(projectPath == "ROOT" ? fixture.Root : projectPath,
            PlanTaskFileProbe.PlanPath, mode, "AUTONOMOUS_APPROVAL: approve-and-execute");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
        Assert.False(Exists(fixture, PlanTaskFileProbe.TaskFilePath));
    }

    [Fact]
    public async Task ArchiveRefusesAPlanWithoutCloseoutEvidenceAndSendsTheFindingsToDocsDev()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());
        string plan = Read(fixture, PlanTaskFileProbe.PlanPath);

        string response = await Tools(fixture.Root).ArchivePlanAsync(fixture.Root, PlanTaskFileProbe.PlanPath);

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-LIFECYCLE-004", response, StringComparison.Ordinal);
        Assert.Contains("send these findings to docs-dev", response, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, PlanTaskFileProbe.PlanPath));
    }

    [Fact]
    public async Task ArchiveMovesAPlanWhoseCloseoutEvidenceIsComplete()
    {
        using DocFixture fixture = await GitRepository(DocFixture.Create().WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(ClosedPlan, CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", linkPrefix: ".."))
            .Write("6-Docs/plans/README.md", """
                ---
                id: plans/index
                title: Plan Inventory
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-01
                ---

                # Plan Inventory

                | Plan | Status |
                |---|---|
                | [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |
                """));

        string response = await Tools(fixture.Root).ArchivePlanAsync(fixture.Root, "2026-10-20-widget");

        Assert.Contains("result: archived", response, StringComparison.Ordinal);
        Assert.Contains("mergeReady true", response, StringComparison.Ordinal);
        Assert.False(Exists(fixture, ClosedPlan));
        Assert.True(Exists(fixture, ArchivedPlan));
    }

    /// <summary>
    /// The docs tool is the path a conductor archives through, and it must refuse an unticked task exactly as the CLI
    /// does: a plan archived with an undelivered task reads as finished work that shipped. The second end-to-end
    /// tiered run archived one because the tool binary it called carried no checkbox gate; this holds the served path
    /// to the same rule.
    /// </summary>
    [Fact]
    public async Task ArchiveRefusesAPlanWithAnUncheckedNumberedTaskAndMovesNothing()
    {
        string sections = CloseoutCorpus.DecisionsSection("D1")
            + CloseoutCorpus.CloseoutMappingSection(("D1, H1", "[Widget architecture](../reference/widget.md)"))
            + CloseoutCorpus.DesignHarvestSection("H1")
            + """

              ## Tasks

              - [x] 1.1 Slug rule
              - [ ] 1.2 Word count

              """;
        using DocFixture fixture = await GitRepository(new DocFixture().WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(ClosedPlan, CloseoutCorpus.PlanDocument(status: "ready", bodyStatus: "Ready", sections: sections))
            .Write("6-Docs/plans/README.md", """
                ---
                id: plans/index
                title: Plan Inventory
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-01
                ---

                # Plan Inventory

                | Plan | Status |
                |---|---|
                | [2026-10-20-widget.md](2026-10-20-widget.md) | Ready |
                """));
        string plan = Read(fixture, ClosedPlan);

        string response = await Tools(fixture.Root).ArchivePlanAsync(fixture.Root, "2026-10-20-widget");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-CLOSEOUT-006", response, StringComparison.Ordinal);
        Assert.Contains("1.2", response, StringComparison.Ordinal);
        Assert.Equal(plan, Read(fixture, ClosedPlan));
        Assert.False(Exists(fixture, ArchivedPlan));
    }

    [Fact]
    public async Task ArchiveRefusesAPathThatIsNotAPlan()
    {
        using DocFixture fixture = await GitRepository(PlanFinalizeFixture.Repository());

        string response = await Tools(fixture.Root).ArchivePlanAsync(fixture.Root, "src/Placeholder.cs");

        Assert.Contains("result: refused", response, StringComparison.Ordinal);
        Assert.True(Exists(fixture, "src/Placeholder.cs"));
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }
}
