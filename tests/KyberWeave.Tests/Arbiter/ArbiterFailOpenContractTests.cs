using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// ADR 0072: the Arbiter blocks only on a machine fact or a confident model answer naming a violation. A parse miss,
/// a provider that does not answer, a low-confidence answer and an evaluator fault all allow, and the record keeps
/// what happened.
/// </summary>
public sealed class ArbiterFailOpenContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kw-fail-open-" + Guid.NewGuid().ToString("N"));

    public ArbiterFailOpenContractTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Supplies a fixed changed-path set for the return triggers, as git would.</summary>
    private sealed class ChangedPaths(params string[] paths) : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            facts.Contains("git.changed-paths.since-dispatch")
                ? facts.With("git.changed-paths.since-dispatch", paths.ToList(), ArbiterFactLabel.Derived)
                : facts;
    }

    /// <summary>The shared plan reader every host uses, over this test's repository.</summary>
    private sealed class RealPlanReader(string root) : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            RealPlanFacts.Create().Enrich(facts, classification, root);
    }

    private ArbiterEvaluator Evaluator(IArbiterProvider provider, IArbiterGitFacts? git = null) => new(
        provider,
        _ => null,
        new InFlightLedger(ArbiterEvaluatorFixtures.DirectoryFor(_root)),
        new DecisionLog(ArbiterEvaluatorFixtures.DirectoryFor(_root)),
        git ?? new ChangedPaths("src/a.cs"),
        new RealPlanReader(_root),
        new FileArbiterModelTargetsReader(new PhysicalFileSystem()),
            new GateReportFacts(),
            new FileArbiterStateFactory(),
        TimeProvider.System);

    private static KyberWeaveConfig ShippedConfig() =>
        ArbiterEvaluatorFixtures.ConfigWith(ArbiterConfig.ProductDefaults.Rules);

    private ArbiterEvent Event(string phase, string target, string prompt, string? output = null) => new()
    {
        Harness = "claude",
        Phase = phase,
        Target = target,
        Prompt = prompt,
        HarnessCaller = "conductor",
        Session = "s1",
        Cwd = _root,
        ToolOutput = output,
        IsDispatch = true,
    };

    private void WritePlan(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            "# Plan\n\n## Tasks\n\n### T3 — Add the parser\nFiles: src/a.cs\nRequired skills: dev-medium\n");
    }

    public static TheoryData<string, string> GarbledDispatches => new()
    {
        // Header names in another case, a path with a space, a task with prose after its id.
        { "pre", "plan_file: docs/plans/my plan.md\ntask: T3 — add the parser\n\nImplement the parser." },
        { "post", "plan_file: docs/plans/my plan.md\ntask: T3 — add the parser\n\nImplement the parser." },
        // No routing header at all.
        { "pre", "Please implement the parser in src/a.cs." },
        { "post", "Please implement the parser in src/a.cs." },
        // An absolute PLAN_FILE that the validated grammar rejects.
        { "pre", "KYBER-ARBITER: true\nPLAN_FILE: /elsewhere/docs/plans/plan.md\nTASK: T3\n\nImplement." },
    };

    [Theory]
    [MemberData(nameof(GarbledDispatches))]
    public async Task AGarbledDispatchWithNoModel_AllowsEveryDelegateAndReturnRule(string phase, string prompt)
    {
        ArbiterEvaluationResult result = await Evaluator(new NoneProvider()).EvaluateAsync(
            Event(phase, "dev-medium", prompt, phase == "post" ? "Done. All tests pass." : null),
            ShippedConfig(),
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord record = Assert.Single(
            ArbiterEvaluatorFixtures.Records(_root), decision => decision.Trigger.StartsWith("delegate", StringComparison.Ordinal)
                && decision.Trigger != "delegate.model");
        Assert.DoesNotContain(record.Rules, rule => rule.Effect == RuleEffects.Escalate);
    }

    [Fact]
    public async Task APlanFileWithASpace_IsReadFromDiskAndDoesNotBlock()
    {
        WritePlan("docs/plans/my plan.md");
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/my plan.md\nTASK: T3\n\nImplement the parser.";

        ArbiterEvaluationResult result = await Evaluator(new NoneProvider()).EvaluateAsync(
            Event("pre", "dev-medium", Prompt), ShippedConfig(), null, TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord record = ArbiterEvaluatorFixtures.Records(_root).First(decision => decision.Trigger == "delegate");
        // The rejected header's raw value found the plan on disk, a machine fact, so PLAN-001 decided at step 0.
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-PLAN-001" && rule.Answer == "has-tasks" && rule.Step == 0);
        // TASK: T3 matched the plan's task, so the task rules read it rather than their no-task guard.
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-READY-001" && rule.Answer == "ready");
        Assert.DoesNotContain(record.Rules, rule => rule.Answer == "no-task");
    }

    [Fact]
    public async Task ALowConfidenceViolatingAnswer_Allows()
    {
        ArbiterEvaluationResult result = await Evaluator(new FakeArbiterProvider
        {
            Answers = { ["KW-ARB-PLAN-001"] = "no-plan" },
            DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
            ScoreDefaultAnswer = true,
            Confidence = 0.5,
        })
            .EvaluateAsync(Event("pre", "dev-medium", "KYBER-ARBITER: true\n\nPlease implement the parser."), ShippedConfig(), null, TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        ArbiterDecisionRecord record = ArbiterEvaluatorFixtures.Records(_root).First(decision => decision.Trigger == "delegate");
        ArbiterDecisionRule plan = Assert.Single(record.Rules, rule => rule.Id == "KW-ARB-PLAN-001");
        Assert.Equal(RuleEngine.Undecidable, plan.Answer);
        Assert.Equal(0.5, plan.Confidence);
    }

    [Fact]
    public async Task AConfidentViolatingAnswer_StillEscalates()
    {
        ArbiterEvaluationResult result = await Evaluator(new FakeArbiterProvider
        {
            Answers = { ["KW-ARB-PLAN-001"] = "no-plan" },
            DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
            ScoreDefaultAnswer = true,
            Confidence = 0.95,
        })
            .EvaluateAsync(Event("pre", "dev-medium", "KYBER-ARBITER: true\n\nPlease implement the parser."), ShippedConfig(), null, TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        ArbiterDecisionRecord record = ArbiterEvaluatorFixtures.Records(_root).First(decision => decision.Trigger == "delegate");
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-PLAN-001" && rule.Answer == "no-plan" && rule.Step == 1);
    }

    [Fact]
    public async Task AReadOnlyDispatchThatChangedTheTree_StillEscalatesWithNoModel()
    {
        ArbiterEvaluationResult result = await Evaluator(new NoneProvider(), new ChangedPaths("src/a.cs")).EvaluateAsync(
            Event("post", "Explore", "KYBER-ARBITER: true\n\nMap the repository.", "Mapped."),
            ShippedConfig(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        Assert.Contains(
            ArbiterEvaluatorFixtures.Records(_root).SelectMany(decision => decision.Rules),
            rule => rule.Id == "KW-ARB-READONLY-001" && rule.Answer == "changed");
    }

    [Fact]
    public async Task APlannerThatWroteOutsideThePlanningDirectories_StillEscalatesWithNoModel()
    {
        ArbiterEvaluationResult result = await Evaluator(new NoneProvider(), new ChangedPaths("src/Rogue.cs")).EvaluateAsync(
            Event("post", "architect", "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nWrite the plan.", "Plan written."),
            ShippedConfig(),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        Assert.Contains(
            ArbiterEvaluatorFixtures.Records(_root).SelectMany(decision => decision.Rules),
            rule => rule.Id == "KW-ARB-PLANNER-DIFF-001" && rule.Answer == "outside");
    }

    [Fact]
    public void TheHeaderBlockKeepsRawValuesItRejects()
    {
        HeaderBlock block = HeaderBlock.Parse(
            "KYBER-ARBITER: true\nPLAN_FILE: /abs/docs/plans/plan.md\nTask: T3 — the parser\n\nBody.\nREVIEW_RUN: late");

        Assert.False(block.Headers.ContainsKey("PLAN_FILE"));
        Assert.Contains("PLAN_FILE", block.RejectedNames);
        Assert.Equal("/abs/docs/plans/plan.md", block.RawValues["PLAN_FILE"]);
        Assert.Equal("T3 — the parser", block.RawValues["TASK"]);
        Assert.Equal("late", block.RawValues["REVIEW_RUN"]);
        Assert.Equal("true", block.RawValues["KYBER-ARBITER"]);
    }

    [Fact]
    public void AGateReportPathWithASpace_IsStillRead()
    {
        string directory = Path.Combine(_root, "gate runs");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "gates.json");
        File.WriteAllText(
            report,
            Core.Review.ReviewJson.Write(new Core.Review.GateReport(
                Core.Review.GateReport.CurrentSchema, [new Core.Review.GateResult("build", true, 0, "pass")])));

        Assert.True(new GateReportFacts().Agrees($"TASK_GATE: PASS report={report}", _root));
    }

    [Fact]
    public void TheHookTimeoutFormulaIsUnchanged()
    {
        Assert.Equal(5, ArbiterHookTimeout.SecondsFor(3000));
        Assert.Equal(6, ArbiterHookTimeout.SecondsFor(3001));
        Assert.Equal(3, ArbiterHookTimeout.SecondsFor(0));
    }

    [Fact]
    public void AProviderTimeoutTheRenderedHookCannotHold_Warns()
    {
        ArbiterProviderConfig repository = new(ArbiterProviderKind.Systemone, "https://api.typesafe.ai", "jev-1.14.0", 3000);

        Diagnostic warning = Assert.Single(
            RuleValidator.CheckProviderTimeout(repository, repository with { TimeoutMs = 10000 }));
        Assert.Equal(RuleValidator.ProviderTimeoutExceedsHook, warning.Code);
        Assert.Equal(Severity.Warning, warning.Severity);

        Assert.Empty(RuleValidator.CheckProviderTimeout(repository, repository));
        Assert.Empty(RuleValidator.CheckProviderTimeout(repository, repository with { Kind = ArbiterProviderKind.None, TimeoutMs = 10000 }));
    }
}
