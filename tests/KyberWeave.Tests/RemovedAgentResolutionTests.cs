using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Once the old technology agents leave, a plan or task file that still names one hands its work to the agent
/// that replaces it: a developer tier chosen from the task's difficulty, or the named role that took the work
/// over. A Squad update removes a retired agent's rendered file only when the lock records that the Squad wrote
/// it and the host left it unedited; an edited file is kept and reported.
/// </summary>
public sealed class RemovedAgentResolutionTests
{
    private const string Plan = PlanTaskFileProbe.PlanPath;

    private static KyberWeaveConfig Tiered => KyberWeaveConfig.ProductDefaults
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] })
        .WithWorkerRoster(WorkerRosters.Tiered);

    private static KyberWeaveConfig Technology => KyberWeaveConfig.ProductDefaults
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] });

    [Theory]
    [InlineData("csharp-dev", "easy", 0, "dev-easy")]
    [InlineData("csharp-dev", "medium", 0, "dev-medium")]
    [InlineData("csharp-dev", "hard", 0, "dev-hard")]
    [InlineData("csharp-dev", "easy", 1, "dev-medium")]
    [InlineData("csharp-dev", "easy", 2, "dev-hard")]
    [InlineData("test-dev", "hard", 0, "dev-hard")]
    [InlineData("python-dev", "medium", 1, "dev-hard")]
    public void ARemovedDeveloperAgentResolvesToTheTierTheDifficultyGives(
        string named,
        string difficulty,
        int failedAttempts,
        string expected)
    {
        RemovedAgentResolution? resolution =
            PlanTaskAgentPick.ResolveRemovedAgent(named, difficulty, failedAttempts);

        Assert.NotNull(resolution);
        Assert.Equal(named, resolution.Named);
        Assert.Equal(expected, resolution.Resolved);
    }

    [Fact]
    public void TheResolutionReportsTheNameItReplacedAndTheAgentItChose()
    {
        RemovedAgentResolution resolution =
            Assert.IsType<RemovedAgentResolution>(
                PlanTaskAgentPick.ResolveRemovedAgent("csharp-dev", "hard"));

        Assert.Contains("csharp-dev", resolution.Report, StringComparison.Ordinal);
        Assert.Contains("dev-hard", resolution.Report, StringComparison.Ordinal);
        Assert.Contains("removed", resolution.Report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("azure-reader", "platform-researcher")]
    [InlineData("github-devops", "devops")]
    [InlineData("sql-database-architect", "architect")]
    public void TheOtherRemovedAgentsResolveToTheAgentThatReplacesThem(string named, string expected)
    {
        RemovedAgentResolution? resolution =
            PlanTaskAgentPick.ResolveRemovedAgent(named, "hard");

        Assert.NotNull(resolution);
        Assert.Equal(expected, resolution.Resolved);
        Assert.Contains(named, resolution.Report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs-dev")]
    [InlineData("dev-hard")]
    [InlineData("nobody")]
    public void AnAgentTheRemovalKeepsIsNotSubstituted(string named)
    {
        Assert.Null(PlanTaskAgentPick.ResolveRemovedAgent(named, "hard"));
        Assert.Null(PlanTaskAgentPick.ResolveRemovedAgent(null, "hard"));
    }

    [Fact]
    public void ADispatchThatNamesARemovedAgentUnderTheTieredRosterGoesToTheTierAndReportsIt()
    {
        using DocFixture fixture = Ready(Tiered);

        TaskPromptResult result = Prompt(fixture, Tiered);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("dev-hard", result.Agent);
        Assert.NotNull(result.Substitution);
        Assert.Contains("csharp-dev", result.Substitution, StringComparison.Ordinal);
        Assert.Contains("dev-hard", result.Substitution, StringComparison.Ordinal);
    }

    [Fact]
    public void UnderTheTechnologyRosterTheAgentStillExistsSoTheNameIsLeftAlone()
    {
        using DocFixture fixture = Ready(Technology);

        TaskPromptResult result = Prompt(fixture, Technology);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("csharp-dev", result.Agent);
        Assert.Null(result.Substitution);
    }

    [Fact]
    public async Task AnEditedRenderedFileForARemovedAgentIsKeptAndReported()
    {
        using TempDirectory temp = new();
        using SquadHomeHarness harness = new(temp.Path);
        string project = harness.MakeDirectory("project");

        SquadLifecycleResult install = await harness.Lifecycle(new ScriptedAgentRenderer("conductor", "csharp-dev"))
            .InstallAsync(new SquadInstallRequest(
                project,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        string retired = Path.Combine(project, "agents", "csharp-dev.md");
        Assert.True(File.Exists(retired), "The first release must render the agent file this test retires.");
        await File.WriteAllTextAsync(retired, "operator edit\n", new UTF8Encoding(false));

        SquadLifecycleResult update = await harness.Lifecycle(new ScriptedAgentRenderer("conductor"))
            .UpdateAsync(new SquadUpdateRequest(
                project,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4"));

        Assert.True(update.Success, DescribeFailure(update));
        Assert.True(File.Exists(retired), "A file the host edited must survive the update that stops rendering it.");
        Assert.Equal("operator edit\n", await File.ReadAllTextAsync(retired));
        string warnings = string.Join(
            Environment.NewLine,
            (update.Warnings ?? []).Select(line => line.Replace(Path.DirectorySeparatorChar, '/')));
        Assert.Contains("agents/csharp-dev.md", warnings, StringComparison.Ordinal);
        Assert.Contains("Kept the locally edited", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUneditedRenderedFileForARemovedAgentIsRemoved()
    {
        using TempDirectory temp = new();
        using SquadHomeHarness harness = new(temp.Path);
        string project = harness.MakeDirectory("project");

        SquadLifecycleResult install = await harness.Lifecycle(new ScriptedAgentRenderer("conductor", "csharp-dev"))
            .InstallAsync(new SquadInstallRequest(
                project,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        string retired = Path.Combine(project, "agents", "csharp-dev.md");
        Assert.True(File.Exists(retired), "The first release must render the agent file this test retires.");

        SquadLifecycleResult update = await harness.Lifecycle(new ScriptedAgentRenderer("conductor"))
            .UpdateAsync(new SquadUpdateRequest(
                project,
                SquadDeploymentScope.Project,
                [SquadTarget.Codex],
                Version: "1.2.4"));

        Assert.True(update.Success, DescribeFailure(update));
        Assert.False(File.Exists(retired), "A file the lock records as Squad's own, unedited, is removed with the agent.");

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            harness.Store.ReadReceipt(project, SquadDeploymentScope.Project));
        Assert.DoesNotContain(receipt.Files, file =>
            file.RelativePath.EndsWith("csharp-dev.md", StringComparison.Ordinal));
    }

    /// <summary>A corpus whose Ready plan hands its one task to a removed agent at <c>hard</c>.</summary>
    private static DocFixture Ready(KyberWeaveConfig config)
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown());
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, config, DocsPorts.Files)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(
            outcome.Report.HasErrors,
            string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        return fixture;
    }

    private static TaskPromptService Service()
    {
        IServiceProvider provider = DocsTestCommands.Services();
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            _ => null);
    }

    /// <summary>The prompt the service builds for the plan's one task, under the roster of <paramref name="config"/>.</summary>
    private static TaskPromptResult Prompt(DocFixture fixture, KyberWeaveConfig config) =>
        Service().Build(new TaskPromptRequest(fixture.Root, TaskId: "T1", Plan: Plan), config);

    private static string PlanMarkdown() => """
        ---
        id: plans/2026-11-17-removed-agent
        title: Removed agent
        doc-type: plan
        status: current
        component: RemovedAgent
        owner: Maintainers
        last-reviewed: 2026-11-17
        development-mode: test-first
        ---

        # Removed agent

        **Status: Ready.** This plan is approved for execution.

        ## Tasks

        ### T1: Add the settings reader

        - **Files:** `src/App/Settings.cs`
        - **Depends on:** none
        - **Difficulty:** hard
        - **Technology:** csharp
        - **Role:** `csharp-dev`
        - **Capabilities:** read a settings file and its tests

        Command: `dotnet test --filter SettingsTests`
        Acceptance criteria: the settings reader returns the value it read.
        """;

    private static string DescribeFailure(SquadLifecycleResult result) =>
        string.Join("; ", (result.Errors ?? []).Concat(result.Warnings ?? []));

    /// <summary>A renderer that renders one file per named agent and nothing else, so a release can drop an agent.</summary>
    private sealed class ScriptedAgentRenderer(params string[] agentNames) : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            List<SquadDeploymentFile> files = [];
            foreach (SquadTarget target in request.Targets)
            {
                foreach (string agent in agentNames)
                {
                    files.Add(new SquadDeploymentFile(
                        $"agents/{agent}.md",
                        Encoding.UTF8.GetBytes($"canonical {agent}\n"),
                        SquadTargetCatalog.GetToken(target)));
                }
            }

            return Task.FromResult(new SquadRenderResult(true, files, [], [], []));
        }
    }
}
