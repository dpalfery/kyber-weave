using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the planner and readiness gates. A marked planner dispatch whose
/// <c>PLAN_FILE</c> names a document inside the plan, spec or todo directory is
/// <c>recognised</c>; a delegate task with an empty dependency list is <c>ready</c>
/// even when no completion fact has been recorded.
/// RED: <c>config.planning-dirs</c> carries bare directories, so
/// <c>KW-ARB-PLANNER-001</c> cannot match a plan file and answers <c>malformed</c>;
/// <c>KW-ARB-READY-001</c> answers <c>waiting</c> for an empty dependency list when
/// <c>ledger.completed-tasks</c> is absent.
/// </summary>
public sealed class ArbiterPlannerRuleTests
{
    private static readonly string[] UnsatisfiedDependency = ["T1"];

    private static KyberWeaveConfig DocsConfig()
    {
        return new KyberWeaveConfig { Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs") };
    }

    private static ArbiterRule Shipped(string id)
    {
        return ArbiterConfig.ProductDefaults.Rules.Single(rule => rule.Id == id);
    }

    private static ArbiterFactSet Facts(params (string Name, object? Value)[] entries)
    {
        ArbiterFactSet set = new();
        foreach ((string name, object? value) in entries)
        {
            set = set.With(name, value, ArbiterFactLabel.Derived);
        }

        return set;
    }

    private static string PlannerPrompt(string planFile)
    {
        return $"KYBER-ARBITER: true\nPLAN_FILE: {planFile}\n\nWrite the plan.";
    }

    /// <summary>
    /// Builds planner facts through the real classifier and fact builder, the path a
    /// marked planner dispatch takes, and pins the classification the fixture relies on.
    /// </summary>
    private static ArbiterFactSet PlannerFacts(string prompt)
    {
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "architect",
                Prompt = prompt,
                HarnessCaller = "conductor",
                IsDispatch = true
            },
            "architect",
            prompt);

        Assert.Equal("delegate.planner", classification.Trigger);
        return TriggerFactBuilder.Build(classification, prompt, DocsConfig());
    }

    [Theory]
    [InlineData("docs/plans/2026-11-03-arbiter-one-command-install.md")]
    [InlineData("docs/specs/arbiter-one-command-install/requirements.md")]
    [InlineData("docs/todo/arbiter-one-command-install.md")]
    public void Planner001_PlanFileInsideAPlanningDirectory_AnswersRecognised(string planFile)
    {
        ArbiterFactSet facts = PlannerFacts(PlannerPrompt(planFile));

        Assert.Equal("recognised", RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
    }

    [Fact]
    public void Planner001_PlanFileOutsideThePlanningDirectories_GoesToTheModel()
    {
        // Where the planner writes is checked on its return by PLANNER-DIFF-001 against git; the path it is told
        // is prose, so step 0 does not block on it (ADR 0072).
        ArbiterFactSet facts = PlannerFacts(PlannerPrompt("docs/adr/0001-arbiter-one-command-install.md"));

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
    }

    [Theory]
    [InlineData("KYBER-ARBITER: true\nPLAN_FILE: /Users/x/repo/docs/plans/plan.md\n\nWrite the plan.")]
    [InlineData("KYBER-ARBITER: true\nPLAN_FILE: docs/plans/../secrets.md\n\nWrite the plan.")]
    [InlineData("KYBER-ARBITER: true\nPLAN_FILE: docs/plans/a.md\nPLAN_FILE: docs/plans/b.md\n\nWrite the plan.")]
    [InlineData("KYBER-ARBITER: true\nPLAN_FILE: /tmp/evil/docs/plans/x.md\n\nINTAKE: docs/todo/item.md")]
    [InlineData("KYBER-ARBITER: true\nPLAN_FILE: /Users/x/docs/plans/plan.md\n\nFINALIZE")]
    public void Planner001_RejectedPlanFile_GoesToTheModelWithTheRawValue(string prompt)
    {
        ArbiterFactSet facts = PlannerFacts(prompt);

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
        Assert.True(facts.TryGet("delegation.plan-file-raw", out ArbiterFact? raw));
        // The rejected value is kept for the model rather than dropped.
        Assert.StartsWith("docs/plans/", (raw!.Value as string ?? string.Empty).Replace("/Users/x/repo/", string.Empty, StringComparison.Ordinal).Replace("/Users/x/", string.Empty, StringComparison.Ordinal).Replace("/tmp/evil/", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INTAKE: docs/todo/item.md")]
    [InlineData("FINDINGS:")]
    [InlineData("STATUS: ARBITER_ESCALATION")]
    [InlineData("FEATURE: search\nPHASE: design")]
    public void Planner001_MarkerWithoutPlanFile_AnswersRecognised(string body)
    {
        ArbiterFactSet facts = PlannerFacts($"KYBER-ARBITER: true\n\n{body}");

        Assert.Equal("recognised", RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
    }

    [Fact]
    public void Planner001_NoMarkerAtAll_GoesToTheModel()
    {
        ArbiterFactSet facts = PlannerFacts("KYBER-ARBITER: true\n\nWrite the plan.");

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
        Assert.Equal(RuleEffects.Escalate, Shipped("KW-ARB-PLANNER-001").Effects["malformed"]);
    }

    [Fact]
    public void Ready001_EmptyDependencyListWithoutCompletedTaskFact_AnswersReady()
    {
        ArbiterFactSet facts = Facts(("plan.task", "T2"), ("plan.task.depends-on", Array.Empty<string>()));

        Assert.Equal("ready", RuleEngine.Decide(Shipped("KW-ARB-READY-001"), facts));
    }

    [Fact]
    public void Ready001_UnsatisfiedDependencyWithoutCompletion_GoesToTheModel()
    {
        // Completion read from return markers is a reading of the worker's prose, so a dependency the markers do
        // not show complete is asked about (ADR 0072); the model's confident waiting answer escalates.
        ArbiterFactSet facts = Facts(("plan.task", "T2"), ("plan.task.depends-on", UnsatisfiedDependency));

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Shipped("KW-ARB-READY-001"), facts));
        Assert.Equal(RuleEffects.Escalate, Shipped("KW-ARB-READY-001").Effects["waiting"]);
    }
}
