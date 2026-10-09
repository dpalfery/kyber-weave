using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.9: the single entry point running evaluation end to end.
/// RED: <see cref="ArbiterEvaluator"/> does not exist yet.
/// </summary>
public sealed class ArbiterEvaluatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-eval-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private sealed class CountingProvider : IArbiterProvider
    {
        public int CallCount { get; private set; }

        public Func<IReadOnlyList<ArbiterRule>, ArbiterFactSet, Dictionary<string, string>>? AnswerFor { get; init; }

        public Func<bool>? LedgerMustExist { get; init; }

        public bool SawLedgerFile { get; private set; }

        private readonly string _directory;

        public CountingProvider(string directory)
        {
            _directory = directory;
        }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (LedgerMustExist is not null && LedgerMustExist())
            {
                string ledgerPath = Path.Combine(_directory, InFlightLedger.FileName);
                SawLedgerFile = File.Exists(ledgerPath);
            }

            Dictionary<string, string> answers = AnswerFor is not null
                ? AnswerFor(rules, facts)
                : rules.Where(rule => rule.Ask is not null)
                    .ToDictionary(rule => rule.Id, rule => "within-task", StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, true));
        }
    }

    private sealed class ThrowingProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("provider is down");
        }
    }

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }

    private sealed class FailingGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            throw new InvalidOperationException("git facts exploded");
    }

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new() { Arbiter = new ArbiterConfig { Enabled = true, Rules = rules } };

    private static ArbiterEvent DelegateEvent(string prompt) =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "csharp-dev",
            Prompt = prompt,
            HarnessCaller = "conductor",
            IsDispatch = true,
        };

    private static ArbiterRule Step0Rule(string id, string trigger, string answer, string effect, bool alwaysMatch = true) =>
        new(
            id,
            trigger,
            "Step-0 question.",
            [answer],
            new Dictionary<string, string>(StringComparer.Ordinal) { [answer] = effect },
            [new RuleDecideClause(
                alwaysMatch
                    ? RulePredicate.Exists("caller", true)
                    : RulePredicate.Exists("missing-fact-xyz", true),
                answer)]);

    private static ArbiterRule AskRule(string id, string trigger, string answer, string effect) =>
        new(
            id,
            trigger,
            "Step-1 question.",
            [answer, "other"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [answer] = effect,
                ["other"] = effect,
            },
            null,
            new RuleAsk(
                "choice",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "delegation.task" },
                Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [answer] = "criterion",
                    ["other"] = "other criterion",
                }));

    private ArbiterEvaluator CreateEvaluator(
        IArbiterProvider provider,
        IArbiterGitFacts? gitFacts = null,
        IArbiterPlanReader? planReader = null,
        DateTimeOffset? now = null)
    {
        DateTimeOffset fixedNow = now ?? new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);
        return new ArbiterEvaluator(
            provider,
            _ => null,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            gitFacts ?? new PassThroughGitFacts(),
            planReader ?? new PassThroughPlanReader(),
            new FixedClock(fixedNow));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Evaluator_ConstructionTakesEveryCollaborator()
    {
        System.Reflection.ConstructorInfo? ctor = typeof(ArbiterEvaluator).GetConstructors().FirstOrDefault();
        Assert.NotNull(ctor);
        System.Reflection.ParameterInfo[] parameters = ctor!.GetParameters();
        string[] names = parameters.Select(parameter => parameter.Name ?? string.Empty).ToArray();
        Assert.Contains("provider", names);
        Assert.Contains("ledger", names);
        Assert.Contains("decisionLog", names);
        Assert.Contains("clock", names);
        Assert.Contains(parameters.Select(parameter => parameter.ParameterType), type => type == typeof(IArbiterProvider));
        Assert.Contains(parameters.Select(parameter => parameter.ParameterType), type => type == typeof(InFlightLedger));
        Assert.Contains(parameters.Select(parameter => parameter.ParameterType), type => type == typeof(DecisionLog));
        Assert.Contains(parameters.Select(parameter => parameter.ParameterType), type => type == typeof(TimeProvider));
    }

    [Fact]
    public async Task Evaluator_OneProviderCallPerTriggerRegardlessOfRuleCount()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        CountingProvider provider = new(ArbiterDirectory);
        ArbiterEvaluator evaluator = CreateEvaluator(provider);
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
            AskRule("KW-ARB-ASK-002", "delegate", "within-task", RuleEffects.Allow),
            AskRule("KW-ARB-ASK-003", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(DelegateEvent(Prompt), config);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(RuleEffects.Allow, result.Outcome);
    }

    [Fact]
    public async Task Evaluator_Step0ShortCircuitSkipsProviderCall()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        CountingProvider provider = new(ArbiterDirectory);
        ArbiterEvaluator evaluator = CreateEvaluator(provider);
        KyberWeaveConfig config = ConfigWith([
            Step0Rule("KW-ARB-SCOPE-001", "delegate", "not-in-plan", RuleEffects.Escalate),
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(DelegateEvent(Prompt), config);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RuleEffects.Escalate, result.Outcome);
    }

    [Fact]
    public async Task Evaluator_NoneReportsNotEvaluatedRatherThanUndecidable()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        ArbiterEvaluator evaluator = CreateEvaluator(new NoneProvider());
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(DelegateEvent(Prompt), config);

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        Assert.False(result.IsError);
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        Assert.Equal(ArbiterProviderStatuses.NotEvaluated, new DecisionLog(ArbiterDirectory).ReadAll()[0].Provider.Status);
    }

    [Fact]
    public async Task Evaluator_ProviderErrorEscalatesConductorButAllowsReview()
    {
        const string DelegatePrompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        const string LensPrompt = "KYBER-ARBITER: true\nLENS: test-lens\n\nChange.";
        ArbiterEvaluator conductorEvaluator = CreateEvaluator(new ThrowingProvider());
        KyberWeaveConfig conductorConfig = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);
        ArbiterEvaluationResult conductor = await conductorEvaluator.EvaluateAsync(
            DelegateEvent(DelegatePrompt), conductorConfig);
        Assert.Equal(RuleEffects.Escalate, conductor.Outcome);

        ArbiterEvaluator reviewEvaluator = CreateEvaluator(new ThrowingProvider());
        KyberWeaveConfig reviewConfig = ConfigWith([
            AskRule("KW-ARB-LENS-001", "lens.spawn", "applies", RuleEffects.Allow),
        ]);
        ArbiterEvent lensEvent = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = LensPrompt,
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
        };
        ArbiterEvaluationResult review = await reviewEvaluator.EvaluateAsync(lensEvent, reviewConfig);
        Assert.Equal(RuleEffects.Allow, review.Outcome);
    }

    [Fact]
    public async Task Evaluator_ExceptionIsNeverAllowAndCarriesHookError()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        ArbiterEvaluator evaluator = CreateEvaluator(new NoneProvider(), new FailingGitFacts());
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(DelegateEvent(Prompt), config);

        Assert.True(result.IsError);
        Assert.Equal("KW-ARB-HOOK-001", result.ErrorCode);
        Assert.NotEqual(RuleEffects.Allow, result.Outcome);
    }

    [Fact]
    public async Task Evaluator_LedgerBeforeDecisionAroundEvaluation()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        CountingProvider provider = new(ArbiterDirectory) { LedgerMustExist = () => true };
        ArbiterEvaluator evaluator = CreateEvaluator(provider);
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(DelegateEvent(Prompt), config);

        Assert.False(result.IsError);
        Assert.True(provider.SawLedgerFile, "ledger event is appended before evaluation");
        Assert.Single(new InFlightLedger(ArbiterDirectory).ReadAll());
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll());
        string ledgerText = await File.ReadAllTextAsync(Path.Combine(ArbiterDirectory, InFlightLedger.FileName));
        Assert.NotEmpty(ledgerText.Trim());
    }

    [Fact]
    public async Task Evaluator_DryRunReadsLedgerAndWritesNothing()
    {
        const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
        InFlightLedger ledger = new(ArbiterDirectory);
        DateTimeOffset at = new(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await ledger.AppendAsync(new ArbiterLedgerEvent(
            ArbiterRecordId.New(at), at, ArbiterSources.Hook, "claude", "s1", ArbiterLedgerPhases.Pre));
        int filesBefore = Directory.Exists(ArbiterDirectory)
            ? Directory.GetFiles(ArbiterDirectory).Length
            : 0;
        string ledgerBefore = await File.ReadAllTextAsync(Path.Combine(ArbiterDirectory, InFlightLedger.FileName));

        CountingProvider provider = new(ArbiterDirectory);
        ArbiterEvaluator evaluator = CreateEvaluator(provider);
        KyberWeaveConfig config = ConfigWith([
            AskRule("KW-ARB-ASK-001", "delegate", "within-task", RuleEffects.Allow),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            DelegateEvent(Prompt), config, new ArbiterEvaluatorOptions(DryRun: true));

        Assert.False(result.IsError);
        Assert.Equal(ledgerBefore, await File.ReadAllTextAsync(Path.Combine(ArbiterDirectory, InFlightLedger.FileName)));
        Assert.Equal(filesBefore, Directory.GetFiles(ArbiterDirectory).Length);
        Assert.False(File.Exists(Path.Combine(ArbiterDirectory, DecisionLog.FileName)));
    }

    [Fact]
    public async Task Evaluator_AnyEscalatingDispatchEscalatesEvent()
    {
        ArbiterEvent multi = new()
        {
            Harness = "claude",
            Phase = "pre",
            HarnessCaller = "conductor",
            IsDispatch = true,
            Dispatches =
            [
                new ArbiterDispatch("csharp-dev", "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement A."),
                new ArbiterDispatch("csharp-dev", "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement B."),
            ],
        };
        CountingProvider provider = new(ArbiterDirectory)
        {
            AnswerFor = (rules, facts) => rules.ToDictionary(
                rule => rule.Id,
                rule => string.Equals(rule.Id, "KW-ARB-ASK-001", StringComparison.Ordinal)
                    ? "adds-work"
                    : "within-task",
                StringComparer.Ordinal),
        };
        ArbiterEvaluator evaluator = CreateEvaluator(provider);
        KyberWeaveConfig config = ConfigWith([
            new ArbiterRule(
                "KW-ARB-ASK-001",
                "delegate",
                "Step-1 question.",
                ["within-task", "adds-work"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["within-task"] = RuleEffects.Allow,
                    ["adds-work"] = RuleEffects.Escalate,
                },
                null,
                new RuleAsk(
                    "choice",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "delegation.task" },
                    Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["within-task"] = "criterion",
                        ["adds-work"] = "criterion",
                    })),
        ]);

        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(multi, config);

        Assert.Equal(2, result.Dispatches.Count);
        Assert.Equal(RuleEffects.Escalate, result.Outcome);
    }
}
