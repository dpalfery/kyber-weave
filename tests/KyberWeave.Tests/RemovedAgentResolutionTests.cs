using System.Security.Cryptography;
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

    private static KyberWeaveConfig Tiered => KyberWeaveConfigDefaults.Product
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] })
        .WithWorkerRoster(WorkerRosters.Tiered);

    [Theory]
    [InlineData("csharp-dev", "easy", 0, "dev-easy")]
    [InlineData("csharp-dev", "medium", 0, "dev-medium")]
    [InlineData("csharp-dev", "hard", 0, "dev-hard")]
    [InlineData("csharp-dev", "easy", 1, "dev-medium")]
    [InlineData("csharp-dev", "easy", 2, "dev-hard")]
    [InlineData("test-dev", "hard", 0, "dev-hard")]
    [InlineData("python-dev", "medium", 1, "dev-hard")]
    [InlineData("CSharp-Dev", "hard", 0, "dev-hard")]
    [InlineData("  csharp-dev  ", "easy", 0, "dev-easy")]
    [InlineData("AZURE-READER", "medium", 0, "platform-researcher")]
    [InlineData("GitHub-DevOps", "hard", 0, "devops")]
    public void ARemovedDeveloperAgentResolvesToTheTierTheDifficultyGives(
        string named,
        string difficulty,
        int failedAttempts,
        string expected)
    {
        RemovedAgentResolution? resolution =
            PlanTaskAgentPick.ResolveRemovedAgent(named, difficulty, failedAttempts);

        Assert.NotNull(resolution);
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

    /// <summary>
    /// Two removed agents are not developers: their replacement is a named role, so an explicit tiered dispatch must
    /// follow the removal map instead of being told to use a developer tier.
    /// </summary>
    [Theory]
    [InlineData("github-devops", "devops")]
    [InlineData("sql-database-architect", "architect")]
    public void AnExplicitDispatchToANonDeveloperRemovedAgentResolvesToItsReplacement(
        string named,
        string expected)
    {
        using DocFixture fixture = Ready(Tiered);

        TaskPromptResult result = Service().Build(
            new TaskPromptRequest(fixture.Root, TaskId: "T1", Plan: Plan, Agent: named),
            Tiered);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal(expected, result.Agent);
        Assert.NotNull(result.Substitution);
        Assert.Contains(expected, result.Substitution, StringComparison.Ordinal);
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

    /// <summary>
    /// A task file records the roster it was written under, and a value that is not exactly <c>tiered</c> — padded,
    /// cased, or one this build does not know — must not decide whether the removed-agent substitution runs. There
    /// is one roster, so a dispatch that names a deleted agent resolves to its tier whatever the file records.
    /// </summary>
    /// <param name="recorded">The roster value the task file is written with.</param>
    [Theory]
    [InlineData(" Tiered ")]
    [InlineData("side-by-side")]
    public void ARecordedRosterValueThatIsNotExactlyTieredStillResolvesTheRemovedName(string recorded)
    {
        KyberWeaveConfig config = Tiered.WithWorkerRoster(recorded);
        using DocFixture fixture = Ready(config);

        TaskPromptResult result = Prompt(fixture, config);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("dev-hard", result.Agent);
        Assert.NotNull(result.Substitution);
    }

    /// <summary>
    /// A plan author writes the role by hand, so a capitalised or space-padded removed name reaches the task file
    /// unchanged; it still resolves rather than dispatching a name no installed agent answers to.
    /// </summary>
    [Theory]
    [InlineData("CSharp-Dev")]
    [InlineData("  csharp-dev  ")]
    public void ARemovedNameThePlanAuthorCapitalisedStillResolves(string role)
    {
        using DocFixture fixture = Ready(Tiered, role);

        TaskPromptResult result = Prompt(fixture, Tiered);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("dev-hard", result.Agent);
        Assert.NotNull(result.Substitution);
        Assert.Contains("dev-hard", result.Substitution, StringComparison.Ordinal);
    }


    /// <summary>
    ///     The retirement of the fourteen agents the removal phase deletes, on a global deployment of two
    ///     harnesses: every file the lock records as Squad's own, unedited, goes with its agent, while the one the
    ///     host edited survives and is reported. The retired names come from the shipped removal map, so a name
    ///     dropped from that map fails here rather than passing on a fixture written to match.
    /// </summary>
    [Fact]
    public async Task EveryRemovedAgentFileIsRemovedFromEveryTargetUnlessTheHostEditedIt()
    {
        using TempDirectory temp = new();
        using SquadHomeHarness harness = new(temp.Path);
        string anchor = harness.MakeDirectory("anchor");
        string[] removed = [.. PlanTaskAgentPick.RemovedAgents.Keys.Order(StringComparer.Ordinal)];
        SquadTarget[] targets = [SquadTarget.Codex, SquadTarget.Claude];
        const string edited = "csharp-dev";
        const string editedContent = "operator edit\n";

        SquadLifecycleResult install = await harness.Lifecycle(FakeSquadRenderer.ScriptedAgents(["conductor", .. removed]))
            .InstallAsync(new SquadInstallRequest(
                anchor,
                SquadDeploymentScope.Global,
                targets,
                Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        string editedPath = Path.Combine(harness.GlobalRoot(SquadTarget.Codex), "agents", edited + ".md");
        Assert.True(File.Exists(editedPath), "The first release must render the agent file this test edits.");
        await File.WriteAllTextAsync(editedPath, editedContent, new UTF8Encoding(false));

        SquadLifecycleResult update = await harness.Lifecycle(FakeSquadRenderer.ScriptedAgents("conductor"))
            .UpdateAsync(new SquadUpdateRequest(
                anchor,
                SquadDeploymentScope.Global,
                targets,
                Version: "1.2.4"));
        Assert.True(update.Success, DescribeFailure(update));

        foreach (SquadTarget target in targets)
        {
            foreach (string agent in removed)
            {
                string path = Path.Combine(harness.GlobalRoot(target), "agents", agent + ".md");
                if (target == SquadTarget.Codex && string.Equals(agent, edited, StringComparison.Ordinal))
                {
                    Assert.True(File.Exists(path), "A file the host edited must survive the update that retires it.");
                    Assert.Equal(editedContent, await File.ReadAllTextAsync(path));
                    continue;
                }

                Assert.False(
                    File.Exists(path),
                    $"'{target}' still carries the removed agent '{agent}', which the lock records as unedited Squad output.");
            }
        }

        string warnings = string.Join(
            Environment.NewLine,
            (update.Warnings ?? []).Select(line => line.Replace(Path.DirectorySeparatorChar, '/')));
        Assert.Contains($"agents/{edited}.md", warnings, StringComparison.Ordinal);
        Assert.Contains("Kept the locally edited", warnings, StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            harness.Store.ReadReceipt(anchor, SquadDeploymentScope.Global));
        Assert.DoesNotContain(receipt.Files, file =>
            file.RelativePath.EndsWith(".md", StringComparison.Ordinal)
            && removed.Contains(Path.GetFileNameWithoutExtension(file.RelativePath), StringComparer.Ordinal)
            && !(string.Equals(file.Target, "codex", StringComparison.Ordinal)
                 && string.Equals(file.RelativePath, $"agents/{edited}.md", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     An adopted entry is a file the host already had with the release's bytes; the lock records that Squad
    ///     did not write it. A release that stops rendering the path must therefore leave it on disk, keeping the
    ///     record, rather than deleting a file the host brought (Requirement 13.6).
    /// </summary>
    [Fact]
    public void AnAdoptedFileIsKeptWhenTheReleaseStopsRenderingIt()
    {
        using TempDirectory temp = new();
        const string retired = "agents/csharp-dev.md";
        const string shipped = "shipped agent\n";
        SquadOwnedFile adopted = new(retired, Digest(shipped), "codex", Adopted: true);
        WriteFile(temp.Path, retired, shipped);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            temp.Path,
            DateTimeOffset.UnixEpoch,
            [],
            [adopted]);

        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateUpdate(
            temp.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [new SquadDeploymentFile("agents/conductor.md", Encoding.UTF8.GetBytes("conductor\n"), "codex")],
            receipt,
            [],
            false,
            TimeProvider.System);

        Assert.DoesNotContain(
            plan.PlannedFileChanges,
            change => change.RelativePath == retired && change.Kind == SquadFileMutationKind.Delete);
        Assert.Contains(plan.Receipt.Files, file => file == adopted);
        Assert.Equal(shipped, File.ReadAllText(Path.Combine(temp.Path, "agents", "csharp-dev.md")));
    }

    /// <summary>A corpus whose Ready plan hands its one task to a removed agent at <c>hard</c>.</summary>
    private static DocFixture Ready(KyberWeaveConfig config, string role = "csharp-dev")
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown(role));
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

    private static string PlanMarkdown(string role) => $$"""
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
        - **Role:** `{{role}}`
        - **Capabilities:** read a settings file and its tests

        Command: `dotnet test --filter SettingsTests`
        Acceptance criteria: the settings reader returns the value it read.
        """;

    private static string DescribeFailure(SquadLifecycleResult result) =>
        string.Join("; ", (result.Errors ?? []).Concat(result.Warnings ?? []));

    private static SquadLock Lock(string version) => new(
        "kyber-squad.lock/v1",
        version,
        version,
        version,
        "full",
        ["codex"],
        [],
        "best-effort",
        Digest("bundle"),
        Digest("asset"),
        SquadApmIdentity.None);

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
