using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter;

/// <summary>The git-derived facts the evaluator merges into each trigger's fact bag.</summary>
/// <remarks>
/// A port the host implements; Core takes it as a constructor argument and
/// constructs nothing itself. Tests inject a pass-through stub.
/// </remarks>
public interface IArbiterGitFacts
{
    /// <summary>Merges git-derived facts into <paramref name="facts"/>.</summary>
    ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev);
}

/// <summary>The plan facts the evaluator merges into each trigger's fact bag.</summary>
/// <remarks>
/// A port the host implements; Core takes it as a constructor argument and
/// constructs nothing itself. Tests inject a pass-through stub.
/// </remarks>
public interface IArbiterPlanReader
{
    /// <summary>Merges plan-derived facts into <paramref name="facts"/>.</summary>
    ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification);
}

/// <summary>Options for one <see cref="ArbiterEvaluator"/> call.</summary>
/// <param name="DryRun">When true, the evaluator reads the ledger and writes nothing.</param>
/// <param name="Source">Who observed the event: <c>hook</c>, <c>serve</c> or <c>eval</c>.</param>
public sealed record ArbiterEvaluatorOptions(bool DryRun = false, string Source = ArbiterSources.Hook);

/// <summary>
/// The single entry point the hook host, <c>serve</c> and <c>eval</c> call,
/// running the evaluation flow end to end.
/// </summary>
/// <remarks>
/// Every collaborator arrives through the constructor: the provider, the key
/// resolver, the ledger, the decision log, git facts, the plan reader and the
/// clock. Core constructs none of them. The ledger event is appended before
/// evaluation so a hook killed mid-evaluation still leaves an undecided entry;
/// one decision record per trigger is appended after, the primary first and
/// each secondary behind it, all sharing the event's ledger id. A provider
/// failure degrades to <c>undecidable</c> (which the family maps); any other
/// exception becomes an error result carrying <c>KW-ARB-HOOK-001</c>, never
/// <c>allow</c>. The key is resolved to prove the origin is served but is never
/// logged.
/// </remarks>
public sealed class ArbiterEvaluator
{
    /// <summary>The hook error code every evaluator failure carries.</summary>
    public const string HookErrorCode = "KW-ARB-HOOK-001";

    private readonly IArbiterProvider _provider;
    private readonly Func<Uri, string?> _keyResolver;
    private readonly InFlightLedger _ledger;
    private readonly DecisionLog _decisionLog;
    private readonly IArbiterGitFacts _gitFacts;
    private readonly IArbiterPlanReader _planReader;
    private readonly TimeProvider _clock;
    private readonly Func<string, ArbiterEvent, IReadOnlyList<string>> _secondaryTriggers;

