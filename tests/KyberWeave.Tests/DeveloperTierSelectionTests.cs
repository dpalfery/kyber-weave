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
/// never down, and the task file records the tier the rating in use gives. There is one roster, so these are the
/// rules for every run.
/// </summary>
public sealed class DeveloperTierSelectionTests
{
    private const string Plan = PlanTaskFileProbe.PlanPath;

    private static KyberWeaveConfig Tiered => KyberWeaveConfigDefaults.Product
        .WithOntology(new OntologyConfig { Technologies = ["csharp"] })
        .WithWorkerRoster(WorkerRosters.Tiered);

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

    /// <summary>A plan with one developer task per difficulty, and no documentation task.</summary>
    private static string ThreeTiersPlan() => """
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

        ### T1: Small local change

        - **Files:** `src/App/Small.cs`
        - **Depends on:** none
        - **Difficulty:** easy
        - **Technology:** csharp
        - **Capabilities:** a small local change with its tests

        Command: `dotnet test --filter SmallTests`
        Acceptance criteria: the small change passes its test.

        ### T2: Several files and a non-obvious path

        - **Files:** `src/App/Medium.cs`
        - **Depends on:** none
        - **Difficulty:** medium
        - **Technology:** csharp
        - **Capabilities:** several files with a non-obvious path and tests

        Command: `dotnet test --filter MediumTests`
        Acceptance criteria: the change passes its tests.

        ### T3: Cross-cutting change

        - **Files:** `src/App/Hard.cs`
        - **Depends on:** none
        - **Difficulty:** hard
        - **Technology:** csharp
        - **Capabilities:** a change that spans layers and alters a contract

        Command: `dotnet test --filter HardTests`
        Acceptance criteria: the contract change passes its tests.
        """;

    private static TaskPromptService Service(DocFixture fixture, KyberWeaveConfig config)
    {
        IServiceProvider provider = DocsTestCommands.Services();
        return new TaskPromptService(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            _ => null,
            provider.GetRequiredService<IProcessEnvironment>());
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

    /// <summary>
    /// With no decision model, the architect's rating is the only rating in use, so the task file must name the
    /// tier it gives. A hard task written as <c>dev-medium</c> dispatches the wrong worker from the file alone.
    /// </summary>
    [Fact]
    public void TheTaskFileRecordsEachTierTheArchitectRatedWithNoDecisionModel()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(ThreeTiersPlan());
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files)
            .Run(Plan, PlanTaskFileMode.Write);

        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        Assert.Equal(
            ["dev-easy", "dev-medium", "dev-hard"],
            outcome.File!.Tasks!.Select(task => task.Agent));

