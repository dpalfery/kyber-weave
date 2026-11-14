using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Review;

/// <summary>One subject put to a decision-model rule: what it is about and the fact values its question reads.</summary>
/// <param name="Subject">What the question is about, as the log and the report name it: a lens or a finding id.</param>
/// <param name="Facts">The value of each fact the rule's declared state reads, by fact name.</param>
/// <param name="Lens">For a rule whose instructions come from a lens, the lens whose text they are.</param>
public sealed record ReviewQuestion(string Subject, IReadOnlyDictionary<string, object?> Facts, string? Lens = null);

/// <summary>What the decision model answered for one subject.</summary>
/// <param name="Answer">The rule's answer name, or <c>undecidable</c> when nothing confident came back.</param>
/// <param name="Score">The confidence of a choice, or for a <c>noul</c> the probability of the rule's first answer.</param>
/// <param name="Threshold">The floor or ceiling the score was compared with.</param>
public sealed record ReviewAnswer(string Answer, double? Score, double? Threshold)
{
    /// <summary>The answer of a question that was not asked or got no usable reply.</summary>
    public static ReviewAnswer Undecidable { get; } = new(RuleEngine.Undecidable, null, null);
}

/// <summary>One logged decision-model question and what the review did with its answer.</summary>
/// <param name="RuleId">The rule whose question was asked.</param>
/// <param name="Subject">The lens or finding the question was about.</param>
/// <param name="Answer">The answer as the rule's floor left it.</param>
/// <param name="Score">The confidence, or the probability of a <c>noul</c>'s first answer.</param>
/// <param name="Effect">What the review did: for example <c>not seated</c>, <c>dropped</c> or <c>no effect</c>.</param>
public sealed record ReviewDecision(string RuleId, string Subject, string Answer, double? Score, string Effect);

/// <summary>
/// Puts the review commands' questions to the decision model the Arbiter's provider configuration names, through the
/// same client the Arbiter rules use, and records one decision-log entry per question. Optional by design: with no
/// provider, no key, a fault or a timeout every answer is <c>undecidable</c> and the review behaves as without it.
/// </summary>
/// <remarks>
/// The first fault (a transport error, a timeout or an unreadable reply) stops every later call of the command run, so
/// a model that is down costs at most one timeout. A reply that answered below a rule's floor is not a fault.
/// </remarks>
public sealed class ReviewDecisionModel
{
    /// <summary>
    /// Requests in flight at once. Each request carries one question: the provider's batched state is one object, and
    /// in a live replay two similar questions in one request each came back near 50/50 (confidence 0.10 and 0.12)
    /// while the same question alone came back at 1.0, so the model cannot tell which state is its question's.
    /// </summary>
    private const int Concurrency = 4;
    private const string ProviderKind = "systemone";

    private readonly IArbiterProvider? _provider;
    private readonly IReadOnlyList<ArbiterRule> _rules;
    private readonly string? _logDirectory;
    private readonly TimeProvider _clock;
    private readonly List<ReviewDecision> _decisions = [];
    private bool _faulted;

    /// <summary>Creates the model over a provider; a null provider is the model switched off.</summary>
    /// <param name="provider">The provider that answers; null when no decision model is configured.</param>
    /// <param name="rules">The effective Arbiter rules, with the host's overrides applied.</param>
    /// <param name="logDirectory">The Arbiter directory the decision log is appended in; null logs nothing.</param>
    /// <param name="clock">Stamps the decision records.</param>
    /// <param name="unavailableReason">Why a configured model cannot be used (no key, a bad endpoint); null otherwise.</param>
    /// <param name="cachePath">A file the answers of this review cycle are kept in and reused from; null keeps none.</param>
    public ReviewDecisionModel(
        IArbiterProvider? provider,
        IReadOnlyList<ArbiterRule> rules,
        string? logDirectory,
        TimeProvider clock,
        string? unavailableReason = null,
        string? cachePath = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(clock);
        _provider = provider is NoneProvider ? null : provider;
        _rules = rules;
        _logDirectory = logDirectory;
        _clock = clock;
        UnavailableReason = _provider is null ? unavailableReason : null;
        _cache = _provider is not null && cachePath is not null ? ReviewDecisionCache.Load(cachePath) : null;
    }

    private readonly ReviewDecisionCache? _cache;

    /// <summary>The model switched off: nothing is asked, logged or printed.</summary>
    public static ReviewDecisionModel Off { get; } = new(null, [], null, TimeProvider.System);

    /// <summary>Whether a provider is configured, so questions are asked until the first fault.</summary>
    public bool Configured => _provider is not null;

    /// <summary>Why a decision model the configuration names cannot be used; null when it can, or when none is named.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Whether a fault stopped further calls in this command run.</summary>
    public bool Faulted => _faulted;

