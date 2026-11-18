using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A rule loaded from a technology folder judges only an event whose task is in that technology or in a
/// technology that builds on it. Role folder rules and <c>arbiter.rules</c> judge every event, and an event
/// that names no task technology is judged by them alone.
/// </summary>
public sealed class ArbiterTechnologyRuleScopeTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ASqlServerRuleDoesNotJudgeACsharpTask()
    {
        (string outcome, IReadOnlyList<ArbiterDecisionRecord> records) =
            Evaluate([Escalating("sql-server-scope-001", "sql-server")], Prompt("csharp"));

        Assert.Equal(RuleEffects.Allow, outcome);
        Assert.DoesNotContain(
            records,
            record => record.Rules.Any(rule => string.Equals(rule.Id, "sql-server-scope-001", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASqlServerRuleJudgesASqlServerTask()
    {
        (string outcome, IReadOnlyList<ArbiterDecisionRecord> records) =
            Evaluate([Escalating("sql-server-scope-001", "sql-server")], Prompt("sql-server"));

        Assert.Equal(RuleEffects.Escalate, outcome);
        Assert.Contains(
            records,
            record => record.Rules.Any(rule => string.Equals(rule.Id, "sql-server-scope-001", StringComparison.Ordinal)));
    }

    [Fact]
    public void AParentsRuleJudgesItsChildsTask()
    {
        (string outcome, _) = Evaluate(
            [Escalating("typescript-scope-001", "typescript")],
            Prompt("react"),
            parents: new Dictionary<string, string>(StringComparer.Ordinal) { ["react"] = "typescript" });

        Assert.Equal(RuleEffects.Escalate, outcome);
    }

    [Fact]
    public void AChildsRuleDoesNotJudgeItsParentsTask()
    {
        (string outcome, _) = Evaluate(
            [Escalating("react-scope-001", "react")],
            Prompt("typescript"),
            parents: new Dictionary<string, string>(StringComparer.Ordinal) { ["react"] = "typescript" });

        Assert.Equal(RuleEffects.Allow, outcome);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("sql-server")]
    [InlineData(null)]
    public void ARoleFolderRuleJudgesEveryEvent(string? technology)
    {
        (string outcome, _) = Evaluate([Escalating("host-role-001")], Prompt(technology));

        Assert.Equal(RuleEffects.Escalate, outcome);
    }

    [Fact]
    public void AnEventWithNoTaskTechnologyGetsNoTechnologyFolderRule()
    {
        (string outcome, _) = Evaluate([Escalating("sql-server-scope-001", "sql-server")], Prompt(null));

        Assert.Equal(RuleEffects.Allow, outcome);
    }

    [Fact]
    public void TheTasksOwnTechnologyIsUsedWhenTheDispatchNamesNoHeader()
    {
        (string outcome, _) = Evaluate(
            [Escalating("sql-server-scope-001", "sql-server")],
            Prompt(null),
            planFacts: facts => facts.With("plan.task.technology", "sql-server", ArbiterFactLabel.Derived));

        Assert.Equal(RuleEffects.Escalate, outcome);
    }

    [Fact]
    public void TheTasksOwnTechnologyOutranksATechnologyHeader()
    {
        (string outcome, _) = Evaluate(
            [Escalating("sql-server-scope-001", "sql-server")],
            Prompt("csharp"),
            planFacts: facts => facts.With("plan.task.technology", "sql-server", ArbiterFactLabel.Derived));

        Assert.Equal(RuleEffects.Escalate, outcome);
    }

    /// <summary>
    /// The shipped catalogue carries no technology tag, so when the host adds no folder every one of the 36
    /// rules still loads and judges every event, exactly as before this scoping exists.
    /// </summary>
    [Fact]
    public void TheShipped36RulesJudgeExactlyAsBeforeWhenTheHostAddsNoFile()
    {
        IReadOnlyList<ArbiterRule> shipped = ArbiterConfigDefaults.Product.Rules;

        Assert.Equal(36, shipped.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(shipped, rule => Assert.Null(rule.Technology));
        Assert.Equal(
            shipped,
            ArbiterRuleScopes.Applicable(
                shipped,
                "csharp",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Equal(
            shipped,
            ArbiterRuleScopes.Applicable(shipped, "sql-server", new Dictionary<string, string>(StringComparer.Ordinal)));

        string[] ids = [.. shipped.Select(rule => rule.Id).Distinct(StringComparer.Ordinal)];
        Assert.Equal(21, ids.Count(id => !id.StartsWith("KW-ARB-PLANNER", StringComparison.Ordinal)
                                        && !ReviewerRuleIds.Contains(id)));
        Assert.Equal(2, ids.Count(id => id.StartsWith("KW-ARB-PLANNER", StringComparison.Ordinal)));
        Assert.Equal(13, ids.Count(ReviewerRuleIds.Contains));
    }

    private static readonly HashSet<string> ReviewerRuleIds = new(StringComparer.Ordinal)
    {
        "KW-ARB-LENS-001",
        "KW-ARB-LENS-002",
        "KW-ARB-QUOTE-001",
        "KW-ARB-PREEX-001",
        "KW-ARB-CLAIM-001",
        "KW-ARB-GATE-CORROBORATED-001",
        "KW-ARB-GATE-001",
        "KW-ARB-NITPICK-001",
        "KW-ARB-NITPICK-002",
        "KW-ARB-DUP-001",
        "KW-ARB-COMMENT-001",
        "KW-ARB-SEVERITY-001",
        "KW-ARB-OVERRIDE-001",
    };

    /// <summary>A <c>delegate</c> rule that escalates whenever it judges the event at all.</summary>
    private static ArbiterRule Escalating(string id, string? technology = null) =>
        new(
            id,
            "delegate",
            "Does this rule judge the dispatch?",
            ["yes"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["yes"] = RuleEffects.Escalate },
            [new RuleDecideClause(RulePredicate.Exists("delegation.target", expected: true), "yes")],
            Technology: technology);

    /// <summary>The fixture's delegate prompt, with a <c>TECHNOLOGY</c> header when one is named.</summary>
    private static string Prompt(string? technology) =>
        technology is null
            ? ArbiterEvaluatorFixtures.Prompt
            : $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\nTECHNOLOGY: {technology}\n\nImplement.";

    private (string Outcome, IReadOnlyList<ArbiterDecisionRecord> Records) Evaluate(
        IReadOnlyList<ArbiterRule> rules,
        string prompt,
        IReadOnlyDictionary<string, string>? parents = null,
        Func<ArbiterFactSet, ArbiterFactSet>? planFacts = null)
    {
        KyberWeaveConfig config = new()
        {
            Arbiter = new ArbiterConfig
            {
                Enabled = true,
                Rules = rules,
                RuleFolderParents = parents ?? new Dictionary<string, string>(StringComparer.Ordinal),
            },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };
        ArbiterEvaluator evaluator = ArbiterEvaluatorFixtures.Evaluator(
            _root.Path,
            new ArbiterEvaluatorFixtures.ScriptedProvider(),
            planReader: new ArbiterEvaluatorFixtures.PlanFacts(planFacts));
        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "dev-medium",
            Prompt = prompt,
            HarnessCaller = "conductor",
            IsDispatch = true,
        };

        ArbiterEvaluationResult result = evaluator.EvaluateAsync(ev, config).GetAwaiter().GetResult();
        return (result.Outcome, ArbiterEvaluatorFixtures.Records(_root.Path));
    }
}
