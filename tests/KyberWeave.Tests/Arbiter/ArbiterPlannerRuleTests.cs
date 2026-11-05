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
    public void Planner001_PlanFileOutsideThePlanningDirectories_AnswersMalformed()
    {
        ArbiterFactSet facts = PlannerFacts(PlannerPrompt("docs/adr/0001-arbiter-one-command-install.md"));

        Assert.Equal("malformed", RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
    }

    [Fact]
    public void Planner001_MarkedDispatchWithoutPlanFile_AnswersMalformed()
    {
        const string Prompt = "KYBER-ARBITER: true\n\nINTAKE: docs/todo/item.md";
        ArbiterFactSet facts = PlannerFacts(Prompt);

        Assert.Equal("malformed", RuleEngine.Decide(Shipped("KW-ARB-PLANNER-001"), facts));
    }

    [Fact]
    public void Ready001_EmptyDependencyListWithoutCompletedTaskFact_AnswersReady()
    {
        ArbiterFactSet facts = Facts(("plan.task.depends-on", Array.Empty<string>()));

        Assert.Equal("ready", RuleEngine.Decide(Shipped("KW-ARB-READY-001"), facts));
    }

    [Fact]
    public void Ready001_UnsatisfiedDependencyWithoutCompletion_AnswersWaiting()
    {
        ArbiterFactSet facts = Facts(("plan.task.depends-on", UnsatisfiedDependency));

        Assert.Equal("waiting", RuleEngine.Decide(Shipped("KW-ARB-READY-001"), facts));
    }
}
