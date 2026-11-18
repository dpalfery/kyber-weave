using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// Contract for task 1.5: <c>RuleAsk.ForEach</c> over a list fact expands to one question per item.
/// RED: <c>ArbiterDecisionRule.Candidate</c> does not exist yet, so the record assertions cannot compile.
/// </summary>
public sealed class ArbiterForEachExpansionTests
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
    private const string RuleId = "KW-ARB-ASK-010";
    private static readonly string[] Files = ["src/a.cs", "src/b.cs", "src/c.cs"];

    private readonly InMemoryArbiterState _arbiter = new();

    /// <summary>Supplies the list fact the rule expands over.</summary>
    private sealed class FileListPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts.With("plan.task.files", Files, ArbiterFactLabel.Derived);
    }


    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private static ArbiterEvent DelegateEvent() =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "dev-medium",
            Prompt = Prompt,
            HarnessCaller = "conductor",
            IsDispatch = true,
        };

    /// <summary>A step-1 rule that asks once per item of <c>plan.task.files</c>, binding each item to <c>candidate.file</c>.</summary>
    private static ArbiterRule ForEachRule() =>
        new(
            RuleId,
            "delegate",
            "Is this file in scope?",
            ["within-task", "other"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["within-task"] = RuleEffects.Allow,
                ["other"] = RuleEffects.Escalate,
            },
            null,
            new RuleAsk(
                "choice",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["file"] = "candidate.file" },
                Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["within-task"] = "criterion",
                    ["other"] = "other criterion",
                },
                ForEach: "plan.task.files"));

    private IReadOnlyList<ArbiterDecisionRecord> Records() => _arbiter.Records;

    /// <summary>The provider these tests use: it answers every asked rule and reports no confidence.</summary>
    private static FakeArbiterProvider Recording() => new() { AnswerAsked = "within-task" };

    [Fact]
    public async Task ForEachOverListFact_ExpandsToOneQuestionPerItemInOneBatch()
    {
        FakeArbiterProvider provider = Recording();
        ArbiterEvaluator evaluator = _arbiter.Evaluator(
            provider,
            planReader: new FileListPlanReader(),
            clock: new FixedClock(new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await evaluator.EvaluateAsync(DelegateEvent(), ConfigWith([ForEachRule()]));

        Assert.Single(provider.BatchSizes);
        Assert.Equal(Files.Length, provider.BatchSizes[0]);
    }

    [Fact]
    public async Task ForEachEntries_EachRecordsItsCandidateInListOrder()
    {
        ArbiterEvaluator evaluator = _arbiter.Evaluator(
            Recording(),
            planReader: new FileListPlanReader(),
            clock: new FixedClock(new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await evaluator.EvaluateAsync(DelegateEvent(), ConfigWith([ForEachRule()]));

        ArbiterDecisionRecord record = Assert.Single(Records());
        Assert.Equal(Files.Length, record.Rules.Count);
        Assert.Equal(Files, record.Rules.Select(entry => entry.Candidate));
        Assert.All(record.Rules, entry => Assert.Equal(RuleEffects.Allow, entry.Effect));
    }

    /// <summary>
    /// The provider in these tests reports no confidence, so the decision record must not claim one: a score the
    /// removed double never sent would put the answer on a path the test does not mean to exercise.
    /// </summary>
    [Fact]
    public async Task AnAnswerFromAProviderThatReportsNoConfidenceRecordsNone()
    {
        FakeArbiterProvider provider = Recording();
        ArbiterEvaluator evaluator = _arbiter.Evaluator(
            provider,
            planReader: new FileListPlanReader(),
            clock: new FixedClock(new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await evaluator.EvaluateAsync(DelegateEvent(), ConfigWith([ForEachRule()]));

        Assert.All(Records().SelectMany(record => record.Rules), rule => Assert.Null(rule.Confidence));
    }
}