    /// <summary>Every question asked so far, in order, with its answer and effect.</summary>
    public IReadOnlyList<ReviewDecision> Decisions => _decisions;

    /// <summary>The effective rule with this id, when it is enabled and asks a question; null otherwise.</summary>
    public ArbiterRule? Rule(string id) =>
        _rules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal) && rule.Enabled && rule.Ask is not null);

    /// <summary>
    /// Asks <paramref name="rule"/>'s question once per subject, in as few requests as the provider budget allows, and
    /// logs one decision per subject with the effect <paramref name="effectOf"/> gives its answer.
    /// </summary>
    /// <returns>The answer per subject; <see cref="ReviewAnswer.Undecidable"/> for every subject when nothing was asked.</returns>
    public async Task<IReadOnlyDictionary<string, ReviewAnswer>> AskAsync(
        ArbiterRule? rule,
        IReadOnlyList<ReviewQuestion> questions,
        Func<ReviewQuestion, ReviewAnswer, string> effectOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(effectOf);

        Dictionary<string, ReviewAnswer> answers = new(StringComparer.Ordinal);
        foreach (ReviewQuestion question in questions)
            answers[question.Subject] = ReviewAnswer.Undecidable;

        if (_provider is null || rule?.Ask is null || questions.Count == 0)
            return answers;

        // An answer an earlier run of this cycle got (--candidates, then the verdict) is reused, so the two runs agree
        // and the question costs one call.
        List<ReviewQuestion> unasked = [];
        foreach (ReviewQuestion question in questions)
        {
            if (_cache is not null && _cache.TryGet(rule, question, out ReviewAnswer? cached))
            {
                answers[question.Subject] = cached;
                _decisions.Add(new ReviewDecision(rule.Id, question.Subject, cached.Answer, cached.Score, effectOf(question, cached)));
            }
            else
            {
                unasked.Add(question);
            }
        }

        for (int start = 0; start < unasked.Count && !_faulted; start += Concurrency)
        {
            Sent[] wave = await Task.WhenAll(unasked
                    .Skip(start)
                    .Take(Concurrency)
                    .Select(question => SendAsync(rule, question, cancellationToken)))
                .ConfigureAwait(false);
            foreach (Sent sent in wave)
                await RecordAsync(rule, sent, answers, effectOf, cancellationToken).ConfigureAwait(false);
        }

        _cache?.Save();
        return answers;
    }

    /// <summary>
    /// The one line a review prints about the model: what it changed, or that a configured model could not be used.
    /// Null when no model is configured, so a review without one prints exactly what it printed before.
    /// </summary>
    /// <param name="counts">The non-zero effects to report, in order, each as a phrase such as <c>2 nitpicks dropped</c>.</param>
    public string? SummaryLine(IReadOnlyList<string> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (UnavailableReason is not null)
            return $"decision model: not configured ({UnavailableReason})";
        if (_provider is null)
            return null;

        string fault = _faulted ? "; stopped after a provider fault, the rest went unasked" : string.Empty;
        return counts.Count == 0
            ? $"decision model: {_decisions.Count} question(s), no effect{fault}"
            : $"decision model: {string.Join(", ", counts)}{fault}";
    }

    private sealed record Sent(ReviewQuestion Question, string Id, ArbiterStep1BatchResult? Batch, int LatencyMs);

    private async Task<Sent> SendAsync(ArbiterRule rule, ReviewQuestion question, CancellationToken cancellationToken)
    {
        Dictionary<string, ArbiterFact> facts = new(StringComparer.Ordinal);
        ArbiterRule expanded = Expand(rule, question, 0, facts);
        Stopwatch watch = Stopwatch.StartNew();
        ArbiterStep1BatchResult? batch = null;
        try
        {
            batch = await _provider!.AskStep1Async([expanded], new ArbiterFactSet(facts), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ProviderFailures.IsAbsorbable(ex))
        {
            // Recorded as a fault below, like the client's own error paths.
        }

        return new Sent(question, expanded.Id, batch, (int)watch.ElapsedMilliseconds);
    }

    private async Task RecordAsync(
        ArbiterRule rule,
        Sent sent,
        Dictionary<string, ReviewAnswer> answers,
        Func<ReviewQuestion, ReviewAnswer, string> effectOf,
        CancellationToken cancellationToken)
    {
        ArbiterStep1BatchResult? batch = sent.Batch;
        // The client's error paths answer every question undecidable with no scores; a reply below a floor keeps them.
        bool fault = batch is null || (batch.RequestSent && batch.ScoresByRule is null);
        _faulted |= fault;
        string status = fault ? ArbiterProviderStatuses.Error
            : batch!.RequestSent ? ArbiterProviderStatuses.Answered
            : ArbiterProviderStatuses.OverBudget;

        {
            ReviewQuestion question = sent.Question;
            string id = sent.Id;
            string answer = batch?.AnswersByRule.TryGetValue(id, out string? given) == true ? given : RuleEngine.Undecidable;
            ArbiterStep1Score? score = null;
            batch?.ScoresByRule?.TryGetValue(id, out score);
            double? value = string.Equals(rule.Ask!.Type, "noul", StringComparison.Ordinal)
                ? score?.Probabilities is [double probability, ..] ? probability : null
                : score?.Confidence;
            ReviewAnswer result = new(answer, value, score?.Threshold);
            answers[question.Subject] = result;
            if (!fault)
                _cache?.Put(rule, question, result);

            string effect = effectOf(question, result);
            _decisions.Add(new ReviewDecision(rule.Id, question.Subject, answer, value, effect));
            await LogAsync(rule, question, score, answer, effect, status, batch, sent.LatencyMs, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One question per subject: the rule's id gains the for-each suffix the evaluator uses, and each state field reads
    /// a fact scoped to the subject, so questions in one request never share a value.
    /// </summary>
    private static ArbiterRule Expand(ArbiterRule rule, ReviewQuestion question, int index, Dictionary<string, ArbiterFact> facts)
    {
        RuleAsk ask = rule.Ask!;
        Dictionary<string, string> state = new(StringComparer.Ordinal);
        foreach ((string field, string factName) in ask.State)
        {
            string scoped = $"q[{index}].{factName}";
            question.Facts.TryGetValue(factName, out object? value);
            facts[scoped] = new ArbiterFact(value, ArbiterFactLabel.Derived);
            state[field] = scoped;
        }

        string? instructions = question.Lens is not null && ask.InstructionsFrom is { } from && from.StartsWith("lens:", StringComparison.Ordinal)
            ? "lens:" + question.Lens
            : ask.InstructionsFrom;
        // A choice is answered among its criteria only; the step-0 answers a hook-side rule also declares are not
        // options, and leaving them in would stop the probabilities from being read for the log.
        IReadOnlyList<string> answers = string.Equals(ask.Type, "choice", StringComparison.Ordinal) && ask.Criteria is { Count: > 0 } criteria
            ? [.. rule.Answers.Where(criteria.ContainsKey)]
            : rule.Answers;
        return rule with
        {
            Id = rule.Id + RuleValidator.ExpansionSeparator + index,
            Answers = answers,
            Ask = ask with { State = state, ForEach = null, When = null, InstructionsFrom = instructions },
        };
    }

    private async Task LogAsync(
        ArbiterRule rule,
        ReviewQuestion question,
        ArbiterStep1Score? score,
        string answer,
        string effect,
        string status,
        ArbiterStep1BatchResult? batch,
        int? latencyMs,
        CancellationToken cancellationToken)
    {
        if (_logDirectory is null)
            return;

        DateTimeOffset now = _clock.GetUtcNow();
        ArbiterDecisionRule entry = new(
            rule.Id,
            1,
            answer,
            score?.Probabilities,
            score?.Confidence,
            score?.Threshold,
            rule.Effects.TryGetValue(answer, out string? mapped) ? mapped : ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamilies.ForTrigger(rule.Trigger)),
            [.. rule.Ask!.State.Values.Distinct(StringComparer.Ordinal).Select(fact => new ArbiterRuleFactRef(fact, "derived"))],
            "review command: " + effect,
            candidate: question.Subject);
        // Usage is the request's, so only the first question of a request carries it, and a sum over the log stays true.
        ArbiterProviderRecord provider = new(
            ProviderKind,
            null,
            batch?.Model,
            latencyMs is not null && batch?.Usage is { } usage ? new ArbiterProviderUsage(usage.InputTokens, usage.OutputTokens) : null,
            latencyMs,
            status);
        ArbiterDecisionRecord record = new(
            ArbiterRecordId.New(now),
            now,
            ArbiterSources.Serve,
            "review",
            null,
            string.Empty,
            rule.Trigger,
            null,
            "none",
            null,
            null,
            null,
            null,
            string.Empty,
            [entry],
            provider,
            effect,
            0,
            latencyMs ?? 0);
        try
        {
            await new DecisionLog(_logDirectory).AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is an audit trail; a review is never failed because it could not be written.
        }
    }

    /// <summary>Formats a score for a report line, as two decimals.</summary>
    public static string Format(double? score) =>
        score is double value ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    /// <summary>The text of a finding a question about its substance reads: the claim, then the failure scenario.</summary>
    public static string FindingText(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        StringBuilder text = new();
        text.Append(finding.File).Append(':').Append(finding.Line).Append(" — ").Append(finding.Claim ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(finding.FailureScenario))
            text.Append("\nFailure scenario: ").Append(finding.FailureScenario);
        return text.ToString();
    }
}
