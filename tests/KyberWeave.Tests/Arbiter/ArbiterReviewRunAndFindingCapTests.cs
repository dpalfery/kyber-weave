using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review-run and finding-cap contracts for task 3.19-F2 and task 3.21 (RED, src work pending).
/// (a) A lens return with no <c>REVIEW_RUN</c> header still judges its findings, persists nothing, and the decision
/// record carries a visible note with the reason <c>review-run-missing</c>. A <c>REVIEW_RUN</c> value with unusual
/// characters or length is accepted and keys its own store.
/// (b) A lens return with more findings than <see cref="StateBudget.MaxQuestions"/> judges only the first
/// <c>MaxQuestions</c>, keeps the rest without dropping them, and the decision record notes how many were not judged
/// under the reason <c>findings-over-cap</c>.
/// </summary>
/// <remarks>
/// Names the GREEN side must match: the note is an <see cref="ArbiterDecisionRule.Evidence"/> line on a rule of the
/// <c>lens.returned</c> record that contains the reason word. The tests pin the reason word and the stored outcome,
/// not the digest path of a store. Store keys are read back through <see cref="ArbiterRunStore.ReadFindings"/>.
/// </remarks>
public sealed class ArbiterReviewRunAndFindingCapTests : IDisposable
{
    private const string NitpickRule = "KW-ARB-NITPICK-001";
    private const string Nitpick = "nitpick";
    private const string Substantive = "substantive";
    private const string ReviewRun = "RUN-CAP";
    private const string OtherRun = "RUN-OTHER";
    private const string MinorFinding = "id: nit-1\nseverity: Minor\nfile: src/Widget.cs\nline: 12\nclaim: naming";
    private const string ReviewRunMissing = "review-run-missing";
    private const string FindingsOverCap = "findings-over-cap";
    private const string LensTrigger = "lens.returned";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-review-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers every step-1 question with one answer per finding id, and counts the requests made.</summary>
    private sealed class PerFindingAnswers(string answerForAll) : IArbiterProvider
    {
        public int CallCount { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => answerForAll, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => new ArbiterStep1Score(null, 0.9, 0.85), StringComparer.Ordinal);
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig
        {
            Enabled = true,
            Rules = ArbiterConfig.ProductDefaults.Rules
                .Select(HookAskedRules.AsHookAsked)
                .Select(rule => string.Equals(rule.Id, NitpickRule, StringComparison.Ordinal) ? rule with { Shadow = false } : rule)
                .ToList(),
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static ArbiterEvaluator Evaluator(string arbiterDirectory, IArbiterProvider provider) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FixedClock(Now),
            (_, _) => []);

    /// <summary>A lens return whose prompt carries the given header line (empty for none) and whose output is the findings.</summary>
    private static ArbiterEvent LensReturn(string headerLine, string findings) =>
        new()
        {
            Harness = "claude",
            Phase = "return",
            Target = "review-lens",
            Prompt = "KYBER-ARBITER: true\nLENS: correctness\n" + headerLine + "\nApply the lens.",
            HarnessCaller = "code-reviewer",
            IsDispatch = true,
            Session = "session-1",
            ToolOutput = findings,
        };

    private static ArbiterEvent LensReturnWithRun(string reviewRun, string findings) =>
        LensReturn("REVIEW_RUN: " + reviewRun, findings);

    private static string Finding(int number) =>
        $"id: nit-{number}\nseverity: Minor\nfile: src/Widget.cs\nline: {number}\nclaim: naming";

    private IReadOnlyList<ArbiterDecisionRecord> LensRecords() =>
        new DecisionLog(ArbiterDirectory).ReadAll().Where(record => record.Trigger == LensTrigger).ToList();

    /// <summary>Every evidence line of every rule on a record, joined so a test can search the notes it carries.</summary>
    private static string NotesOf(ArbiterDecisionRecord record) =>
        string.Join("\n", record.Rules.Select(rule => rule.Evidence ?? string.Empty));

    // ---- (a) REVIEW_RUN absent, and REVIEW_RUN values with unusual characters ----

    /// <summary>
    /// A lens return with no review run is still judged, nothing is persisted, and the record says why the store was
    /// not read. Failure this guards: the return is silently judged with no trace of the missing header, so the operator
    /// cannot tell a review that lost its store from one that never had a findings store to lose.
    /// </summary>
    [Fact]
    public async Task ALensReturnWithNoReviewRunHeaderIsJudgedAndNotedWithoutPersisting()
    {
        PerFindingAnswers provider = new(Nitpick);

        await Evaluator(ArbiterDirectory, provider).EvaluateAsync(LensReturn(string.Empty, MinorFinding), Config());

        ArbiterDecisionRecord record = Assert.Single(LensRecords());
        Assert.Contains(ReviewRunMissing, NotesOf(record), StringComparison.Ordinal);
        Assert.Contains(record.Rules, rule => rule.Id == NitpickRule && rule.Answer == Nitpick);
        Assert.True(provider.CallCount > 0, "the finding must still be judged when the review run is missing");
        Assert.False(
            Directory.Exists(Path.Combine(ArbiterDirectory, "stores")),
            "with no review run there is no review to persist into, so no store is written");
    }

