using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The dispatches a Copilot CLI root conductor sent in a real run with no routing headers (ADR 0082). They classify
/// as delegations, their plan and task are resolved from the run's worktree, and a dispatch that is no plan task has
/// a step-0 path that allows. The prompts are the run's own, cut to their opening lines, with the worktree path
/// replaced.
/// </summary>
public sealed class ArbiterHeaderlessDelegateIdentityTests : IDisposable
{
    private const string DataLayerTask =
        """
        TASK_ID: T1
        TITLE: Job status summary data access (JobStatusSummaryDto + IJobRepository/JobRepository.GetJobStatusSummaryAsync, single GROUP BY)
        WORKTREE: /work/demo.worktrees/run
        BRANCH: copilot/feat/admin-dashboard-job-status-cards
        DEVELOPMENT_MODE: test-first (write the failing tests first, run them failing, then implement, in one dispatch)
        DEPENDENCIES: none
        """;

    private const string ApiTask =
        """
        TASK_ID: T2
        TITLE: Job status summary API (IJobService/JobService delegation + GET /api/job/status-summary, ManagerOrAdmin, 500 on exception)
        WORKTREE: /work/demo.worktrees/run
        DEPENDENCIES: T1 is complete and committed on this branch (commit f43ed15). Build on it; do not redo it.
        """;

    private const string ProsePacket =
        "Run docs packet T3 in /work/demo.worktrees/run and do exactly what it says.";

    private const string Closeout =
        """
        Documentation-impact review and closeout for a completed, plan-backed change.

        WORKTREE: /work/demo.worktrees/run
        PLAN: docs/plans/cards.md (Ready; task file .tasks.json beside it)

        Completed tasks (committed on the branch):
        - T1 dal-dev: f43ed15
        - T2 csharp-dev: 0b08d4c
        - T3 react-dev: 04cb146
        """;

    private const string Delivery =
        "Deliver the run branch. Worktree /work/demo.worktrees/run, branch copilot/feat/cards, base development. " +
        "Stage only docs/plans/README.md and docs/archive/plans/cards.md, commit, push and open a draft pull request.";

    private const string PlanText =
        """
        # Plan: job status cards

        **Status: Ready, 2026-11-14.**

        ## Tasks

        ### T1 — Job status summary aggregation in the data layer
        - **Capabilities:** ADO.NET repository method with a GROUP BY query
        - **Files:** `src/Data/JobRepository.cs`

        ### T2 — Job status summary service and API endpoint
        - **Depends on:** T1
        - **Files:** `src/Api/JobController.cs`

        ### T3 — Job status cards on the dashboard overview
        - **Files:** `web/src/JobStatusCards.tsx`
        """;

    private static readonly string[] NonTaskPlanReadAnswers = ["closeout", "not-a-task"];

    private readonly TempDirectory _repo = new();

    public ArbiterHeaderlessDelegateIdentityTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, "docs", "plans"));
        File.WriteAllText(Path.Combine(_repo.Path, "docs", "plans", "cards.md"), PlanText);
        File.WriteAllText(Path.Combine(_repo.Path, "docs", "plans", "README.md"), "# Plans\n\n**Status: Ready**\n");
    }

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("dal-dev", DataLayerTask, "T1")]
    [InlineData("csharp-dev", ApiTask, "T2")]
    [InlineData("react-dev", ProsePacket, "T3")]
    public void AHeaderlessTaskDispatch_IsADelegate_WithItsTaskResolved(string target, string prompt, string task)
    {
        (TriggerClassification classification, ArbiterFactSet facts) = Facts(target, prompt);

        Assert.Equal("delegate", classification.Trigger);
        Assert.Equal(PlanFacts.MachineIdentity, Value(facts, PlanFacts.IdentityFact));
        Assert.Equal(true, Value(facts, "plan.exists"));
        Assert.Equal(task, Value(facts, "plan.task"));
    }

    [Theory]
    [InlineData("docs-dev", Closeout)]
    [InlineData("github-devops", Delivery)]
    public void ADispatchThatIsNoPlanTask_KnowsThePlan_AndIsAllowedAtStepZero(string target, string prompt)
    {
        (TriggerClassification classification, ArbiterFactSet facts) = Facts(target, prompt);

        Assert.Equal("delegate", classification.Trigger);
        Assert.Equal(true, Value(facts, "plan.exists"));
        Assert.Null(Value(facts, "plan.task"));
        Assert.Equal("has-tasks", Decide("KW-ARB-PLAN-001", facts));
        Assert.Equal("task-unknown", Decide("KW-ARB-SCOPE-001", facts));
        Assert.Equal("no-task", Decide("KW-ARB-READY-001", facts));
        Assert.Equal("no-task", Decide("KW-ARB-SCOPE-002", facts));
        Assert.Contains(Decide("KW-ARB-PLANREAD-001", facts), NonTaskPlanReadAnswers);
    }

    [Fact]
    public void AHeaderlessDispatchWithNoActivePlan_IsAllowedWithoutAskingTheModel()
    {
        File.Delete(Path.Combine(_repo.Path, "docs", "plans", "cards.md"));

        (_, ArbiterFactSet facts) = Facts("dal-dev", DataLayerTask);

        Assert.Equal("unresolved", Decide("KW-ARB-PLAN-001", facts));
        Assert.Equal("task-unknown", Decide("KW-ARB-SCOPE-001", facts));
    }

    [Fact]
    public void TwoActivePlans_LeaveThePlanUnknown()
    {
        File.WriteAllText(Path.Combine(_repo.Path, "docs", "plans", "other.md"), PlanText);

        (_, ArbiterFactSet facts) = Facts("dal-dev", DataLayerTask);

        Assert.Null(Value(facts, "plan.exists"));
        Assert.Equal("unresolved", Decide("KW-ARB-PLAN-001", facts));
    }

    [Fact]
    public void ANonTaskReturn_IsNotHeldToThePlansOutOfScopeList()
    {
        (_, ArbiterFactSet facts) = Facts("docs-dev", Closeout, phase: "post");
        facts = facts
            .With("plan.out-of-scope", new List<string> { "docs/todo/cards.md" }, ArbiterFactLabel.Derived)
            .With("git.changed-paths.attributed", new List<string> { "docs/todo/cards.md" }, ArbiterFactLabel.Derived);

        Assert.Equal("no-task", Decide("KW-ARB-DIFF-001", facts));
        Assert.Equal("not-a-task", Decide("KW-ARB-GATE-REPORT-001", facts.With("delegation.returned-tail", "Closeout done.", ArbiterFactLabel.Asserted).With("config.task-gates-declared", true, ArbiterFactLabel.Derived)));
    }

    [Fact]
    public void HeadersStillWin_OverMachineIdentity()
    {
        (TriggerClassification classification, ArbiterFactSet facts) = Facts(
            "csharp-dev", "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/cards.md\nTASK: T2\n\nTASK_ID: T1\nDo the work.");

        Assert.Equal("delegate", classification.Trigger);
        Assert.Equal(PlanFacts.HeaderIdentity, Value(facts, PlanFacts.IdentityFact));
        Assert.Equal("T2", Value(facts, "plan.task"));
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("antigravity")]
    public void OnAHarnessWhereNoCallerMeansUnknown_AHeaderlessDispatchStaysAnInvestigate(string harness)
    {
        TriggerClassification classification = Assert.Single(TriggerClassifier.Classify(Event("dal-dev", DataLayerTask, harness)));

        Assert.Equal("investigate", classification.Trigger);
    }

    [Fact]
    public void ARootDispatchToAReadOnlyAgent_KeepsItsTrigger()
    {
        TriggerClassification classification = Assert.Single(TriggerClassifier.Classify(Event("research-agent", "Find the vendor docs.")));

        Assert.Equal("investigate", classification.Trigger);
    }

    [Theory]
    [InlineData("TASK_ID: T2\nUses task T1's DTO.", "T2")]
    [InlineData("Task T3: build the cards.", "T3")]
    [InlineData("TASK PACKET \u2014 T1 (test-first). Work ONLY in worktree: /work/run.", "T1")]
    [InlineData("Implement task T1 and task T2.", null)]
    [InlineData("Implement task T9.", null)]
    [InlineData("Build the cards.", null)]
    public void TheTaskId_IsTakenFromALabelOrOneUnambiguousMention(string prompt, string? expected)
    {
        PlanDocument document = PlanDocumentParser.Parse(PlanText);

        Assert.Equal(expected, PlanFacts.TaskNamedIn(document, prompt)?.Id);
    }

    private (TriggerClassification Classification, ArbiterFactSet Facts) Facts(string target, string prompt, string phase = "pre")
    {
        TriggerClassification classification = Assert.Single(TriggerClassifier.Classify(Event(target, prompt, phase: phase)));
        KyberWeaveConfig config = KyberWeaveConfig.ProductDefaults;
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, config);
        facts = facts.With("config.planning-dirs", new List<string> { "docs/plans/**" }, ArbiterFactLabel.Derived);
        return (classification, PlanFacts.Enrich(facts, classification, _repo.Path));
    }

    private static ArbiterEvent Event(string target, string prompt, string harness = "copilot-cli", string phase = "pre") =>
        new() { Harness = harness, Phase = phase, Target = target, Prompt = prompt };

    private static object? Value(ArbiterFactSet facts, string name) =>
        facts.TryGet(name, out ArbiterFact? fact) ? fact.Value : null;

    private static string Decide(string ruleId, ArbiterFactSet facts) =>
        RuleEngine.Decide(ArbiterConfig.ProductDefaults.Rules.First(rule => rule.Id == ruleId), facts);
}