    /// <summary>Creates an evaluator over already-constructed collaborators.</summary>
    /// <param name="provider">The step-1 provider.</param>
    /// <param name="keyResolver">Resolves the provider key for an endpoint; the key is never logged.</param>
    /// <param name="ledger">The in-flight ledger.</param>
    /// <param name="decisionLog">The decision log.</param>
    /// <param name="gitFacts">Git-derived facts.</param>
    /// <param name="planReader">Plan-derived facts.</param>
    /// <param name="clock">The evaluation clock.</param>
    /// <param name="secondaryTriggers">Derives the secondary triggers of a primary dispatch; defaults to none.</param>
    public ArbiterEvaluator(
        IArbiterProvider provider,
        Func<Uri, string?> keyResolver,
        InFlightLedger ledger,
        DecisionLog decisionLog,
        IArbiterGitFacts gitFacts,
        IArbiterPlanReader planReader,
        TimeProvider clock,
        Func<string, ArbiterEvent, IReadOnlyList<string>>? secondaryTriggers = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(keyResolver);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisionLog);
        ArgumentNullException.ThrowIfNull(gitFacts);
        ArgumentNullException.ThrowIfNull(planReader);
        ArgumentNullException.ThrowIfNull(clock);
        _provider = provider;
        _keyResolver = keyResolver;
        _ledger = ledger;
        _decisionLog = decisionLog;
        _gitFacts = gitFacts;
        _planReader = planReader;
        _clock = clock;
        _secondaryTriggers = secondaryTriggers ?? SecondaryTriggers.For;
    }

    /// <summary>Runs the evaluation flow for <paramref name="ev"/> end to end.</summary>
    public async Task<ArbiterEvaluationResult> EvaluateAsync(
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(config);
        ArbiterEvaluatorOptions effective = options ?? new ArbiterEvaluatorOptions();
        Stopwatch elapsed = Stopwatch.StartNew();

        // Allocated outside the try so the error result can name the ledger entry that
        // was already appended. A fault after the append leaves exactly one record that
        // explains the run, and returning an empty id here made it unfindable.
        string ledgerId = string.Empty;
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            IReadOnlyList<TriggerClassification> classifications = TriggerClassifier.Classify(ev);
            List<string> prompts = PromptsFor(ev);
            ledgerId = ArbiterRecordId.New(now);
            TriggerClassification first = classifications.Count > 0
                ? classifications[0]
                : new TriggerClassification(
                    null, null, ArbiterCallerSources.None, ev.Target, "not-squad",
                    new Dictionary<string, string>(StringComparer.Ordinal), null, true);
            ArbiterLedgerEvent ledgerEvent = BuildLedgerEvent(
                ledgerId, now, effective.Source, ev, first, TaskFilesFor(ev, first));
            if (effective.DryRun)
            {
                // Dry run reads and writes nothing: the reads below prove the
                // ledger is consulted without appending to either file.
                _ = _ledger.ReadAll();
                _ = _decisionLog.ReadAll();
            }
            else
            {
                await _ledger.AppendAsync(ledgerEvent, cancellationToken).ConfigureAwait(false);
            }

            // Origin-bound key check: the key is resolved but never stored or logged.
            ResolveKey(config);

            List<ArbiterDispatchEvaluation> dispatches = new(classifications.Count);
            int count = Math.Min(classifications.Count, prompts.Count);
            for (int index = 0; index < count; index++)
            {
                TriggerClassification classification = classifications[index];
                string prompt = prompts[index];
                ArbiterDispatchEvaluation evaluated = await EvaluateDispatchAsync(
                    classification, prompt, ev, config, effective, ledgerId, elapsed, cancellationToken)
                    .ConfigureAwait(false);
                dispatches.Add(evaluated);
            }

            string outcome = CombineEventOutcome(dispatches);
            return new ArbiterEvaluationResult(outcome, false, null, null, dispatches, ledgerId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed: an evaluator exception is an error result, never allow.
            // ledgerId is empty only when the fault landed before the id was allocated.
            return new ArbiterEvaluationResult(
                RuleEffects.Escalate, true, HookErrorCode, ex.Message, [], ledgerId);
        }
    }

    /// <summary>One question put to the provider: the rule it answers and, for a <c>ForEach</c> item, the candidate.</summary>
    /// <param name="RuleId">The rule's own id, which the decision record keeps.</param>
    /// <param name="Candidate">The list item, or null for a question asked once.</param>
    /// <param name="Rule">The rule as sent; an expansion carries a key of its own (rule id plus candidate).</param>
    private sealed record Question(string RuleId, string? Candidate, ArbiterRule Rule);

    /// <summary>
    /// A trigger after step 0: its answers, the step-1 questions it still needs, and the rules that
    /// could not be asked (an unreadable list). Those are never sent; they record as undecidable.
    /// </summary>
    private sealed record TriggerPlan(
        string Trigger,
        ArbiterTriggerFamily Family,
        ArbiterOutcome Step0,
        IReadOnlyDictionary<string, string> Step0Answers,
        bool ShortCircuits,
        List<Question> Questions,
        List<Question> Unanswerable);

    /// <summary>What one batched provider call returned for a set of questions.</summary>
    private sealed record Step1Call(
        IReadOnlyDictionary<string, string> Answers,
        IReadOnlyDictionary<string, ArbiterStep1Score>? Scores,
        string? Model,
        ArbiterStep1Usage? Usage,
        string Status);

    private async Task<ArbiterDispatchEvaluation> EvaluateDispatchAsync(
        TriggerClassification classification,
        string prompt,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        string? trigger = classification.Trigger;
        if (trigger is null)
        {
            return new ArbiterDispatchEvaluation(null, RuleEffects.Allow);
        }

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            classification, prompt, config, ev.ObservesReturns, ev.ModelOverrideField);
        facts = _gitFacts.Enrich(facts, classification, ev);
        facts = _planReader.Enrich(facts, classification);
        facts = LedgerFacts.Enrich(facts, _ledger, _decisionLog, ev, ledgerId);
        // D29: a return's TASK_GATE: line is checked against the report it names. Absent
        // evidence answers absent and the rule escalates; an unexpected failure here
        // reaches the catch in EvaluateAsync, which escalates with the reason.
        facts = GateReportFacts.Enrich(facts, ev);

        // Read once here and reused for both REPEAT and the record, so the count and the
        // entry it is written beside are keyed on the same digest.
        string? planDigest = facts.TryGet("plan.digest", out ArbiterFact? digestFact)
            ? digestFact.Value as string
            : null;

        // Primary and secondary read one fact set and one candidate map, so their questions
        // can share a single provider call.
        Dictionary<string, ArbiterFact> candidateFacts = new(StringComparer.Ordinal);
        TriggerPlan primary = PlanTrigger(trigger, facts, config, candidateFacts);

        // Step 0 of the primary short-circuits: no provider call, and no secondary work.
        List<TriggerPlan> secondaries = [];
        if (!primary.ShortCircuits)
        {
            foreach (string secondary in _secondaryTriggers(trigger, ev))
            {
                if (config.Arbiter.Rules.Any(rule => rule.Enabled
                    && string.Equals(rule.Trigger, secondary, StringComparison.Ordinal)))
                {
                    secondaries.Add(PlanTrigger(secondary, facts, config, candidateFacts));
                }
            }
        }

        ArbiterFactSet batchFacts = candidateFacts.Aggregate(
            facts,
            (set, pair) => set.With(pair.Key, pair.Value.Value, pair.Value.Label));
        Dictionary<string, string> step0Answers = new(StringComparer.Ordinal);
        foreach (TriggerPlan plan in new List<TriggerPlan> { primary }.Concat(secondaries))
        {
            foreach (KeyValuePair<string, string> answer in plan.Step0Answers)
            {
                step0Answers[answer.Key] = answer.Value;
            }
        }

        List<Question> primaryQuestions = primary.Questions;
        List<Question> secondaryQuestions = secondaries.SelectMany(plan => plan.Questions).ToList();
        bool hasSecondary = secondaryQuestions.Count > 0;
        Step1Call combined = await AskAsync(
            [.. primaryQuestions, .. secondaryQuestions], batchFacts, step0Answers, hasSecondary, cancellationToken)
            .ConfigureAwait(false);

        Step1Call primaryCall;
        Step1Call secondaryCall = combined;
        if (!hasSecondary)
        {
            primaryCall = combined;
        }
        else if (combined.Status is ArbiterProviderStatuses.Error or ArbiterProviderStatuses.OverBudget)
        {
            // The secondary question failed or did not fit the budget. It answers undecidable,
            // and the primary goes alone so its own answer is not lost with it.
            primaryCall = await AskAsync(primaryQuestions, batchFacts, step0Answers, false, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            primaryCall = Restrict(combined, primaryQuestions);
        }

        ArbiterDispatchEvaluation primaryResult = await RecordPrimaryAsync(
            classification, ev, primary, primaryCall, config, effective, ledgerId, planDigest, elapsed,
            cancellationToken).ConfigureAwait(false);
        string primaryOutcome = primaryResult.Outcome;

        List<ArbiterSecondaryEvaluation> secondaryResults = new(secondaries.Count);
        foreach (TriggerPlan plan in secondaries)
        {
            ArbiterSecondaryEvaluation evaluated = await RecordSecondaryAsync(
                classification, ev, plan, secondaryCall, primaryOutcome, config, effective, ledgerId, planDigest,
                elapsed, cancellationToken).ConfigureAwait(false);
            secondaryResults.Add(evaluated);
        }

        return new ArbiterDispatchEvaluation(trigger, primaryOutcome) { Secondary = secondaryResults };
    }

    /// <summary>Plans one trigger: its step-0 answers, and the step-1 questions the step-0 answers leave open.</summary>
    private static TriggerPlan PlanTrigger(
        string trigger,
        ArbiterFactSet facts,
        KyberWeaveConfig config,
        Dictionary<string, ArbiterFact> candidateFacts)
    {
        ArbiterTriggerFamily family = ArbiterTriggerFamilies.ForTrigger(trigger);
        List<ArbiterRule> step0Rules = config.Arbiter.Rules
            .Where(rule => rule.Enabled
                && string.Equals(rule.Trigger, trigger, StringComparison.Ordinal)
                && rule.Decide is { Count: > 0 })
            .ToList();
        ArbiterOutcome step0 = RuleEngine.EvaluateStep0(step0Rules, facts, trigger);
        bool shortCircuits = step0.RuleAnswers.Count > 0 && step0.Step0ShortCircuitsStep1;
        Dictionary<string, string> step0Answers = step0.RuleAnswers
            .ToDictionary(answer => answer.RuleId, answer => answer.Answer, StringComparer.Ordinal);

        List<Question> questions = [];
        List<Question> unanswerable = [];
        if (!shortCircuits)
        {
            foreach (ArbiterRule rule in config.Arbiter.Rules)
            {
                if (!rule.Enabled || !string.Equals(rule.Trigger, trigger, StringComparison.Ordinal) || rule.Ask is null)
                {
                    continue;
                }

                string decideAnswer = step0Answers.TryGetValue(rule.Id, out string? answered)
                    ? answered
                    : RuleEngine.Undecidable;
                bool shouldAsk = rule.Decide is null || rule.ShouldAsk(decideAnswer);
                if (shouldAsk && SystemOneContracts.WhenMatches(rule.Ask, step0Answers))
                {
                    AddQuestions(rule, facts, candidateFacts, questions, unanswerable);
                }
            }
        }

        return new TriggerPlan(trigger, family, step0, step0Answers, shortCircuits, questions, unanswerable);
    }

    /// <summary>
    /// Adds a rule's question. A <c>ForEach</c> rule asks once per item of its list fact, each
    /// keyed by rule id plus candidate, with the item bound to a per-item <c>candidate[i].*</c> fact.
    /// A list that is missing, has the wrong shape, or is empty gives no question to ask; the rule
    /// goes to <paramref name="unanswerable"/> so it still records, as undecidable.
    /// </summary>
    private static void AddQuestions(
        ArbiterRule rule,
        ArbiterFactSet facts,
        Dictionary<string, ArbiterFact> candidateFacts,
        List<Question> questions,
        List<Question> unanswerable)
    {
        RuleAsk ask = rule.Ask!;
        if (ask.ForEach is null)
        {
            questions.Add(new Question(rule.Id, null, rule));
            return;
        }

        if (!facts.TryGet(ask.ForEach, out ArbiterFact? list) || list.Value is not IEnumerable<string> items)
        {
            // Unsure, missing or unreadable is undecidable, never a silent disappearance.
            unanswerable.Add(new Question(rule.Id, null, rule));
            return;
        }

        int index = 0;
        foreach (string item in items)
        {
            Dictionary<string, string> state = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> field in ask.State)
            {
                if (field.Value.StartsWith("candidate.", StringComparison.Ordinal))
                {
                    string scoped = $"candidate[{rule.Id}][{index}]{field.Value["candidate".Length..]}";
                    candidateFacts[scoped] = new ArbiterFact(item, ArbiterFactLabel.Derived);
                    state[field.Key] = scoped;
                }
                else
                {
                    state[field.Key] = field.Value;
                }
            }

            // The index, not the item text, keys the question: a list may repeat an item, and the
            // provider keeps one answer per id, so equal items must not share one.
            ArbiterRule expanded = rule with
            {
                Id = rule.Id + "@" + index,
                Ask = ask with { State = state, ForEach = null },
            };
            questions.Add(new Question(rule.Id, item, expanded));
            index++;
        }

        if (index == 0)
        {
            // An empty list leaves nothing to ask, so the rule records as undecidable too.
            unanswerable.Add(new Question(rule.Id, null, rule));
        }
    }

    /// <summary>
    /// Makes one batched call for the questions. A transport or parse failure is an
    /// <c>error</c> with every answer undecidable; with <paramref name="guardAll"/> any failure
    /// is, so a secondary fault never reaches the primary.
    /// </summary>
    private async Task<Step1Call> AskAsync(
        IReadOnlyList<Question> questions,
        ArbiterFactSet facts,
        IReadOnlyDictionary<string, string> step0Answers,
        bool guardAll,
        CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
        {
            return new Step1Call(EmptyAnswers(), null, null, null, ArbiterProviderStatuses.NotEvaluated);
        }

        List<ArbiterRule> rules = new(questions.Count);
        foreach (Question question in questions)
        {
            rules.Add(question.Rule);
        }

        ArbiterStep1BatchResult batch;
        try
        {
            batch = await _provider.AskStep1Async(rules, facts, step0Answers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProviderFailure(ex, guardAll, cancellationToken))
        {
            // A provider failure is undecidable, not an error result: every question answers
            // undecidable and the family maps it. A provider bug with no guard surfaces as
            // KW-ARB-HOOK-001 instead of silently allowing.
            return new Step1Call(EmptyAnswers(), null, null, null, ArbiterProviderStatuses.Error);
        }

        bool allNotEvaluated = questions.All(question =>
            batch.AnswersByRule.TryGetValue(question.Rule.Id, out string? value)
            && string.Equals(value, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal));
        string status = allNotEvaluated
            ? ArbiterProviderStatuses.NotEvaluated
            : batch.RequestSent
                ? ArbiterProviderStatuses.Answered
                : ArbiterProviderStatuses.OverBudget;
        return new Step1Call(batch.AnswersByRule, batch.ScoresByRule, batch.Model, batch.Usage, status);
    }

    private static bool IsProviderFailure(Exception ex, bool guardAll, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or System.Text.Json.JsonException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        || (guardAll && ex is not OperationCanceledException);

    private static Dictionary<string, string> EmptyAnswers() => new(StringComparer.Ordinal);

    /// <summary>The part of a combined call that answered one trigger's questions.</summary>
    private static Step1Call Restrict(Step1Call combined, List<Question> questions)
    {
        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        foreach (Question question in questions)
        {
            if (combined.Answers.TryGetValue(question.Rule.Id, out string? answer))
            {
                answers[question.Rule.Id] = answer;
            }
        }

        return combined with { Answers = answers };
    }

    /// <summary>
    /// Combines the effects of the gating entries only. A shadow entry is log-only and never joins the
    /// combination; when every entry is shadow, the family's neutral effect stands.
    /// </summary>
    private static string CombineGating(ArbiterTriggerFamily family, List<string> gating, List<ArbiterDecisionRule> rules)
    {
        bool onlyShadow = rules.Count > 0 && rules.All(rule => rule.Shadow == true);
        return gating.Count == 0 && onlyShadow
            ? ArbiterTriggerFamilies.NeutralEffect(family)
            : ArbiterTriggerFamilies.Combine(family, gating);
    }

    private static string ProviderStatusFor(TriggerPlan plan, Step1Call call) =>
        plan.ShortCircuits
            ? ArbiterProviderStatuses.ShortCircuited
            : plan.Questions.Count == 0
                ? ArbiterProviderStatuses.NotEvaluated
                : call.Status;

    private static string AnswerFor(Question question, Step1Call call) =>
        call.Answers.TryGetValue(question.Rule.Id, out string? answer) ? answer : RuleEngine.Undecidable;

    private async Task<ArbiterDispatchEvaluation> RecordPrimaryAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        TriggerPlan plan,
        Step1Call call,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        List<ArbiterDecisionRule> rules = Step0Rules(plan.Step0);
        List<string> effects = new(plan.Step0.RuleAnswers.Where(answer => !answer.Shadow).Select(answer => answer.Effect));
        foreach (Question question in plan.Questions.Concat(plan.Unanswerable))
        {
            string answer = AnswerFor(question, call);
            string mapped = MapOrNotEvaluated(question.Rule, answer, plan.Family);
            ArbiterStep1Score? score = call.Scores is not null && call.Scores.TryGetValue(question.Rule.Id, out ArbiterStep1Score? found)
                ? found
                : null;
            (string effect, bool? shadow, string? wouldEffect) = ShadowState(
                question.Rule, mapped, ArbiterTriggerFamilies.NeutralEffect(plan.Family));
            rules.Add(Step1Rule(question, answer, effect, score, shadow, wouldEffect, evidence: null));
            if (!string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
                && !question.Rule.Shadow)
            {
                effects.Add(effect);
            }
        }

        string outcome = CombineGating(plan.Family, effects, rules);
        ArbiterProviderRecord provider = ProviderRecord(config, call, ProviderStatusFor(plan, call), elapsed);
        await AppendRecordAsync(
            classification, ev, plan.Trigger, rules, outcome, provider, call.Model, primaryLedgerId: null,
            effective, ledgerId, planDigest, config, elapsed, cancellationToken).ConfigureAwait(false);
        return new ArbiterDispatchEvaluation(plan.Trigger, outcome);
    }

    /// <summary>
    /// Records one secondary trigger behind the primary. Its rules shadow or suppress to the
    /// family's neutral effect, keeping the would-be effect on the record; its combined outcome is
    /// reported on the dispatch and the record shares the primary's ledger id.
    /// </summary>
    private async Task<ArbiterSecondaryEvaluation> RecordSecondaryAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        TriggerPlan plan,
        Step1Call call,
        string primaryOutcome,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        string neutral = ArbiterTriggerFamilies.NeutralEffect(plan.Family);
        // The primary allows when it reaches its family's allow-equivalent, the neutral effect. A gate's
        // neutral is applies and a shortcut's is spawn, so comparing against allow alone suppressed them.
        ArbiterTriggerFamily primaryFamily = ArbiterTriggerFamilies.ForTrigger(classification.Trigger!);
        bool primaryAllows = string.Equals(
            primaryOutcome, ArbiterTriggerFamilies.NeutralEffect(primaryFamily), StringComparison.Ordinal);
        List<ArbiterDecisionRule> rules = Step0Rules(plan.Step0);
        List<string> effects = new(plan.Step0.RuleAnswers.Where(answer => !answer.Shadow).Select(answer => answer.Effect));
        List<string> payload = [];
        foreach (Question question in plan.Questions.Concat(plan.Unanswerable))
        {
            string answer = AnswerFor(question, call);
            string mapped = MapOrNotEvaluated(question.Rule, answer, plan.Family);
            ArbiterStep1Score? score = call.Scores is not null && call.Scores.TryGetValue(question.Rule.Id, out ArbiterStep1Score? found)
                ? found
                : null;
            if (string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
            {
                rules.Add(Step1Rule(question, answer, neutral, score, null, null, null));
                continue;
            }

            if (!string.Equals(answer, RuleEngine.Undecidable, StringComparison.Ordinal))
            {
                payload.Add(answer);
            }

            bool shadow = question.Rule.Shadow;
            bool suppressed = !shadow && !primaryAllows;
            if (shadow || suppressed)
            {
                string? evidence = suppressed ? "suppressed: primary " + primaryOutcome : null;
                rules.Add(Step1Rule(question, answer, neutral, score, shadow ? true : null, mapped, evidence));
                if (!shadow)
                {
                    effects.Add(neutral);
                }
            }
            else
            {
                rules.Add(Step1Rule(question, answer, mapped, score, null, null, null));
                effects.Add(mapped);
            }
        }

        string outcome = CombineGating(plan.Family, effects, rules);
        ArbiterProviderRecord provider = ProviderRecord(config, call, ProviderStatusFor(plan, call), elapsed);
        await AppendRecordAsync(
            classification, ev, plan.Trigger, rules, outcome, provider, call.Model, primaryLedgerId: ledgerId,
            effective, ledgerId, planDigest, config, elapsed, cancellationToken).ConfigureAwait(false);
        string? answered = payload.Count == 0 ? null : string.Join(",", payload);
        return new ArbiterSecondaryEvaluation(plan.Trigger, outcome, answered);
    }

    private static List<ArbiterDecisionRule> Step0Rules(ArbiterOutcome step0)
    {
        List<ArbiterDecisionRule> rules = new(step0.RuleAnswers.Count);
        foreach (ArbiterRuleAnswer answer in step0.RuleAnswers)
        {
            rules.Add(new ArbiterDecisionRule(
                answer.RuleId, 0, answer.Answer, null, null, null, answer.Effect, [], null,
                shadow: answer.Shadow ? true : null,
                wouldEffect: answer.WouldEffect));
        }

        return rules;
    }

    /// <summary>One step-1 rule's decision entry: its answer, its effect, and the score when it was scored.</summary>
    private static ArbiterDecisionRule Step1Rule(
        Question question,
        string answer,
        string effect,
        ArbiterStep1Score? score,
        bool? shadow,
        string? wouldEffect,
        string? evidence) =>
        new(
            question.RuleId,
            1,
            answer,
            score?.Probabilities,
            score?.Confidence,
            score?.Threshold,
            effect,
            [],
            evidence,
            shadow: shadow,
            wouldEffect: wouldEffect,
            candidate: question.Candidate);

    /// <summary>
    /// A rule's shadow state for one answer: a shadow rule records and contributes the neutral effect,
    /// and keeps the mapped effect as the would-be effect. Shared by the primary and secondary paths.
    /// </summary>
    private static (string Effect, bool? Shadow, string? WouldEffect) ShadowState(
        ArbiterRule rule,
        string mapped,
        string neutral) =>
        rule.Shadow ? (neutral, true, mapped) : (mapped, null, null);

    /// <summary>
    /// A question the provider did not evaluate records its family's neutral effect. It is not counted in
    /// the combination, and <c>allow</c> is not a legal effect for every family.
    /// </summary>
    private static string MapOrNotEvaluated(ArbiterRule rule, string answer, ArbiterTriggerFamily family) =>
        string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
            ? ArbiterTriggerFamilies.NeutralEffect(family)
            : MapStep1AnswerToEffect(rule, answer, family);

    private static ArbiterProviderRecord ProviderRecord(
        KyberWeaveConfig config,
        Step1Call call,
        string status,
        Stopwatch elapsed) =>
        new(
            ProviderKindName(config),
            EndpointOrigin(config),
            call.Model,
            call.Usage is null ? null : new ArbiterProviderUsage(call.Usage.InputTokens, call.Usage.OutputTokens),
            (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds),
            status);

    private async Task AppendRecordAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        string trigger,
        List<ArbiterDecisionRule> rules,
        string outcome,
        ArbiterProviderRecord provider,
        string? model,
        string? primaryLedgerId,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        KyberWeaveConfig config,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        classification.Headers.TryGetValue("PLAN_FILE", out string? planFile);
        classification.Headers.TryGetValue("TASK", out string? task);
        string repeatKey = rules.Count > 0 ? rules[0].Id : trigger;
        int repeat = _decisionLog.Repeat(repeatKey, planFile, planDigest, task, ev.Session);
        ArbiterDecisionRecord record = new(
            ArbiterRecordId.New(now),
            now,
            effective.Source,
            ev.Harness,
            ev.Session,
            ledgerId,
            trigger,
            classification.Caller,
            classification.CallerSource,
            classification.Target,
            planFile,
            planDigest,
            task,
            RuleSetDigest(config.Arbiter.Rules),
            rules,
            provider,
            outcome,
            repeat,
            (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds),
            primaryLedgerId: primaryLedgerId,
            model: model);
        if (!effective.DryRun)
        {
            await _decisionLog.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ResolveKey(KyberWeaveConfig config)
    {
        // The none provider sends nothing, so it has no key to prove. Reading the OS
        // credential store for it would fail on hosts without that store, such as a
        // Linux runner without secret-tool, and block a configuration that needs no key.
        if (config.Arbiter.Provider.Kind == ArbiterProviderKind.None)
        {
            return;
        }

        if (Uri.TryCreate(config.Arbiter.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            // The key itself is never stored, logged, or interpolated anywhere.
            _ = _keyResolver(endpoint);
        }
    }

    private static string? EndpointOrigin(KyberWeaveConfig config)
    {
        try
        {
            return ArbiterKeyResolver.GetOrigin(config.Arbiter.Provider.Endpoint);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string MapStep1AnswerToEffect(ArbiterRule rule, string answer, ArbiterTriggerFamily family)
    {
        if (string.Equals(answer, RuleEngine.Undecidable, StringComparison.Ordinal))
        {
            return ArbiterTriggerFamilies.UndecidableEffect(family);
        }

        if (rule.Effects.TryGetValue(answer, out string? effect))
        {
            return effect;
        }

        return ArbiterTriggerFamilies.UndecidableEffect(family);
    }

    private static string CombineEventOutcome(List<ArbiterDispatchEvaluation> dispatches)
    {
        if (dispatches.Count == 0)
        {
            return RuleEffects.Allow;
        }

        foreach (ArbiterDispatchEvaluation dispatch in dispatches)
        {
            if (string.Equals(dispatch.Outcome, RuleEffects.Escalate, StringComparison.Ordinal))
            {
                return RuleEffects.Escalate;
            }
        }

        return dispatches[0].Outcome;
    }

    private static List<string> PromptsFor(ArbiterEvent ev)
    {
        if (ev.Dispatches.Count > 0)
        {
            List<string> prompts = new(ev.Dispatches.Count);
            foreach (ArbiterDispatch dispatch in ev.Dispatches)
            {
                prompts.Add(dispatch.Prompt);
            }

            return prompts;
        }

        return [ev.Prompt];
    }

    private static ArbiterLedgerEvent BuildLedgerEvent(
        string ledgerId,
        DateTimeOffset now,
        string source,
        ArbiterEvent ev,
        TriggerClassification first,
        IReadOnlyList<string>? taskFiles)
    {
        string phase = MapPhase(ev);
        ArbiterLedgerEvent ledgerEvent = new(ledgerId, now, source, ev.Harness, ev.Session, phase)
        {
            CallId = ev.ToolCallId,
            Trigger = first.Trigger,
            Target = first.Target,
            Caller = first.Caller,
            CallerSource = first.CallerSource,
            Headers = BuildHeaders(first),
            PairDigest = PairDigestOf(ev),
            TaskFiles = taskFiles,
            Returns = phase == ArbiterLedgerPhases.Post
                ? ReturnMarkerParser.Parse(
                    ev.ToolOutput,
                    readAttestations: string.Equals(first.Trigger, "delegate.planner.returned", StringComparison.Ordinal))
                : null,
        };
        return ledgerEvent;
    }

    /// <summary>
    /// The task files of a <c>delegate</c> dispatch, recorded on its pre event so a later
    /// dispatch can tell which files it holds. An unreadable plan records none, and the
    /// rules that need the plan answer for that themselves.
    /// </summary>
    private IReadOnlyList<string>? TaskFilesFor(ArbiterEvent ev, TriggerClassification first)
    {
        if (MapPhase(ev) != ArbiterLedgerPhases.Pre
            || !string.Equals(first.Trigger, "delegate", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            ArbiterFactSet facts = _planReader.Enrich(new ArbiterFactSet(), first);
            return facts.TryGet("plan.task.files", out ArbiterFact? files)
                && files.Value is IEnumerable<string> paths
                    ? [.. paths]
                    : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pairs a return with its dispatch when the harness has no call id: the target and
    /// the prompt as finally executed, which is the prompt after its headers are stripped.
    /// </summary>
    private static string? PairDigestOf(ArbiterEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Target) || string.IsNullOrWhiteSpace(ev.Prompt))
        {
            return null;
        }

        string executed = HeaderBlock.Strip(ev.Prompt).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ev.Target + "\n" + executed)));
    }

    private static ArbiterLedgerHeaders? BuildHeaders(TriggerClassification classification)
    {
        classification.Headers.TryGetValue("PLAN_FILE", out string? planFile);
        classification.Headers.TryGetValue("TASK", out string? task);
        classification.Headers.TryGetValue("LENS", out string? lens);
        classification.Headers.TryGetValue("REFUTE", out string? refute);
        if (planFile is null && task is null && lens is null && refute is null)
        {
            return null;
        }

        return new ArbiterLedgerHeaders(planFile, task, lens, refute);
    }

    private static string MapPhase(ArbiterEvent ev)
    {
        if (!ev.IsDispatch)
        {
            return ArbiterLedgerPhases.Unmarked;
        }

        if (string.Equals(ev.Phase, "post", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ev.Phase, "return", StringComparison.OrdinalIgnoreCase))
        {
            return ArbiterLedgerPhases.Post;
        }

        if (string.Equals(ev.Phase, "pre", StringComparison.OrdinalIgnoreCase))
        {
            return ArbiterLedgerPhases.Pre;
        }

        return ArbiterLedgerPhases.Unmarked;
    }

    private static string ProviderKindName(KyberWeaveConfig config) =>
        config.Arbiter.Provider.Kind == ArbiterProviderKind.Systemone ? "systemone" : "none";

    private static string RuleSetDigest(IReadOnlyList<ArbiterRule> rules)
    {
        List<string> ids = new(rules.Count);
        foreach (ArbiterRule rule in rules)
        {
            ids.Add($"{rule.Id}|{rule.Trigger}|{(rule.Enabled ? "1" : "0")}");
        }

        ids.Sort(StringComparer.Ordinal);
        string joined = string.Join("\n", ids);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(hash);
    }
}
