using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Shared fakes for the evaluator tests: a scripted step-1 provider that records every question it
/// receives with the facts it was sent, pass-through ports, a fixed clock, and rule builders.
/// Every test drives the real <see cref="ArbiterEvaluator"/> path through these.
/// </summary>
internal static class ArbiterEvaluatorFixtures
{
    public const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";

    public static readonly DateTimeOffset FixedNow = new(2025, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static string DirectoryFor(string root) => Path.Combine(root, "artifacts", "arbiter");

    /// <summary>Answers each question by rule id, records every question with the facts it was sent.</summary>
    public sealed class ScriptedProvider : IArbiterProvider
    {
        private readonly List<ArbiterRule> _asked = [];
        private readonly List<ArbiterFactSet> _factsSent = [];

        /// <summary>Every question the provider received, in order, including repeats of one rule id.</summary>
        public IReadOnlyList<ArbiterRule> Asked => _asked;

        /// <summary>The facts set sent with each batch, in batch order.</summary>
        public IReadOnlyList<ArbiterFactSet> FactsSent => _factsSent;

        /// <summary>The answer per rule id. A rule id absent here is not answered.</summary>
        public IReadOnlyDictionary<string, string> AnswerByRuleId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            _asked.AddRange(rules);
            _factsSent.Add(facts);
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (AnswerByRuleId.TryGetValue(rule.Id, out string? answer))
                {
                    answers[rule.Id] = answer;
                }
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
        }
    }

    /// <summary>Passes git facts through unchanged.</summary>
    public sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    /// <summary>Adds plan facts through a caller-supplied function, or passes them through.</summary>
    public sealed class PlanFacts(Func<ArbiterFactSet, ArbiterFactSet>? enrich = null) : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            enrich is null ? facts : enrich(facts);
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    public static ArbiterEvent DelegateEvent(string target = "csharp-dev") =>
        new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = target,
            Prompt = Prompt,
            HarnessCaller = "conductor",
            IsDispatch = true,
        };

    /// <summary>Builds the real evaluator over the given fakes and the on-disk stores under <paramref name="root"/>.</summary>
    public static ArbiterEvaluator Evaluator(
        string root,
        IArbiterProvider provider,
        Func<string, ArbiterEvent, IReadOnlyList<string>>? secondaryTriggers = null,
        PlanFacts? planReader = null) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(DirectoryFor(root)),
            new DecisionLog(DirectoryFor(root)),
            new PassThroughGitFacts(),
            planReader ?? new PlanFacts(),
            new Clock(FixedNow),
            secondaryTriggers);

    public static IReadOnlyList<ArbiterDecisionRecord> Records(string root) =>
        new DecisionLog(DirectoryFor(root)).ReadAll();

    /// <summary>A step-1 rule with one answer that maps to <paramref name="effect"/> and a second answer that never matches.</summary>
    public static ArbiterRule AskRule(string id, string trigger, string answer, string effect, bool shadow = false) =>
        new ArbiterRule(
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
                }))
        with
        { Shadow = shadow };

    /// <summary>A step-1 rule that asks once per item of <paramref name="listFact"/>, binding each item to <c>candidate.file</c>.</summary>
    public static ArbiterRule ForEachRule(string id, string trigger, string listFact, string answer, string effect) =>
        new ArbiterRule(
            id,
            trigger,
            "Step-1 question per item.",
            [answer, "other"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [answer] = effect,
                ["other"] = effect,
            },
            null,
            new RuleAsk(
                "choice",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["file"] = "candidate.file" },
                Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [answer] = "criterion",
                    ["other"] = "other criterion",
                },
                ForEach: listFact));
}