        Assert.Equal("dev-easy", Prompt(fixture, Tiered, taskId: "T1").Agent);
        Assert.Equal("dev-medium", Prompt(fixture, Tiered, taskId: "T1", failedAttempts: 1).Agent);
        Assert.Equal("dev-hard", Prompt(fixture, Tiered, taskId: "T2", failedAttempts: 3).Agent);
        Assert.Equal("dev-hard", Prompt(fixture, Tiered, taskId: "T3").Agent);
    }

    [Fact]
    public void ATaskWithNoRatingIsMediumAndOneFailedAttemptClimbsItToHard()
    {
        using DocFixture fixture = Ready(Tiered, difficulty: null);

        Assert.Equal("dev-medium", Prompt(fixture, Tiered).Agent);
        Assert.Equal("dev-hard", Prompt(fixture, Tiered, failedAttempts: 1).Agent);
    }

    /// <summary>
    /// A retry count is caller-supplied and can be at the top of its range. The clamp is the last tier, not an
    /// overflowed index, so the prompt is still built for the strongest developer rather than throwing.
    /// </summary>
    [Theory]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void AnImpossibleFailedAttemptCountSaturatesAtHard(string difficulty)
    {
        using DocFixture fixture = Ready(Tiered, difficulty);

        TaskPromptResult result = Prompt(fixture, Tiered, failedAttempts: int.MaxValue);

        Assert.True(result.Built, result.Refusal);
        Assert.Equal("dev-hard", result.Agent);
    }

    [Fact]
    public void TheTierSaturatingBoundaryMatchesTheLadder()
    {
        Assert.Equal("dev-hard", TaskPromptService.DeveloperTier("easy", int.MaxValue));
        Assert.Equal("dev-hard", TaskPromptService.DeveloperTier("easy", 2));
        Assert.Equal("dev-easy", TaskPromptService.DeveloperTier("easy", 0));
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

    /// <summary>
    /// The recorded failure: a tiered run dispatched task 3.1 to <c>csharp-dev</c>. The dispatch tool must refuse
    /// the technology developer and name the tier the task belongs on, so the worker choice cannot be bypassed.
    /// </summary>
    [Theory]
    [InlineData("csharp-dev", 0, "dev-easy")]
    [InlineData("csharp-dev", 1, "dev-medium")]
    [InlineData("test-dev", 2, "dev-hard")]
    public void UnderTieredADispatchToATechnologyDeveloperIsRefusedNamingTheTier(
        string technologyAgent,
        int failedAttempts,
        string tier)
    {
        using DocFixture fixture = Ready(Tiered, "easy");

        TaskPromptResult result = Prompt(fixture, Tiered, agent: technologyAgent, failedAttempts: failedAttempts);

        Assert.False(result.Built);
        Assert.Contains(technologyAgent, result.Refusal, StringComparison.Ordinal);
        Assert.Contains($"'{tier}'", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("tiered", result.Refusal, StringComparison.Ordinal);
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


    /// <summary>
    /// A confident pick names the developer kind; the file records the tier the rating gives, so the pick's
    /// <c>dev-medium</c> never stands in for an easy task's <c>dev-easy</c>.
    /// </summary>
    [Fact]
    public void AConfidentPickRecordsTheTierTheRatingGives()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown("easy"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        StubProvider provider = new("dev-medium");
        using PlanTaskAgentPick picker = new(
            provider, PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered));

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);

        Assert.False(outcome.Report.HasErrors, string.Join("; ", outcome.Report.Items.Select(item => item.Message)));
        Assert.Equal("dev-easy", outcome.File!.Tasks![0].Agent);
        Assert.Equal("dev-easy", Prompt(fixture, Tiered).Agent);
        Assert.Equal("dev-medium", Prompt(fixture, Tiered, failedAttempts: 1).Agent);
    }

    /// <summary>
    /// A task file written before the removal records the retired roster and a deleted agent's name. A rewrite must
    /// re-pick every agent for the roster now shipped, because keeping the stale pick would name a deleted agent
    /// beside <c>worker-roster: tiered</c>.
    /// </summary>
    [Fact]
    public void ARewriteOfAStaleTaskFileRepicksEveryAgentForTheOneRoster()
    {
        using DocFixture fixture = Ready(Tiered, "easy");
        string path = Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath);
        PlanTaskFile stale = PlanTaskFileFormat.Deserialize(path, File.ReadAllText(path))!;
        PlanTaskFile previousRoster = stale with
        {
            WorkerRoster = WorkerRosters.Technology,
            Tasks = [.. stale.Tasks!.Select(task => task.Id == "T1" ? task with { Agent = "csharp-dev" } : task)]
        };
        File.WriteAllText(path, PlanTaskFileFormat.Serialize(previousRoster));

        using PlanTaskAgentPick picker = new(
            new StubProvider("dev-medium"),
            PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered));
        PlanTaskFileOutcome second = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);

        Assert.False(second.Report.HasErrors, string.Join("; ", second.Report.Items.Select(item => item.Message)));
        Assert.Equal(WorkerRosters.Tiered, second.File!.WorkerRoster);
        Assert.Equal("dev-easy", second.File.Tasks![0].Agent);
    }

    /// <summary>
    /// A hand-edited task file may name an agent that ships nowhere. The tiered dispatch must not hand that name to
    /// the harness, and a rewrite must replace it; a check must report it.
    /// </summary>
    [Fact]
    public void AnUnknownAgentNameIsReplacedByTheTierAndReportedByCheck()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus(PlanMarkdown("easy"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        using PlanTaskAgentPick picker = new(
            new StubProvider("dev-medium"),
            PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered));
        PlanTaskFileOutcome written = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files, picker)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(written.Report.HasErrors, string.Join("; ", written.Report.Items.Select(item => item.Message)));

        // A dispatch that names a name shipping nowhere falls back to the tier; the file is still valid here.
        TaskPromptResult dispatched = Prompt(fixture, Tiered, agent: "my-wrapper");
        Assert.True(dispatched.Built, dispatched.Refusal);
        Assert.Equal("dev-easy", dispatched.Agent);

        string taskPath = Path.Combine(fixture.Root, PlanTaskFileProbe.TaskFilePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(
            taskPath,
            File.ReadAllText(taskPath).Replace("agent: dev-easy", "agent: my-wrapper", StringComparison.Ordinal));

        PlanTaskFileOutcome check = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files)
            .Run(Plan, PlanTaskFileMode.Check);
        Assert.Contains(
            check.Report.Items,
            item => item.Code == PlanTaskFileRules.DiffersFromPlan
                    && item.Message.Contains("differs in agent", StringComparison.Ordinal));

        // The dispatch path checks the file first, so the hand edit is refused rather than handed to a harness.
        TaskPromptResult refused = Prompt(fixture, Tiered, agent: "my-wrapper");
        Assert.False(refused.Built);
        Assert.Contains("does not match the plan", refused.Refusal, StringComparison.Ordinal);

        using PlanTaskAgentPick repick = new(
            new StubProvider("dev-medium"),
            PlanTaskAgentPick.ImplementationAgents(WorkerRosters.Tiered));
        PlanTaskFileOutcome rewritten = new PlanTaskFileGenerator(fixture.Root, Tiered, DocsPorts.Files, repick)
            .Run(Plan, PlanTaskFileMode.Write);
        Assert.False(rewritten.Report.HasErrors, string.Join("; ", rewritten.Report.Items.Select(item => item.Message)));
        Assert.Equal("dev-easy", rewritten.File!.Tasks![0].Agent);
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
        ArbiterRule rule = ArbiterConfigDefaults.Product.Rules.First(
            candidate => string.Equals(candidate.Id, "KW-ARB-ROSTER-001", StringComparison.Ordinal));
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "investigate", target, "Do the thing.", caller, ArbiterCallerSources.Harness, KyberWeaveConfigDefaults.Product);

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
