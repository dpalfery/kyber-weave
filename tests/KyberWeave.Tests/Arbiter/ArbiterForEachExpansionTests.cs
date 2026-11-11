using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.5: <c>RuleAsk.ForEach</c> over a list fact expands to one question per item.
/// RED: <c>ArbiterDecisionRule.Candidate</c> does not exist yet, so the record assertions cannot compile.
/// </summary>
public sealed class ArbiterForEachExpansionTests : IDisposable
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
    private const string RuleId = "KW-ARB-ASK-010";
    private static readonly string[] Files = ["src/a.cs", "src/b.cs", "src/c.cs"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-foreach-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    /// <summary>Records every batch it receives and answers each question within the task.</summary>
    private sealed class RecordingProvider : IArbiterProvider
    {
        private readonly List<int> _batchSizes = [];

        public IReadOnlyList<int> BatchSizes => _batchSizes;

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            _batchSizes.Add(rules.Count);
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => "within-task", StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
        }
    }

    /// <summary>Supplies the list fact the rule expands over.</summary>
    private sealed class FileListPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts.With("plan.task.files", Files, ArbiterFactLabel.Derived);
    }

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
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
            Target = "csharp-dev",
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

    private IReadOnlyList<ArbiterDecisionRecord> Records() => new DecisionLog(ArbiterDirectory).ReadAll();

    [Fact]
    public async Task ForEachOverListFact_ExpandsToOneQuestionPerItemInOneBatch()
    {
        RecordingProvider provider = new();
        ArbiterEvaluator evaluator = new(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            new PassThroughGitFacts(),
            new FileListPlanReader(),
            new FixedClock(new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await evaluator.EvaluateAsync(DelegateEvent(), ConfigWith([ForEachRule()]));

        Assert.Single(provider.BatchSizes);
        Assert.Equal(Files.Length, provider.BatchSizes[0]);
    }

    [Fact]
    public async Task ForEachEntries_EachRecordsItsCandidateInListOrder()
    {
        ArbiterEvaluator evaluator = new(
            new RecordingProvider(),
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            new PassThroughGitFacts(),
            new FileListPlanReader(),
            new FixedClock(new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero)));

        await evaluator.EvaluateAsync(DelegateEvent(), ConfigWith([ForEachRule()]));

        ArbiterDecisionRecord record = Assert.Single(Records());
        Assert.Equal(Files.Length, record.Rules.Count);
        Assert.Equal(Files, record.Rules.Select(entry => entry.Candidate));
        Assert.All(record.Rules, entry => Assert.Equal(RuleEffects.Allow, entry.Effect));
    }
}
