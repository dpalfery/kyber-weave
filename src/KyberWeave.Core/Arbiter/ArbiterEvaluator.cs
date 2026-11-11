using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
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
/// <param name="ConfirmModelWrite">Writes the model token into the harness input and reports whether it held. Asked
/// before a delivered model is recorded, so the decision says delivered only for a write that happened. Null when no
/// harness writes the model (serve, eval), in which case the decision is recorded as resolved.</param>
public sealed record ArbiterEvaluatorOptions(
    bool DryRun = false,
    string Source = ArbiterSources.Hook,
    Func<string, bool>? ConfirmModelWrite = null);

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

    private const string ModelTargetTrigger = "delegate.model";

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
        IReadOnlyList<ArbiterRule> rules = ModelTargetingRules.Apply(config.Arbiter.Rules, config.Arbiter.ModelTargeting.Mode);
        ArbiterModelTargets? table = ArbiterModelTargets.TryRead(_ledger.DirectoryPath, ev.Harness);

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
                ledgerId, now, effective.Source,
                ev with { RenderedModelToken = RenderedTokenOf(ev, first.Target, table) },
                first, TaskFilesFor(ev, first), table);
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
                    classification, prompt, ev, config, rules, table, effective, ledgerId, elapsed, cancellationToken)
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

    /// <summary>A trigger after step 0: its answers, and the step-1 questions it still needs.</summary>
    private sealed record TriggerPlan(
        string Trigger,
        ArbiterTriggerFamily Family,
        ArbiterOutcome Step0,
        IReadOnlyDictionary<string, string> Step0Answers,
        bool ShortCircuits,
        List<Question> Questions);

    /// <summary>One finding's judgement input: the plan its own fact bag produced and the call that answered it.</summary>
    private sealed record FindingJudgement(string? FindingId, TriggerPlan Plan, Step1Call Call);

    /// <summary>One finding's decision rules and the effects they contribute to the dispatch outcome.</summary>
    private sealed record JudgedFinding(string? FindingId, List<ArbiterDecisionRule> Rules, List<string> Effects);

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
        IReadOnlyList<ArbiterRule> rules,
        ArbiterModelTargets? table,
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

        // The primary's decision record id, allocated up front: a stored failure names the decision that judged it.
        string recordId = ArbiterRecordId.New(_clock.GetUtcNow());

        // Derived once: the bag declares the facts of every secondary, and the loop below plans them.
        // The derivation reads this dispatch's own target and prompt, so a marker on one batched dispatch never derives
        // a shortcut for another.
        IReadOnlyList<string> secondaryNames = _secondaryTriggers(
            trigger, ev with { Target = classification.Target, Prompt = prompt });
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            classification, prompt, config, ev.ObservesReturns, ev.ModelOverrideField, ev.Model, secondaryNames);
        facts = _gitFacts.Enrich(facts, classification, ev);
        facts = GovernedPathsFacts.Enrich(facts, config.Ontology.DocsRoots);
        facts = _planReader.Enrich(facts, classification);
        facts = LedgerFacts.Enrich(facts, _ledger, _decisionLog, ev, ledgerId);
        facts = ModelTargetFacts.Enrich(facts, _ledger, _decisionLog, ledgerId, config.Arbiter.ModelTargeting);
        // D29: a return's TASK_GATE: line is checked against the report it names. Absent
        // evidence answers absent and the rule escalates; an unexpected failure here
        // reaches the catch in EvaluateAsync, which escalates with the reason.
        facts = GateReportFacts.Enrich(facts, ev);

        // The review's REVIEW_RUN names the store its findings sit in. A lens return's first finding is the
        // one the nitpick filter reads; a refutation reads whether the filter dropped the finding it refutes.
        string? reviewRun = HeaderBlock.Parse(prompt).Headers.TryGetValue("REVIEW_RUN", out string? run) ? run : null;
        IReadOnlyList<ReturnedFinding> returned = string.Equals(trigger, "lens.returned", StringComparison.Ordinal)
            ? ReturnedFinding.ParseAll(ev.ToolOutput)
            : [];
        // Each finding is judged on its own bag: the static facts plus that finding's own id, text and severity.
        ArbiterFactSet baseFacts = facts;
        bool gateCorroborated = GateReportFacts.FailedGateCorroborates(ev.Cwd);
        if (returned.Count > 0)
        {
            facts = TriggerFactBuilder.WithReturnedFinding(baseFacts, returned[0], gateCorroborated);
        }

        // A refutation names its finding as <lens>/<slug>; the store and the notices carry the bare slug.
        string? refutedSlug = null;
        if (reviewRun is not null
            && string.Equals(trigger, "refute.spawn", StringComparison.Ordinal)
            && facts.TryGet("finding.id", out ArbiterFact? refuted)
            && refuted.Value is string refutedId)
        {
            refutedSlug = FindingSlug(refutedId);
            IReadOnlyList<string> stored = ReviewStore(config).ReadFindings(reviewRun);
            facts = TriggerFactBuilder.WithDroppedStatus(facts, refutedSlug, stored);
            facts = TriggerFactBuilder.WithPriorFindings(facts, refutedSlug, stored);
        }

        // Read once here and reused for both REPEAT and the record, so the count and the
        // entry it is written beside are keyed on the same digest.
        string? planDigest = facts.TryGet("plan.digest", out ArbiterFact? digestFact)
            ? digestFact.Value as string
            : null;

        // A rework dispatch reads the failure its cluster stored last, then stores this one, so the next rework of
        // the cluster has a prior to compare. The store is written on every evaluation but a dry run.
        string? priorDecisionId = null;
        if (secondaryNames.Contains(FailureFacts.Trigger)
            && FailureFacts.ClusterOf(prompt) is { } cluster
            && FailureFacts.CurrentTextOf(prompt) is { } currentFailure)
        {
            classification.Headers.TryGetValue("PLAN_FILE", out string? failurePlan);
            ArbiterRunStore failures = ReviewStore(config);
            FailureRecord? prior = FailureFacts.LastFor(failures.ReadFailures(failurePlan, planDigest, ev.Session), cluster);
            facts = facts
                .With(FailureFacts.PreviousText, prior?.Text, ArbiterFactLabel.Derived)
                .With(FailureFacts.CurrentText, currentFailure, ArbiterFactLabel.Derived);
            priorDecisionId = prior?.DecisionId;
            if (!effective.DryRun)
            {
                await failures.AppendFailureAsync(
                    failurePlan, planDigest, ev.Session,
                    FailureFacts.Stored(cluster, recordId, currentFailure), cancellationToken).ConfigureAwait(false);
            }
        }

        // Primary and secondary read one fact set and one candidate map, so their questions
        // can share a single provider call.
        Dictionary<string, ArbiterFact> candidateFacts = new(StringComparer.Ordinal);
        TriggerPlan primary = PlanTrigger(trigger, facts, rules, candidateFacts);

        // Step 0 of the primary short-circuits: no provider call, and no secondary work.
        List<TriggerPlan> secondaries = [];
        if (!primary.ShortCircuits)
        {
            foreach (string secondary in secondaryNames)
            {
                if (rules.Any(rule => rule.Enabled
                    && string.Equals(rule.Trigger, secondary, StringComparison.Ordinal)))
                {
                    secondaries.Add(PlanTrigger(secondary, facts, rules, candidateFacts));
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

        // A lens return judges every finding it carries. The first rides the batch above; each later one is asked
        // alone on its own fact bag, so its step-0 gates and its step-1 answer read its own id, text and severity.
        FindingJudgement primaryJudgement = new(returned.Count > 0 ? returned[0].Id : null, primary, primaryCall);
        List<FindingJudgement> judgements = [primaryJudgement];
        for (int index = 1; index < returned.Count; index++)
        {
            ArbiterFactSet findingFacts = TriggerFactBuilder.WithReturnedFinding(baseFacts, returned[index], gateCorroborated);
            Dictionary<string, ArbiterFact> findingCandidates = new(StringComparer.Ordinal);
            TriggerPlan findingPlan = PlanTrigger(trigger, findingFacts, rules, findingCandidates);
            ArbiterFactSet findingBatch = findingCandidates.Aggregate(
                findingFacts, (set, pair) => set.With(pair.Key, pair.Value.Value, pair.Value.Label));
            Step1Call findingCall = await AskAsync(
                findingPlan.Questions, findingBatch, findingPlan.Step0Answers, false, cancellationToken).ConfigureAwait(false);
            judgements.Add(new FindingJudgement(returned[index].Id, findingPlan, findingCall));
        }

        // One decision record per dispatch: every finding's rules sit on it, tagged with the finding they judged.
        List<JudgedFinding> judged = judgements
            .Select(judgement => JudgeFinding(judgement, recordId, refutedSlug))
            .ToList();
        ArbiterDispatchEvaluation primaryResult = await RecordPrimaryAsync(
            classification, ev, primaryJudgement, judged, recordId, config, rules, effective, ledgerId, planDigest,
            elapsed, cancellationToken).ConfigureAwait(false);
        string primaryOutcome = primaryResult.Outcome;
        if (!effective.DryRun && reviewRun is not null && returned.Count > 0)
        {
            bool[] dropped = judged
                .Select(finding => string.Equals(
                    ArbiterTriggerFamilies.Combine(primary.Family, finding.Effects), RuleEffects.Drop, StringComparison.Ordinal))
                .ToArray();
            await RecordReturnedFindingsAsync(ReviewStore(config), reviewRun, returned, dropped)
                .ConfigureAwait(false);
        }

        // A refuter's return records its verdict against the finding its REFUTE header names, keyed by the slug.
        // A return that states no recognised verdict records unknown; it never drops the finding it refutes.
        if (!effective.DryRun && reviewRun is not null
            && string.Equals(trigger, "refute.returned", StringComparison.Ordinal)
            && HeaderBlock.Parse(prompt).Headers.TryGetValue("REFUTE", out string? refuteHeader))
        {
            await ReviewStore(config)
                .AppendRefutationOutcomeAsync(reviewRun, FindingSlug(refuteHeader), RefutationOutcome.Parse(ev.ToolOutput), cancellationToken)
                .ConfigureAwait(false);
        }

        List<ArbiterSecondaryEvaluation> secondaryResults = new(secondaries.Count);
        foreach (TriggerPlan plan in secondaries)
        {
            ArbiterSecondaryEvaluation evaluated = await RecordSecondaryAsync(
                classification, ev, plan, secondaryCall, primaryOutcome, config, rules, facts, table, effective,
                ledgerId, planDigest, priorDecisionId, elapsed, cancellationToken).ConfigureAwait(false);
            secondaryResults.Add(evaluated);
        }

        // An answered shortcut is the one secondary effect that gates: it withholds the primary's worker, so the
        // dispatch is answered when the primary allowed it. Every other secondary outcome leaves the primary's alone.
        bool answered = string.Equals(primaryOutcome, RuleEffects.Allow, StringComparison.Ordinal)
            && secondaryResults.Any(result => string.Equals(result.Outcome, RuleEffects.Answer, StringComparison.Ordinal));
        // A same-failure advisory is the other secondary effect that reaches the dispatch, when the primary allowed it.
        bool advised = string.Equals(primaryOutcome, RuleEffects.Allow, StringComparison.Ordinal)
            && secondaryResults.Any(result => string.Equals(result.Outcome, RuleEffects.SameFailure, StringComparison.Ordinal));
        string dispatchOutcome = answered ? RuleEffects.Answer : advised ? RuleEffects.SameFailure : primaryOutcome;
        return new ArbiterDispatchEvaluation(trigger, dispatchOutcome) { Secondary = secondaryResults };
    }

    /// <summary>Plans one trigger: its step-0 answers, and the step-1 questions the step-0 answers leave open.</summary>
    private static TriggerPlan PlanTrigger(
        string trigger,
        ArbiterFactSet facts,
        IReadOnlyList<ArbiterRule> rules,
        Dictionary<string, ArbiterFact> candidateFacts)
    {
        ArbiterTriggerFamily family = ArbiterTriggerFamilies.ForTrigger(trigger);
        List<ArbiterRule> step0Rules = rules
            .Where(rule => rule.Enabled
                && string.Equals(rule.Trigger, trigger, StringComparison.Ordinal)
                && rule.Decide is { Count: > 0 })
            .ToList();
        ArbiterOutcome step0 = RuleEngine.EvaluateStep0(step0Rules, facts, trigger);
        bool shortCircuits = step0.RuleAnswers.Count > 0 && step0.Step0ShortCircuitsStep1;
        Dictionary<string, string> step0Answers = step0.RuleAnswers
            .ToDictionary(answer => answer.RuleId, answer => answer.Answer, StringComparer.Ordinal);

        List<Question> questions = [];
        if (!shortCircuits)
        {
            foreach (ArbiterRule rule in rules)
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
                    AddQuestions(rule, facts, candidateFacts, questions);
                }
            }
        }

        return new TriggerPlan(trigger, family, step0, step0Answers, shortCircuits, questions);
    }

    /// <summary>
    /// Adds a rule's question. A <c>ForEach</c> rule asks once per item of its list fact, each
    /// keyed by rule id plus candidate, with the item bound to a per-item <c>candidate[i].*</c> fact.
    /// </summary>
    private static void AddQuestions(
        ArbiterRule rule,
        ArbiterFactSet facts,
        Dictionary<string, ArbiterFact> candidateFacts,
        List<Question> questions)
    {
        RuleAsk ask = rule.Ask!;
        if (ask.ForEach is null)
        {
            questions.Add(new Question(rule.Id, null, rule));
            return;
        }

        if (!facts.TryGet(ask.ForEach, out ArbiterFact? list) || list.Value is not IEnumerable<string> items)
        {
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

            ArbiterRule expanded = rule with
            {
                Id = rule.Id + "@" + item,
                Ask = ask with { State = state, ForEach = null },
            };
            questions.Add(new Question(rule.Id, item, expanded));
            index++;
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
        return new Step1Call(
            FloorAnswers(batch.AnswersByRule, batch.ScoresByRule), batch.ScoresByRule, batch.Model, batch.Usage, status);
    }

    /// <summary>
    /// An answer whose confidence is below its own floor is undecidable. The provider client applies the
    /// floor itself; the evaluator applies it again so no provider can gate a dispatch on a score under its floor.
    /// </summary>
    private static IReadOnlyDictionary<string, string> FloorAnswers(
        IReadOnlyDictionary<string, string> answers,
        IReadOnlyDictionary<string, ArbiterStep1Score>? scores)
    {
        if (scores is null)
        {
            return answers;
        }

        Dictionary<string, string> floored = new(answers, StringComparer.Ordinal);
        foreach (KeyValuePair<string, ArbiterStep1Score> pair in scores)
        {
            if (pair.Value.Threshold is double floor
                && pair.Value.Confidence is double confidence
                && confidence < floor
                && floored.TryGetValue(pair.Key, out string? answer)
                && !string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
            {
                floored[pair.Key] = RuleEngine.Undecidable;
            }
        }

        return floored;
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

    private static string ProviderStatusFor(TriggerPlan plan, Step1Call call) =>
        plan.ShortCircuits
            ? ArbiterProviderStatuses.ShortCircuited
            : plan.Questions.Count == 0
                ? ArbiterProviderStatuses.NotEvaluated
                : call.Status;

    /// <summary>The run store under this evaluator's Arbiter directory, sized by <c>arbiter.stores</c>.</summary>
    private ArbiterRunStore ReviewStore(KyberWeaveConfig config) =>
        new(
            _ledger.DirectoryPath,
            new ArbiterStoreOptions(config.Arbiter.Stores.MaxItemBytes, config.Arbiter.Stores.RetainDays),
            () => _clock.GetUtcNow());

    /// <summary>
    /// Appends a lens return's findings to the review store, in return order. Each carries its own drop status,
    /// the one its own judgement reached.
    /// </summary>
    private static async Task RecordReturnedFindingsAsync(
        ArbiterRunStore store,
        string reviewRun,
        IReadOnlyList<ReturnedFinding> findings,
        IReadOnlyList<bool> dropped)
    {
        for (int index = 0; index < findings.Count; index++)
        {
            await store.AppendFindingAsync(reviewRun, ReturnedFinding.StoredText(findings[index].Text, dropped[index]))
                .ConfigureAwait(false);
        }
    }

    /// <summary>A refutation's finding slug: the part after the lens in <c>&lt;lens&gt;/&lt;slug&gt;</c>.</summary>
    private static string FindingSlug(string refute)
    {
        int slash = refute.LastIndexOf('/');
        return slash < 0 ? refute : refute[(slash + 1)..];
    }

    /// <summary>The id of the earlier finding a stored candidate text names, or null when the text names none.</summary>
    private static string? EarlierIdOf(string? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        IReadOnlyList<ReturnedFinding> parsed = ReturnedFinding.ParseAll(candidate);
        return parsed.Count == 0 ? null : parsed[0].Id;
    }

    /// <summary>
    /// Reads one finding's step-1 answers into decision rules and effects. An expanded for-each rule (one question per
    /// candidate) keeps one entry per rule: the first candidate whose answer has an effect, else the first candidate.
    /// A lens finding's entries are tagged with that finding's id, so the record says which finding each judged.
    /// </summary>
    private static JudgedFinding JudgeFinding(
        FindingJudgement judgement,
        string recordId,
        string? refutedSlug)
    {
        ArbiterTriggerFamily family = judgement.Plan.Family;
        List<ArbiterDecisionRule> rules = Step0Rules(judgement.Plan);
        List<string> effects = new(Step0EffectsOf(judgement.Plan));
        List<(string RuleId, string? Candidate, bool Decisive, ArbiterDecisionRule Entry)> rows = [];
        HashSet<string> decisiveRules = new(StringComparer.Ordinal);
        foreach (Question question in judgement.Plan.Questions)
        {
            string answer = AnswerFor(question, judgement.Call);
            string mapped = MapOrNotEvaluated(question.Rule, answer, family);
            ArbiterStep1Score? score = judgement.Call.Scores is not null
                && judgement.Call.Scores.TryGetValue(question.Rule.Id, out ArbiterStep1Score? found)
                ? found
                : null;
            (string effect, bool? shadow, string? wouldEffect) = ShadowState(
                question.Rule, mapped, ArbiterTriggerFamilies.NeutralEffect(family));
            string? evidence = string.Equals(mapped, RuleEffects.Duplicate, StringComparison.Ordinal)
                && refutedSlug is not null
                && EarlierIdOf(question.Candidate) is { } earlierId
                ? ArbiterFindingNotices.DuplicateLine(refutedSlug, earlierId, recordId)
                : null;
            ArbiterDecisionRule entry = Step1Rule(question, answer, effect, score, shadow, wouldEffect, evidence);
            bool decisive = !string.Equals(mapped, RuleEffects.Allow, StringComparison.Ordinal);
            if (decisive && question.Candidate is not null)
            {
                decisiveRules.Add(question.RuleId);
            }

            rows.Add((question.RuleId, question.Candidate, decisive, entry));
            if (!string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
            {
                effects.Add(effect);
            }
        }

        // Every candidate of a for-each rule is recorded, unless one decided: then the record keeps the decisive one.
        HashSet<string> placed = new(StringComparer.Ordinal);
        foreach ((string ruleId, string? candidate, bool decisive, ArbiterDecisionRule entry) in rows)
        {
            if (candidate is null || !decisiveRules.Contains(ruleId))
            {
                rules.Add(entry);
            }
            else if (decisive && placed.Add(ruleId))
            {
                rules.Add(entry);
            }
        }

        if (judgement.FindingId is not null)
        {
            for (int index = 0; index < rules.Count; index++)
            {
                rules[index] = rules[index].Candidate is null
                    ? rules[index] with { Candidate = judgement.FindingId }
                    : rules[index];
            }
        }

        return new JudgedFinding(judgement.FindingId, rules, effects);
    }

    /// <summary>
    /// A drop that another rule's annotation outranks stays in the record, marked suppressed, so the decision
    /// log still shows the filter's view of the finding.
    /// </summary>
    private static void MarkSuppressedDrops(List<ArbiterDecisionRule> rules, string outcome)
    {
        if (string.Equals(outcome, RuleEffects.Drop, StringComparison.Ordinal))
        {
            return;
        }

        for (int index = 0; index < rules.Count; index++)
        {
            if (string.Equals(rules[index].Effect, RuleEffects.Drop, StringComparison.Ordinal))
            {
                rules[index] = rules[index] with { Suppressed = true };
            }
        }
    }

    private static string AnswerFor(Question question, Step1Call call) =>
        call.Answers.TryGetValue(question.Rule.Id, out string? answer) ? answer : RuleEngine.Undecidable;

    /// <summary>
    /// Records the dispatch's one primary decision: every judged finding's rules and effects, combined into the
    /// outcome. The provider record is the primary call's; a later finding's call is judged but not itemised.
    /// </summary>
    private async Task<ArbiterDispatchEvaluation> RecordPrimaryAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        FindingJudgement primary,
        IReadOnlyList<JudgedFinding> judged,
        string recordId,
        KyberWeaveConfig config,
        IReadOnlyList<ArbiterRule> effectiveRules,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        List<ArbiterDecisionRule> rules = [];
        List<string> effects = [];
        foreach (JudgedFinding finding in judged)
        {
            rules.AddRange(finding.Rules);
            effects.AddRange(finding.Effects);
        }

        string outcome = ArbiterTriggerFamilies.Combine(primary.Plan.Family, effects);
        MarkSuppressedDrops(rules, outcome);
        ArbiterProviderRecord provider = ProviderRecord(
            config, primary.Call, ProviderStatusFor(primary.Plan, primary.Call), elapsed);
        await AppendRecordAsync(
            classification, ev, primary.Plan.Trigger, rules, outcome, provider, primaryLedgerId: null,
            effective, ledgerId, planDigest, config, effectiveRules, elapsed, cancellationToken,
            recordId: recordId).ConfigureAwait(false);
        return new ArbiterDispatchEvaluation(primary.Plan.Trigger, outcome);
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
        IReadOnlyList<ArbiterRule> effectiveRules,
        ArbiterFactSet facts,
        ArbiterModelTargets? table,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        string? priorDecisionId,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        string neutral = SecondaryTriggers.NeutralEffect();
        bool primaryAllows = string.Equals(primaryOutcome, RuleEffects.Allow, StringComparison.Ordinal);
        // The record's own id is allocated up front: a same-failure advisory names the record it is written on.
        string secondaryRecordId = ArbiterRecordId.New(_clock.GetUtcNow());
        List<ArbiterDecisionRule> rules = Step0Rules(plan);
        List<string> effects = new(Step0EffectsOf(plan));
        List<string> payload = [];
        foreach (Question question in plan.Questions)
        {
            string answer = AnswerFor(question, call);
            string mapped = MapOrNotEvaluated(question.Rule, answer, plan.Family);
            ArbiterStep1Score? score = call.Scores is not null && call.Scores.TryGetValue(question.Rule.Id, out ArbiterStep1Score? found)
                ? found
                : null;
            if (string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
            {
                rules.Add(Step1Rule(question, answer, RuleEffects.Allow, score, null, null, null));
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
                effects.Add(neutral);
            }
            else
            {
                string? evidence = string.Equals(mapped, RuleEffects.SameFailure, StringComparison.Ordinal) && priorDecisionId is not null
                    ? ArbiterFailureNotices.SameFailureLine(priorDecisionId, secondaryRecordId)
                    : null;
                rules.Add(Step1Rule(question, answer, mapped, score, null, null, evidence));
                effects.Add(mapped);
            }
        }

        string outcome = ArbiterTriggerFamilies.Combine(plan.Family, effects);
        ArbiterModelBlock? model = string.Equals(plan.Trigger, ModelTargetTrigger, StringComparison.Ordinal)
            ? ModelTargetResolver.Resolve(
                rules, facts, config.Arbiter.ModelTargeting, table,
                ev with { RenderedModelToken = RenderedTokenOf(ev, classification.Target, table) },
                config.Arbiter.ModelTargeting.Mode)
            : null;
        if (model is { Delivered: true, Token: { } delivered }
            && effective.ConfirmModelWrite is { } confirm
            && !confirm(delivered))
        {
            model = model with { Delivered = false, Reason = ModelTargetResolver.ModelWriteFailed };
        }

        ArbiterProviderRecord provider = ProviderRecord(config, call, ProviderStatusFor(plan, call), elapsed);
        await AppendRecordAsync(
            classification, ev, plan.Trigger, rules, outcome, provider, primaryLedgerId: ledgerId,
            effective, ledgerId, planDigest, config, effectiveRules, elapsed, cancellationToken, model: model,
            recordId: secondaryRecordId)
            .ConfigureAwait(false);
        string? answered = payload.Count == 0 ? null : string.Join(",", payload);
        return new ArbiterSecondaryEvaluation(plan.Trigger, outcome, model?.Token ?? answered)
        {
            DecisionId = secondaryRecordId,
        };
    }

    /// <summary>
    /// The step-0 effects a plan contributes to its outcome. A shortcut's undecidable step-0 answer is not a verdict
    /// when step 1 asks that rule: the step-1 answer decides it, so the step-0 spawn it would map to must not block an answer.
    /// </summary>
    private static List<string> Step0EffectsOf(TriggerPlan plan)
    {
        List<string> effects = [];
        foreach (ArbiterRuleAnswer answer in plan.Step0.RuleAnswers)
        {
            if (!DecidedByStepOne(plan, answer))
            {
                effects.Add(answer.Effect);
            }
        }

        return effects;
    }

    /// <summary>
    /// Whether a rule's undecidable step-0 answer is superseded by the step-1 question the same rule asked. Only the
    /// shortcut and same-failure families hold this shape: their undecidable maps to a spawn or an allow that must
    /// not stand in for the answer step 1 gives.
    /// </summary>
    private static bool DecidedByStepOne(TriggerPlan plan, ArbiterRuleAnswer answer) =>
        (plan.Family == ArbiterTriggerFamily.Shortcut || plan.Family == ArbiterTriggerFamily.FailureCluster)
        && string.Equals(answer.Answer, RuleEngine.Undecidable, StringComparison.Ordinal)
        && plan.Questions.Any(question => string.Equals(question.RuleId, answer.RuleId, StringComparison.Ordinal));

    /// <summary>The step-0 decision entries of a plan, without the undecidable entry a step-1 question superseded.</summary>
    private static List<ArbiterDecisionRule> Step0Rules(TriggerPlan plan)
    {
        List<ArbiterDecisionRule> rules = new(plan.Step0.RuleAnswers.Count);
        foreach (ArbiterRuleAnswer answer in plan.Step0.RuleAnswers)
        {
            if (DecidedByStepOne(plan, answer))
            {
                continue;
            }

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

    private static string MapOrNotEvaluated(ArbiterRule rule, string answer, ArbiterTriggerFamily family) =>
        string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
            ? RuleEffects.Allow
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
        string? primaryLedgerId,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string? planDigest,
        KyberWeaveConfig config,
        IReadOnlyList<ArbiterRule> effectiveRules,
        Stopwatch elapsed,
        CancellationToken cancellationToken,
        ArbiterModelBlock? model = null,
        string? recordId = null)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        classification.Headers.TryGetValue("PLAN_FILE", out string? planFile);
        classification.Headers.TryGetValue("TASK", out string? task);
        string repeatKey = rules.Count > 0 ? rules[0].Id : trigger;
        int repeat = _decisionLog.Repeat(repeatKey, planFile, planDigest, task, ev.Session);
        ArbiterDecisionRecord record = new(
            recordId ?? ArbiterRecordId.New(now),
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
            RuleSetDigest(effectiveRules),
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
        IReadOnlyList<string>? taskFiles,
        ArbiterModelTargets? table)
    {
        string phase = MapPhase(ev);
        (string? modelProfile, string? modelToken, string? modelSource) = PreModelOf(ev, first, phase, table);
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
            ModelProfile = modelProfile,
            ModelToken = modelToken,
            ModelSource = modelSource,
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
    /// The token the target agent is rendered with: the one the harness reported on the event, or else the
    /// target's entry in the model targets table, which is what the rendered agent file carries.
    /// </summary>
    private static string? RenderedTokenOf(ArbiterEvent ev, string? target, ArbiterModelTargets? table) =>
        ev.RenderedModelToken
        ?? (table is not null && target is not null && table.Agents.TryGetValue(target, out ArbiterModelTargetsAgent? agent)
            ? agent.Token
            : null);

    /// <summary>
    /// The model fields of a delegate pre event. A caller-named model is the model that runs, so it is the token
    /// and carries no profile. Otherwise the target's table entry gives the profile and token, and the source says
    /// whether the model was the caller's, the rendered agent's, or neither.
    /// </summary>
    private static (string? Profile, string? Token, string? Source) PreModelOf(
        ArbiterEvent ev,
        TriggerClassification first,
        string phase,
        ArbiterModelTargets? table)
    {
        if (phase != ArbiterLedgerPhases.Pre || !string.Equals(first.Trigger, "delegate", StringComparison.Ordinal))
        {
            return (null, null, null);
        }

        string source = ev.Model is not null
            ? "caller"
            : ev.RenderedModelToken is not null ? "rendered" : "unknown";
        if (ev.Model is not null)
        {
            return (null, ev.Model, source);
        }

        ArbiterModelTargetsAgent? agent = table is not null
            && first.Target is not null
            && table.Agents.TryGetValue(first.Target, out ArbiterModelTargetsAgent? found)
                ? found
                : null;
        return (agent?.Profile, agent?.Token, source);
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
