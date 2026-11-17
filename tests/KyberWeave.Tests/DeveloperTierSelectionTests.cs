using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Under the tiered roster the pick chooses the kind of worker and the difficulty chooses the tier:
/// <c>tier = min(hard, rated difficulty + failed attempts)</c>. A failed attempt moves the task one tier up and
/// never down, the task file records the kind rather than a tier, and under the technology roster nothing moves.
/// </summary>
public sealed class DeveloperTierSelectionTests
{
    private const string Plan = PlanTaskFileProbe.PlanPath;

    private static readonly string[] TechnologyCandidates =
    [
        "csharp-dev", "dal-dev", "docs-dev", "github-devops", "go-dev", "maui-dev", "pulumi-dev",
        "python-dev", "react-dev", "rust-dev", "sql-database-architect", "tauri-dev", "test-dev",
        "typescript-dev"
    ];

    private static KyberWeaveConfig Tiered => KyberWeaveConfig.ProductDefaults
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] })
        .WithWorkerRoster(WorkerRosters.Tiered);

    private static KyberWeaveConfig Technology => KyberWeaveConfig.ProductDefaults
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] });

    /// <summary>
    /// A corpus with a Ready plan whose first task is <paramref name="difficulty"/> and names csharp, whose second
    /// task is a documentation task, and a task file written under the roster of <paramref name="config"/>.
    /// </summary>
    private static DocFixture Ready(KyberWeaveConfig config, string? difficulty = "easy")
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown(difficulty));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, config, DocsPorts.Files)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(
            outcome.Report.HasErrors,
            string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        return fixture;
    }

    /// <summary>The fixture plan: a developer task with one technology, and a documentation task without one.</summary>
    private static string PlanMarkdown(string? difficulty)
    {
        string rating = difficulty is null ? string.Empty : $"- **Difficulty:** {difficulty}\n";
        return $$"""
            ---
            id: plans/2026-11-17-tiers
            title: Tiers
            doc-type: plan
            status: current
            component: Tiers
            owner: Maintainers
            last-reviewed: 2026-11-17
            development-mode: test-first
            ---

            # Tiers

            **Status: Ready.** This plan is approved for execution.

            ## Tasks

            ### T1: Add the settings reader

            - **Files:** `src/App/Settings.cs`
            - **Depends on:** none
            {{rating}}- **Technology:** csharp
            - **Capabilities:** read a settings file and its tests

            Command: `dotnet test --filter SettingsTests`
            Acceptance criteria: the settings reader returns the value it read.

            ### T2: Document the settings reader

            - **Files:** `docs/settings.md`
            - **Depends on:** none
            - **Role:** `docs-dev`
            - **Capabilities:** documentation

            Command: `kyber-weave docs validate .`
            Acceptance criteria: the settings page names the reader.
            """;
    }

    private static TaskPromptService Service(DocFixture fixture, KyberWeaveConfig config)
    {
        IServiceProvider provider = DocsTestCommands.Services();
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            _ => null);
    }

    /// <summary>
    /// The prompt the service builds. The config is the roster of the dispatch: for a task the task file's own
    /// recorded roster wins, and the config is what a dispatch with no task reads.
    /// </summary>
    private static TaskPromptResult Prompt(
        DocFixture fixture,
        KyberWeaveConfig config,
        string? taskId = "T1",
        int failedAttempts = 0,
        string? agent = null,
        string? technology = null) =>
        Service(fixture, config).Build(
            new TaskPromptRequest(
                fixture.Root,
                TaskId: taskId,
                Plan: taskId is null ? null : Plan,
                Agent: agent,
                Technology: technology ?? (taskId is null ? "csharp" : null),
                FailedAttempts: failedAttempts),
            config);

    [Theory]
    [InlineData("easy", 0, "dev-easy")]
    [InlineData("easy", 1, "dev-medium")]
    [InlineData("easy", 2, "dev-hard")]
    [InlineData("easy", 7, "dev-hard")]
    [InlineData("medium", 0, "dev-medium")]
    [InlineData("medium", 1, "dev-hard")]
    [InlineData("hard", 0, "dev-hard")]
    [InlineData("hard", 2, "dev-hard")]
    public void TheTierIsTheRatingClimbedByTheFailedAttempts(string difficulty, int failed, string expected)
    {
        using DocFixture fixture = Ready(Tiered, difficulty);

        TaskPromptResult result = Prompt(fixture, Tiered, failedAttempts: failed);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal(expected, result.Agent);
    }

    [Fact]
    public void ATaskWithNoRatingIsMediumAndOneFailedAttemptClimbsItToHard()
    {
        using DocFixture fixture = Ready(Tiered, difficulty: null);

        Assert.Equal("dev-medium", Prompt(fixture, Tiered).Agent);
        Assert.Equal("dev-hard", Prompt(fixture, Tiered, failedAttempts: 1).Agent);
    }

    [Fact]
    public void ADispatchWithNoTaskStartsAtMediumThenClimbsToHard()
    {
        using DocFixture fixture = Ready(Tiered);

        Assert.Equal("dev-medium", Prompt(fixture, Tiered, taskId: null).Agent);
        Assert.Equal("dev-hard", Prompt(fixture, Tiered, taskId: null, failedAttempts: 1).Agent);
    }

    [Fact]
    public void ADispatchAlreadyOnTheNextTierIsLeftWhereItIs()
    {
        using DocFixture fixture = Ready(Tiered, "easy");

        TaskPromptResult named = Prompt(fixture, Tiered, agent: "dev-hard");
        TaskPromptResult climbed = Prompt(fixture, Tiered, agent: "dev-easy", failedAttempts: 2);

        Assert.True(named.Built, named.Refusal);
        Assert.Equal("dev-hard", named.Agent);
        Assert.Equal("dev-hard", climbed.Agent);
    }

    [Fact]
    public void ADocumentationTaskKeepsItsAgentAndIsNotSteppedUp()
    {
        using DocFixture fixture = Ready(Tiered);

        TaskPromptResult result = Prompt(fixture, Tiered, taskId: "T2", failedAttempts: 2);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("docs-dev", result.Agent);
    }

    [Fact]
    public void UnderTheTechnologyRosterThePickAndTheDispatchAreUnchanged()
    {
        using DocFixture fixture = Ready(Technology, "easy");

        TaskPromptResult result = Prompt(fixture, Technology, failedAttempts: 2);

        Assert.True(result.Built, result.Refusal);
        Assert.Null(result.Agent);
        Assert.Equal("easy", result.Difficulty);
    }

    [Fact]
    public void TheTieredCandidatesAreTheDeveloperKindAndTheDocumentationAndDeliveryAgents()
    {
        IReadOnlyList<ArbiterSquadAgent> tiered = PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered);

        string[] names = [.. tiered.Select(agent => agent.Name)];
        Assert.Equal(["dev-medium", "devops", "docs-dev"], names);
        Assert.DoesNotContain("dev-easy", names);
        Assert.DoesNotContain("dev-hard", names);
        Assert.DoesNotContain("csharp-dev", names);
    }

    [Fact]
    public void TheTechnologyCandidatesAreTheAgentsThatServeTheTechnologyRoster()
    {
        IReadOnlyList<ArbiterSquadAgent> technology = PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Technology);

        Assert.Equal(TechnologyCandidates, technology.Select(agent => agent.Name));
        Assert.DoesNotContain("dev-medium", technology.Select(agent => agent.Name));
        Assert.DoesNotContain("devops", technology.Select(agent => agent.Name));
    }

    [Fact]
    public void AConfidentPickUnderTheTieredRosterRecordsTheDeveloperKindAndTheTierFollowsDifficulty()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown("easy"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        StubProvider provider = new("dev-medium");
        using PlanTaskAgentPick picker = new(
            provider, PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered));

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);

        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        Assert.Equal("dev-medium", outcome.File!.Tasks![0].Agent);
        Assert.Equal("dev-easy", Prompt(fixture, Tiered).Agent);
        Assert.Equal("dev-medium", Prompt(fixture, Tiered, failedAttempts: 1).Agent);
    }

    /// <summary>Every caller of a replaced agent lists its replacement, so the roster rule allows the dispatch.</summary>
    [Theory]
    [InlineData("conductor", "dev-easy")]
    [InlineData("conductor", "dev-medium")]
    [InlineData("conductor", "dev-hard")]
    [InlineData("conductor", "platform-researcher")]
    [InlineData("conductor", "devops")]
    [InlineData("bug-crusher", "dev-easy")]
    [InlineData("bug-crusher", "dev-medium")]
    [InlineData("bug-crusher", "dev-hard")]
    [InlineData("bug-crusher", "devops")]
    [InlineData("architect", "platform-researcher")]
    [InlineData("code-reviewer", "platform-researcher")]
    public void TheRosterRuleAllowsEachListedCallerToDispatchItsNewAgent(string caller, string target)
    {
        ArbiterRule rule = ArbiterConfig.ProductDefaults.Rules.First(
            candidate => string.Equals(candidate.Id, "KW-ARB-ROSTER-001", StringComparison.Ordinal));
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "investigate", target, "Do the thing.", caller, ArbiterCallerSources.Harness, KyberWeaveConfig.ProductDefaults);

        Assert.Equal("listed", RuleEngine.Decide(rule, facts));
    }

    /// <summary>A decision model that answers every question with one agent name at one confidence.</summary>
    private sealed class StubProvider(string answer) : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = rules.ToDictionary(rule => rule.Id, _ => answer);
            Dictionary<string, ArbiterStep1Score> scores = rules.ToDictionary(
                rule => rule.Id, _ => new ArbiterStep1Score(null, 0.97, 0.8));
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }
}