    /// <summary>
    /// A review run name the header accepts is stored under its own key and read back by that name. Failure this
    /// guards: a value with spaces is rejected by the header parser, so the review loses its store without a note.
    /// </summary>
    [Fact]
    public async Task AReviewRunOfFiveHundredCharactersIsAcceptedAndKeysItsOwnStore()
    {
        string longRun = new('r', 500);

        await AssertStoredUnderOwnKeyAsync(longRun);
    }

    /// <summary>A review run containing a path traversal sequence is a plain key: it never names a path outside the stores.</summary>
    [Fact]
    public async Task AReviewRunContainingDotDotSlashIsAcceptedAndKeysItsOwnStore()
    {
        await AssertStoredUnderOwnKeyAsync("../x");
    }

    /// <summary>A review run with spaces in it is accepted and keys its own store.</summary>
    [Fact]
    public async Task AReviewRunContainingSpacesIsAcceptedAndKeysItsOwnStore()
    {
        await AssertStoredUnderOwnKeyAsync("run with spaces");
    }

    /// <summary>The same review run always reads the same store; a different one reads a different store.</summary>
    [Fact]
    public async Task TheSameReviewRunSharesOneStoreAndADifferentOneDoesNot()
    {
        PerFindingAnswers provider = new(Substantive);
        ArbiterEvaluator evaluator = Evaluator(ArbiterDirectory, provider);

        await evaluator.EvaluateAsync(LensReturnWithRun(ReviewRun, MinorFinding), Config());
        await evaluator.EvaluateAsync(LensReturnWithRun(ReviewRun, MinorFinding), Config());

        ArbiterRunStore store = new(ArbiterDirectory, null, () => Now);
        Assert.Equal(2, store.ReadFindings(ReviewRun).Count);
        Assert.Empty(store.ReadFindings(OtherRun));
    }

    private async Task AssertStoredUnderOwnKeyAsync(string reviewRun)
    {
        PerFindingAnswers provider = new(Substantive);

        await Evaluator(ArbiterDirectory, provider).EvaluateAsync(LensReturnWithRun(reviewRun, MinorFinding), Config());

        ArbiterRunStore store = new(ArbiterDirectory, null, () => Now);
        IReadOnlyList<string> stored = store.ReadFindings(reviewRun);
        string item = Assert.Single(stored);
        Assert.Contains("nit-1", item, StringComparison.Ordinal);
        Assert.Contains("status: kept", item, StringComparison.Ordinal);
    }

    // ---- (b) more findings than the question budget ----

    /// <summary>
    /// A return with more findings than the budget judges only the first <see cref="StateBudget.MaxQuestions"/>. The
    /// rest are kept, never dropped, and the record says how many were not judged. Failure this guards: a long return
    /// is either asked in full (over budget) or silently cut, with no record of the findings that were never judged.
    /// </summary>
    [Fact]
    public async Task AReturnOverTheQuestionCapJudgesOnlyTheFirstAndKeepsTheRest()
    {
        int total = StateBudget.MaxQuestions + 3;
        string findings = string.Join("\n", Enumerable.Range(1, total).Select(Finding));
        PerFindingAnswers provider = new(Nitpick);

        await Evaluator(ArbiterDirectory, provider).EvaluateAsync(LensReturnWithRun(ReviewRun, findings), Config());

        Assert.Equal(StateBudget.MaxQuestions, provider.CallCount);
        IReadOnlyList<string> stored = new ArbiterRunStore(ArbiterDirectory, null, () => Now).ReadFindings(ReviewRun);
        Assert.Equal(total, stored.Count);
        Assert.EndsWith("status: dropped", stored[0], StringComparison.Ordinal);
        for (int index = StateBudget.MaxQuestions; index < total; index++)
        {
            Assert.EndsWith("status: kept", stored[index], StringComparison.Ordinal);
        }

        ArbiterDecisionRecord record = Assert.Single(LensRecords());
        string notes = NotesOf(record);
        Assert.Contains(FindingsOverCap, notes, StringComparison.Ordinal);
        Assert.Contains("3", notes, StringComparison.Ordinal);
    }
}
