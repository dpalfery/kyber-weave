using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for what the hook host receives from the production <see cref="ArbiterHookDecisionEngine"/>:
/// an answered shortcut withholds its worker with the substitute digest, a dropped finding is set aside with one
/// line per dropped finding, a duplicate refutation is denied with its duplicate line, and a shadow rule allows.
/// </summary>
/// <remarks>
/// The engine runs end to end against a temp repository with a scripted provider. Each assertion computes its
/// expected text from the decision record the engine wrote, so a failure shows the text the host would have seen.
/// </remarks>
public sealed class ArbiterHookOutcomeTests : IDisposable
{
    private const string TriageRule = "KW-ARB-TRIAGE-001";
    private const string NitpickRule = "KW-ARB-NITPICK-001";
    private const string DuplicateRule = "KW-ARB-DUP-001";
    private const string TriageTrigger = "triage.shortcut";
    private const string Trivial = "trivial";
    private const string Nitpick = "nitpick";
    private const string SameDefect = "same-defect";
    private const string ReviewRun = "RUN-HOOK";
    private const string LensPrompt = "KYBER-ARBITER: true\nLENS: correctness\nREVIEW_RUN: " + ReviewRun + "\n\nApply the lens.";
    private const string TwoNitpicks =
        "id: nit-1\nseverity: Minor\nfile: src/Widget.cs\nline: 12\nclaim: naming\n" +
        "id: nit-2\nseverity: Minor\nfile: src/Widget.cs\nline: 40\nclaim: naming";

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>Answers every step-1 question with one answer and one confidence, so each finding's answer is the same.</summary>
    private sealed class UniformProvider(string answer, double confidence, double floor) : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => new ArbiterStep1Score(null, confidence, floor), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true, scores));
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

    private static KyberWeaveConfig ConfigWith(IReadOnlyList<ArbiterRule> rules) =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = rules },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private static ArbiterRule Shipped(string id) =>
        HookAskedRules.AsHookAsked(Raw(id));

    /// <summary>The rule exactly as shipped, before <see cref="HookAskedRules.AsHookAsked"/>.</summary>
    private static ArbiterRule Raw(string id) =>
        Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    private static ArbiterRule Enforced(string id) => Shipped(id) with { Shadow = false };

    private ArbiterHookDecisionEngine Engine(IArbiterProvider provider) => new(
        providerFactory: _ => provider,
        gitFactsFactory: _ => new PassThroughGitFacts(),
        planReaderFactory: _ => new PassThroughPlanReader(),
        homeDirectory: () => _repo.Path);

    private HookContext Context() => new(_repo.Path, () => "decision-hook", TextWriter.Null);

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() => new DecisionLog(ArbiterDirectory).ReadAll();

    private static string Marked(string body) => "KYBER-ARBITER: true\n\n" + body;

    private static string Refute(string lensId, string block) =>
        "KYBER-ARBITER: true\nREVIEW_RUN: " + ReviewRun + "\nREFUTE: " + lensId + "\n\n" + block;

    // ---- Answer: an answered shortcut withholds its worker with the substitute digest ----

    /// <summary>An enforced triage answer denies the investigator with the exact digest the caller takes as the result.</summary>
    [Fact]
    public void AnEnforcedTriageAnswerDeniesTheWorkerWithItsSubstituteDigest()
    {
        HookOutcome outcome = Engine(new UniformProvider(Trivial, 0.95, 0.9)).DecidePreDispatch(
            "claude",
            "bug-crusher",
            "bug-crusher-investigator",
            Marked("Find the single site that throws."),
            ConfigWith([Enforced(TriageRule)]),
            Context());

        ArbiterDecisionRecord answered = Assert.Single(Decisions(), record => record.Trigger == TriageTrigger);
        Assert.Equal(HookOutcomeKind.Deny, outcome.Kind);
        Assert.Equal(SubstituteDigests.Triage(answered.Id), outcome.Reason);
    }

    /// <summary>A shipped (shadow) triage answer allows the investigator to run, with no reason and no denial.</summary>
    [Fact]
    public void AShadowTriageAnswerAllowsTheInvestigatorUnchanged()
    {
        HookOutcome outcome = Engine(new UniformProvider(Trivial, 0.95, 0.9)).DecidePreDispatch(
            "claude",
            "bug-crusher",
            "bug-crusher-investigator",
            Marked("Find the single site that throws."),
            ConfigWith([Shipped(TriageRule)]),
            Context());

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    // ---- Drop: a dropped finding is set aside, and the host is told about every drop ----

    /// <summary>An enforced drop tells the host, after the return, one line for each dropped finding, each naming its decision.</summary>
    [Fact]
    public void AnEnforcedDropNamesEveryDroppedFindingInItsPostDispatchLine()
    {
        HookOutcome outcome = Engine(new UniformProvider(Nitpick, 0.9, 0.85)).DecidePostDispatch(
            "claude",
            "code-reviewer",
            "review-lens",
            LensPrompt,
            TwoNitpicks,
            ConfigWith([Enforced(NitpickRule)]),
            Context());

        ArbiterDecisionRecord record = Assert.Single(Decisions(), r => r.Trigger == "lens.returned");
        Assert.Equal(HookOutcomeKind.PostAnnotation, outcome.Kind);
        Assert.Equal(
            "ARBITER_DROPPED: nit-1 rule=" + NitpickRule + " decision=" + record.Id + "\n" +
            "ARBITER_DROPPED: nit-2 rule=" + NitpickRule + " decision=" + record.Id,
            outcome.Reason);
    }

    /// <summary>A shipped (shadow) nitpick rule drops nothing: the host gets the return untouched.</summary>
    [Fact]
    public void AShadowNitpickRuleAnnotatesNothing()
    {
        HookOutcome outcome = Engine(new UniformProvider(Nitpick, 0.9, 0.85)).DecidePostDispatch(
            "claude",
            "code-reviewer",
            "review-lens",
            LensPrompt,
            TwoNitpicks,
            ConfigWith([Shipped(NitpickRule)]),
            Context());

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    // ---- Duplicate: a refutation of a repeated finding is denied with its duplicate line ----

    /// <summary>An enforced duplicate denies the refuter with the duplicate line, naming both findings and the decision.</summary>
    [Fact]
    public async Task AnEnforcedDuplicateDeniesTheRefuterWithItsDuplicateLine()
    {
        ArbiterRunStore store = new(ArbiterDirectory, null, () => DateTimeOffset.UtcNow);
        await store.AppendFindingAsync(ReviewRun, "id: nit-1\nfile: src/Widget.cs\nline: 8\nclaim: naming\nstatus: kept");

        HookOutcome outcome = Engine(new UniformProvider(SameDefect, 0.95, 0.9)).DecidePreDispatch(
            "claude",
            "code-reviewer",
            "review-lens",
            Refute("correctness/nit-2", "id: nit-2\nfile: src/Widget.cs\nline: 12\nclaim: naming"),
            ConfigWith([Enforced(DuplicateRule)]),
            Context());

        ArbiterDecisionRecord record = Assert.Single(Decisions(), r => r.Trigger == "refute.spawn");
        Assert.Equal(HookOutcomeKind.Deny, outcome.Kind);
        Assert.Equal("ARBITER_DUPLICATE: nit-2 of nit-1 decision=" + record.Id, outcome.Reason);
    }

    /// <summary>A shipped (shadow) duplicate rule logs the duplicate and lets the refuter run.</summary>
    [Fact]
    public async Task AShadowDuplicateLetsTheRefuterRun()
    {
        ArbiterRunStore store = new(ArbiterDirectory, null, () => DateTimeOffset.UtcNow);
        await store.AppendFindingAsync(ReviewRun, "id: nit-1\nfile: src/Widget.cs\nline: 8\nclaim: naming\nstatus: kept");

        HookOutcome outcome = Engine(new UniformProvider(SameDefect, 0.95, 0.9)).DecidePreDispatch(
            "claude",
            "code-reviewer",
            "review-lens",
            Refute("correctness/nit-2", "id: nit-2\nfile: src/Widget.cs\nline: 12\nclaim: naming"),
            ConfigWith([Shipped(DuplicateRule)]),
            Context());

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Null(outcome.Reason);
    }
}
