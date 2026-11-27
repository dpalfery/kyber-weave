using System.Security.Cryptography;
using System.Text;
using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The single entry point the hook host, <c>serve</c> and <c>eval</c> call,
/// running the evaluation flow end to end.
/// </summary>
/// <remarks>
/// Every collaborator arrives through the constructor: the provider, the key
/// resolver, the ledger, the decision log, git facts, the plan reader, the
/// model targets reader and the clock. Core constructs none of them. The ledger event is appended before
/// evaluation so a hook killed mid-evaluation still leaves an undecided entry;
/// one decision record per trigger is appended after, the primary first and
/// each secondary behind it, all sharing the event's ledger id. A provider
/// failure degrades to <c>undecidable</c>, which every family maps to its proceeding
/// effect; any other exception becomes an error result that carries
/// <c>KW-ARB-HOOK-001</c> and allows, because the Arbiter never blocks on a decision it
/// could not reach. The key is resolved to prove the origin is served but is never
/// logged.
/// </remarks>
public sealed class ArbiterEvaluator : IArbiterEvaluator
{
    /// <summary>The hook error code every evaluator failure carries.</summary>
    public const string HookErrorCode = "KW-ARB-HOOK-001";

    private readonly IArbiterProvider _provider;
    private readonly Func<Uri, string?> _keyResolver;
    private readonly IArbiterLedger _ledger;
    private readonly IArbiterDecisionLog _decisionLog;
    private readonly IArbiterGitFacts _gitFacts;
    private readonly IArbiterPlanReader _planReader;
    private readonly IArbiterModelTargetsReader _modelTargets;
    private readonly IGateReportFacts _gateReports;
    private readonly IArbiterStateFactory _state;
    private readonly IFileSystem? _files;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly Func<string, ArbiterEvent, IReadOnlyList<string>> _secondaryTriggers;
    private readonly string? _modelTargetsDirectory;

    /// <summary>Creates an evaluator over already-constructed collaborators.</summary>
    /// <param name="provider">The step-1 provider.</param>
    /// <param name="keyResolver">Resolves the provider key for an endpoint; the key is never logged.</param>
    /// <param name="ledger">The in-flight ledger.</param>
    /// <param name="decisionLog">The decision log.</param>
    /// <param name="gitFacts">Git-derived facts.</param>
    /// <param name="planReader">Plan-derived facts.</param>
    /// <param name="modelTargets">Reads the harness's model targets table.</param>
    /// <param name="gateReports">Reads the gate reports a return cites.</param>
    /// <param name="state">Opens the run stores under the arbiter directory.</param>
    /// <param name="clock">The evaluation clock.</param>
    /// <param name="ids">Makes the ledger and decision record ids.</param>
    /// <param name="secondaryTriggers">Derives the secondary triggers of a primary dispatch; defaults to none.</param>
    /// <param name="modelTargetsDirectory">
    /// The directory holding the harness's model targets table, or null for the ledger's own directory. A
    /// global-scope evaluation reads the table a user-scope install deployed under the harness's global root,
    /// not the per-project state directory its ledger lives in.
    /// </param>
    /// <param name="files">The file system the standards are read through to resolve a technology's parent; null resolves none.</param>
    public ArbiterEvaluator(
        IArbiterProvider provider,
        Func<Uri, string?> keyResolver,
        IArbiterLedger ledger,
        IArbiterDecisionLog decisionLog,
        IArbiterGitFacts gitFacts,
        IArbiterPlanReader planReader,
        IArbiterModelTargetsReader modelTargets,
        IGateReportFacts gateReports,
        IArbiterStateFactory state,
        TimeProvider clock,
        IIdGenerator ids,
        Func<string, ArbiterEvent, IReadOnlyList<string>>? secondaryTriggers = null,
        string? modelTargetsDirectory = null,
        IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(keyResolver);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisionLog);
        ArgumentNullException.ThrowIfNull(gitFacts);
        ArgumentNullException.ThrowIfNull(planReader);
        ArgumentNullException.ThrowIfNull(modelTargets);
        ArgumentNullException.ThrowIfNull(gateReports);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);
        _provider = provider;
        _keyResolver = keyResolver;
        _ledger = ledger;
        _decisionLog = decisionLog;
        _gitFacts = gitFacts;
        _planReader = planReader;
        _modelTargets = modelTargets;
        _gateReports = gateReports;
        _state = state;
        _clock = clock;
        _ids = ids;
        _secondaryTriggers = secondaryTriggers ?? SecondaryTriggers.For;
        _modelTargetsDirectory = modelTargetsDirectory;
        _files = files;
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
        long elapsed = _clock.GetTimestamp();
        // Under the tiered roster the model-targeting block and the two rules follow the developer tiers; the
        // configuration a host loads keeps the shipped block, and this is the run's effective one.
        ArbiterModelTargetingConfig targeting = ModelTargetingRules.Targeting(config.Arbiter.ModelTargeting);
        IReadOnlyList<ArbiterRule> rules = ModelTargetingRules.Apply(config.Arbiter.Rules, targeting.Mode);
        ArbiterModelTargets? table = _modelTargets.TryRead(_modelTargetsDirectory ?? _ledger.DirectoryPath, ev.Harness);

        // Allocated outside the try so the error result can name the ledger entry that
        // was already appended. A fault after the append leaves exactly one record that
        // explains the run, and returning an empty id here made it unfindable.
        string ledgerId = string.Empty;
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            IReadOnlyList<TriggerClassification> classifications = TriggerClassifier.Classify(ev);
            List<string> prompts = LedgerProjection.PromptsFor(ev);
            ledgerId = _ids.NewSortableId(now);
            if (effective.DryRun)
            {
                // Dry run reads and writes nothing: the reads below prove the
                // ledger is consulted without appending to either file.
                _ = _ledger.ReadAll();
                _ = _decisionLog.ReadAll();
                _ = LedgerProjection.BuildLedgerEntry(
                    ledgerId, now, effective.Source, ev, classifications, _planReader, table,
                    Recording.RunScopeOf(_gitFacts, ev));
            }
            else
            {
                await _ledger.AppendAsync(
                    LedgerProjection.BuildLedgerEntry(
                        ledgerId, now, effective.Source, ev, classifications, _planReader, table,
                        Recording.RunScopeOf(_gitFacts, ev)),
                    cancellationToken).ConfigureAwait(false);
            }

            // Origin-bound key check: the key is resolved but never stored or logged.
            Recording.ResolveKey(_keyResolver, config);

            List<ArbiterDispatchEvaluation> dispatches = new(classifications.Count);
            int count = Math.Min(classifications.Count, prompts.Count);
            for (int index = 0; index < count; index++)
            {
                TriggerClassification classification = classifications[index];
                string prompt = prompts[index];
                ArbiterDispatchEvaluation evaluated = await EvaluateDispatchAsync(
                    classification, prompt, ev, config, rules, targeting, table, effective, ledgerId, elapsed,
                    cancellationToken)
                    .ConfigureAwait(false);
                dispatches.Add(evaluated);
            }

            string outcome = LedgerProjection.CombineEventOutcome(dispatches);
            return new ArbiterEvaluationResult(outcome, false, null, null, dispatches, ledgerId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Fail open: an evaluator exception is an error result that allows. The caller logs
            // KW-ARB-HOOK-001 with the message, and the ledger entry already appended keeps the trail.
            // ledgerId is empty only when the fault landed before the id was allocated.
            return new ArbiterEvaluationResult(
                RuleEffects.Allow, true, HookErrorCode, ex.Message, [], ledgerId);
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
        List<Question> Unanswerable,
        List<Question> NoCandidates,
        IReadOnlyDictionary<string, string>? Step0Evidence = null);

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

    /// <summary>What the fact assembly and the return/failure reads produced for one dispatch.</summary>
    private sealed record DispatchPreparation(
        ArbiterFactSet Facts,
        IReadOnlyList<ArbiterRule> EventRules,
        string? ReviewRun,
        IReadOnlyList<ReturnedFinding> Returned,
        bool GateCorroborated,
        string? RefutedSlug,
        string? PlanDigest,
        string? PriorDecisionId);

    /// <summary>The return, refutation and gate evidence read for one dispatch.</summary>
    private sealed record ReturnFacts(
        ArbiterFactSet Facts,
        string? ReviewRun,
        IReadOnlyList<ReturnedFinding> Returned,
        bool GateCorroborated,
        string? RefutedSlug);

    /// <summary>The plans one dispatch formed, the batched call that answered them, and the findings judged.</summary>
    private sealed record DispatchPlanning(
        TriggerPlan Primary,
        FindingJudgement PrimaryJudgement,
        List<TriggerPlan> Secondaries,
        Step1Call SecondaryCall,
        List<JudgedFinding> Judged,
        int JudgedCount);

    private async Task<ArbiterDispatchEvaluation> EvaluateDispatchAsync(
        TriggerClassification classification,
        string prompt,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        IReadOnlyList<ArbiterRule> rules,
        ArbiterModelTargetingConfig targeting,
        ArbiterModelTargets? table,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        long elapsed,
        CancellationToken cancellationToken)
    {
        string? trigger = classification.Trigger;
        if (trigger is null)
        {
            return new ArbiterDispatchEvaluation(null, RuleEffects.Allow);
        }

        // The primary's decision record id, allocated up front: a stored failure names the decision that judged it.
        string recordId = _ids.NewSortableId(_clock.GetUtcNow());

        // Derived once: the bag declares the facts of every secondary, and the loop below plans them.
        // Derived from this dispatch's own target and prompt: a multi-dispatch event carries a target per dispatch,
        // not on the event, and a marker on one batched dispatch never derives a shortcut for another.
        IReadOnlyList<string> secondaryNames = _secondaryTriggers(
            trigger, ev with { Target = classification.Target, Prompt = prompt });

        DispatchPreparation prepared = await PrepareDispatchAsync(
            classification, prompt, ev, config, rules, targeting, effective, ledgerId, recordId, trigger, secondaryNames,
            cancellationToken).ConfigureAwait(false);
        DispatchPlanning planning = await PlanAndAskAsync(
            config, trigger, prepared, recordId, secondaryNames, cancellationToken).ConfigureAwait(false);
        return await RecordDispatchOutcomeAsync(
            classification, prompt, ev, config, targeting, table, effective, ledgerId, elapsed, recordId,
            prepared, planning, trigger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the dispatch's fact bag, scopes the rules, and reads the return, refutation and prior-failure facts.
    /// </summary>
    private async Task<DispatchPreparation> PrepareDispatchAsync(
        TriggerClassification classification,
        string prompt,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        IReadOnlyList<ArbiterRule> rules,
        ArbiterModelTargetingConfig targeting,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string recordId,
        string trigger,
        IReadOnlyList<string> secondaryNames,
        CancellationToken cancellationToken)
    {
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            classification, prompt, config, ev.ObservesReturns, ev.ModelOverrideField, ev.Model, secondaryNames,
            ev.PromptInserted(), ev.Cwd, _files);
        facts = _gitFacts.Enrich(facts, classification, ev);
        facts = GovernedPathsFacts.Enrich(facts, config.Ontology.DocsRoots);
        facts = _planReader.Enrich(facts, classification);
        facts = TriggerFactBuilder.WithTestsOnlyTaskFact(facts, config.Arbiter.TestFilePatterns);
        facts = TriggerFactBuilder.WithTaskGatesApplyFact(
            facts, config, classification.Target, prompt, Recording.ParentTechnologyOf(_files, config, classification, ev.Cwd));
        ArbiterRunScope? runScope = Recording.RunScopeOf(_gitFacts, ev);
        facts = LedgerFacts.Enrich(facts, _ledger, _decisionLog, ev, ledgerId, runScope);
        facts = AttributedChangeFacts.Enrich(facts, _ledger, _decisionLog, ledgerId);
        facts = ModelTargetFacts.Enrich(facts, _ledger, _decisionLog, ledgerId, targeting);
        // D29: a return's TASK_GATE: line is checked against the report it names. Absent
        // evidence answers absent, and the rule asks the model about the raw report instead;
        // an unexpected failure here reaches the catch in EvaluateAsync, which allows and logs.
        facts = _gateReports.Enrich(facts, ev);
        facts = Recording.WithTaskReturns(Recording.ReviewStore(_state, _ledger, _clock, config), facts, runScope);

        // A technology folder's rules judge only a task of that technology or of one that builds on it. Role
        // folder rules and arbiter.rules carry no technology and judge every event, and an event that names no
        // task technology is judged by those alone.
        IReadOnlyList<ArbiterRule> eventRules = ArbiterRuleScopes.Applicable(
            rules, LedgerProjection.EventTechnology(classification, facts), config.Arbiter.RuleFolderParents);

        ReturnFacts returns = ApplyReturnFacts(facts, prompt, trigger, ev, config);

        // Read once here and reused for both REPEAT and the record, so the count and the
        // entry it is written beside are keyed on the same digest.
        string? planDigest = returns.Facts.TryGet("plan.digest", out ArbiterFact? digestFact)
            ? digestFact.Value as string
            : null;
        (ArbiterFactSet failedFacts, string? priorDecisionId) = await ApplyPriorFailureAsync(
            returns.Facts, classification, prompt, planDigest, secondaryNames, recordId, ev, config,
            effective.DryRun, cancellationToken).ConfigureAwait(false);

        return new DispatchPreparation(
            failedFacts, eventRules, returns.ReviewRun, returns.Returned, returns.GateCorroborated, returns.RefutedSlug,
            planDigest, priorDecisionId);
    }

    /// <summary>Reads the return's findings, the review run and the refuted finding's stored status into the facts.</summary>
    private ReturnFacts ApplyReturnFacts(
        ArbiterFactSet facts,
        string prompt,
        string trigger,
        ArbiterEvent ev,
        KyberWeaveConfig config)
    {
        // The review's REVIEW_RUN names the store its findings sit in. A lens return's first finding is the
        // one the nitpick filter reads; a refutation reads whether the filter dropped the finding it refutes.
        string? reviewRun = HeaderBlock.Parse(prompt).Headers.TryGetValue(DispatchHeaders.ReviewRun, out string? run) ? run : null;
        IReadOnlyList<ReturnedFinding> returned = string.Equals(trigger, ArbiterTriggers.LensReturned, StringComparison.Ordinal)
            ? ReturnedFinding.ParseAll(ev.ToolOutput)
            : [];
        // Each finding is judged on its own bag: the static facts plus that finding's own id, text and severity.
        ArbiterFactSet baseFacts = facts;
        bool gateCorroborated = _gateReports.FailedGateCorroborates(ev.Cwd, reviewRun);
        if (returned.Count > 0)
        {
            facts = TriggerFactBuilder.WithReturnedFinding(baseFacts, returned[0], gateCorroborated);
        }

        // A refutation names its finding as <lens>/<slug>; the store and the notices carry the bare slug.
        string? refutedSlug = null;
        if (reviewRun is not null
            && string.Equals(trigger, ArbiterTriggers.RefuteSpawn, StringComparison.Ordinal)
            && facts.TryGet("finding.id", out ArbiterFact? refuted)
            && refuted.Value is string refutedId)
        {
            refutedSlug = ReturnedFinding.IdKey(refutedId);
            IReadOnlyList<string> stored = Recording.ReviewStore(_state, _ledger, _clock, config).ReadFindings(reviewRun);
            facts = TriggerFactBuilder.WithDroppedStatus(facts, refutedSlug, stored);
            facts = TriggerFactBuilder.WithPriorFindings(facts, refutedSlug, stored);
        }

        return new ReturnFacts(facts, reviewRun, returned, gateCorroborated, refutedSlug);
    }

    /// <summary>Reads and stores a rework's last cluster failure, so the next rework of the cluster has a prior.</summary>
    private async Task<(ArbiterFactSet Facts, string? PriorDecisionId)> ApplyPriorFailureAsync(
        ArbiterFactSet facts,
        TriggerClassification classification,
        string prompt,
        string? planDigest,
        IReadOnlyList<string> secondaryNames,
        string recordId,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // A rework dispatch reads the failure its cluster stored last, then stores this one, so the next rework of
        // the cluster has a prior to compare. The store is written on every evaluation but a dry run.
        if (!secondaryNames.Contains(FailureFacts.Trigger)
            || FailureFacts.ClusterOf(prompt) is not { } cluster
            || FailureFacts.CurrentTextOf(prompt) is not { } currentFailure)
        {
            return (facts, null);
        }

        classification.Headers.TryGetValue(DispatchHeaders.PlanFile, out string? failurePlan);
        IArbiterRunStore failures = Recording.ReviewStore(_state, _ledger, _clock, config);
        FailureRecord? prior = FailureFacts.LastFor(failures.ReadFailures(failurePlan, planDigest, ev.Session), cluster);
        facts = facts
            .With(FailureFacts.PreviousText, prior?.Text, ArbiterFactLabel.Derived)
            .With(FailureFacts.CurrentText, currentFailure, ArbiterFactLabel.Derived);
        if (!dryRun)
        {
            await failures.AppendFailureAsync(
                failurePlan, planDigest, ev.Session,
                FailureFacts.Stored(cluster, recordId, currentFailure), cancellationToken).ConfigureAwait(false);
        }

        return (facts, prior?.DecisionId);
    }

    /// <summary>Plans the primary and its secondaries, makes the batched provider call, and judges each finding.</summary>
    private async Task<DispatchPlanning> PlanAndAskAsync(
        KyberWeaveConfig config,
        string trigger,
        DispatchPreparation prepared,
        string recordId,
        IReadOnlyList<string> secondaryNames,
        CancellationToken cancellationToken)
    {
        // Primary and secondary read one fact set and one candidate map, so their questions
        // can share a single provider call.
        Dictionary<string, ArbiterFact> candidateFacts = new(StringComparer.Ordinal);
        TriggerPlan primary = Judging.PlanTrigger(trigger, prepared.Facts, prepared.EventRules, candidateFacts, config.WorkerRoster);

        // Step 0 of the primary short-circuits: no provider call, and no secondary work.
        List<TriggerPlan> secondaries = [];
        if (!primary.ShortCircuits)
        {
            foreach (string secondary in secondaryNames)
            {
                if (prepared.EventRules.Any(rule => rule.Enabled
                    && string.Equals(rule.Trigger, secondary, StringComparison.Ordinal)))
                {
                    secondaries.Add(Judging.PlanTrigger(secondary, prepared.Facts, prepared.EventRules, candidateFacts, config.WorkerRoster));
                }
            }
        }

        ArbiterFactSet batchFacts = candidateFacts.Aggregate(
            prepared.Facts,
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
        Step1Call combined = await Recording.AskAsync(_provider,
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
            primaryCall = await Recording.AskAsync(_provider, primaryQuestions, batchFacts, step0Answers, false, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            primaryCall = Judging.Restrict(combined, primaryQuestions);
        }

        // A lens return judges every finding it carries. The first rides the batch above; each later one is asked
        // alone on its own fact bag, so its step-0 gates and its step-1 answer read its own id, text and severity.
        FindingJudgement primaryJudgement = new(
            prepared.Returned.Count > 0 ? prepared.Returned[0].Id : null, primary, primaryCall);
        // Findings past the question budget are kept unjudged: they stay real and are never asked, so a long return
        // costs at most MaxQuestions questions and no finding is dropped on no answer.
        int judgedCount = Math.Min(prepared.Returned.Count, StateBudget.MaxQuestions);
        List<FindingJudgement> judgements = [primaryJudgement];
        for (int index = 1; index < judgedCount; index++)
        {
            ArbiterFactSet findingFacts = TriggerFactBuilder.WithReturnedFinding(
                prepared.Facts, prepared.Returned[index], prepared.GateCorroborated);
            Dictionary<string, ArbiterFact> findingCandidates = new(StringComparer.Ordinal);
            TriggerPlan findingPlan = Judging.PlanTrigger(trigger, findingFacts, prepared.EventRules, findingCandidates, config.WorkerRoster);
            ArbiterFactSet findingBatch = findingCandidates.Aggregate(
                findingFacts, (set, pair) => set.With(pair.Key, pair.Value.Value, pair.Value.Label));
            Step1Call findingCall = await Recording.AskAsync(_provider,
                findingPlan.Questions, findingBatch, findingPlan.Step0Answers, false, cancellationToken).ConfigureAwait(false);
            judgements.Add(new FindingJudgement(prepared.Returned[index].Id, findingPlan, findingCall));
        }

        // One decision record per dispatch: every finding's rules sit on it, tagged with the finding they judged.
        List<JudgedFinding> judged = judgements
            .Select(judgement => Judging.JudgeFinding(judgement, recordId, prepared.RefutedSlug))
            .ToList();
        return new DispatchPlanning(primary, primaryJudgement, secondaries, secondaryCall, judged, judgedCount);
    }

    /// <summary>Writes the dispatch's decision records and returns its combined outcome.</summary>
    private async Task<ArbiterDispatchEvaluation> RecordDispatchOutcomeAsync(
        TriggerClassification classification,
        string prompt,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterModelTargetingConfig targeting,
        ArbiterModelTargets? table,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        long elapsed,
        string recordId,
        DispatchPreparation prepared,
        DispatchPlanning planning,
        string trigger,
        CancellationToken cancellationToken)
    {
        // Log-only notes the record carries: a missing review run, and a return's findings past the question budget.
        List<ArbiterDecisionRule> notes = [];
        string neutralOfTrigger = ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamilies.ForTrigger(trigger));
        bool lensDispatch = string.Equals(trigger, ArbiterTriggers.LensReturned, StringComparison.Ordinal)
            || string.Equals(trigger, ArbiterTriggers.LensSpawn, StringComparison.Ordinal);
        if (lensDispatch && prepared.ReviewRun is null)
        {
            notes.Add(Judging.NoteRow("review-run", neutralOfTrigger, ReviewRunMissingNote));
        }

        int notJudged = prepared.Returned.Count - planning.JudgedCount;
        if (notJudged > 0)
        {
            notes.Add(Judging.NoteRow("findings-over-cap", neutralOfTrigger, Judging.FindingsOverCapNote(notJudged)));
        }

        ArbiterDispatchEvaluation primaryResult = await Recording.RecordPrimaryAsync(_decisionLog, _ids, _clock,
            classification, ev, planning.PrimaryJudgement, planning.Judged, notes, recordId, config, prepared.EventRules, effective,
            ledgerId, prepared.PlanDigest, elapsed, cancellationToken).ConfigureAwait(false);
        string primaryOutcome = primaryResult.Outcome;
        await RecordFollowUpsAsync(
            classification, prompt, ev, config, effective, ledgerId, trigger, prepared, planning, primaryOutcome,
            cancellationToken).ConfigureAwait(false);
        List<ArbiterSecondaryEvaluation> secondaryResults = await RecordSecondariesAsync(
            classification, ev, config, targeting, table, effective, ledgerId, elapsed, prepared, planning,
            primaryOutcome, cancellationToken).ConfigureAwait(false);
        return new ArbiterDispatchEvaluation(trigger, CombineDispatchOutcome(trigger, primaryOutcome, secondaryResults))
        {
            Secondary = secondaryResults,
        };
    }

    /// <summary>Records the return's findings, a worker's report tail and a refuter's verdict after the primary.</summary>
    private async Task RecordFollowUpsAsync(
        TriggerClassification classification,
        string prompt,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        string trigger,
        DispatchPreparation prepared,
        DispatchPlanning planning,
        string primaryOutcome,
        CancellationToken cancellationToken)
    {
        if (!effective.DryRun && prepared.ReviewRun is not null && prepared.Returned.Count > 0)
        {
            // One flag per returned finding; a finding past the budget was never judged, so it is kept (not dropped).
            bool[] dropped = Enumerable.Range(0, prepared.Returned.Count)
                .Select(index => index < planning.Judged.Count && string.Equals(
                    Judging.CombineGating(planning.Primary.Family, planning.Judged[index].Effects), RuleEffects.Drop, StringComparison.Ordinal))
                .ToArray();
            await Judging.RecordReturnedFindingsAsync(
                Recording.ReviewStore(_state, _ledger, _clock, config), prepared.ReviewRun, prepared.Returned, dropped)
                .ConfigureAwait(false);
        }

        // A worker's return keeps its report tail beside the plan, so a later dispatch's model question can read
        // what the worker said rather than only the markers the ledger parsed. Never the ledger: it holds no text.
        if (!effective.DryRun
            && string.Equals(trigger, ArbiterTriggers.DelegateReturned, StringComparison.Ordinal)
            && TextExcerpts.ReturnTail(ev.ToolOutput, ReturnTailChars) is { } tail
            && _ledger.DispatchHeadersFor(ledgerId) is { PlanFile: { } returnPlan, Task: { } returnTask })
        {
            await Recording.ReviewStore(_state, _ledger, _clock, config)
                .AppendReturnAsync(
                    LedgerProjection.ReturnsKey(returnPlan, Recording.RunScopeOf(_gitFacts, ev)),
                    $"task: {returnTask}\ntarget: {classification.Target}\n{tail}", cancellationToken)
                .ConfigureAwait(false);
        }

        // A refuter's return records its verdict against the finding its REFUTE header names, keyed by the slug.
        // A return that states no recognised verdict records unknown; it never drops the finding it refutes.
        if (!effective.DryRun && prepared.ReviewRun is not null
            && string.Equals(trigger, ArbiterTriggers.RefuteReturned, StringComparison.Ordinal)
            && HeaderBlock.Parse(prompt).Headers.TryGetValue(DispatchHeaders.Refute, out string? refuteHeader))
        {
            await Recording.ReviewStore(_state, _ledger, _clock, config)
                .AppendRefutationOutcomeAsync(
                    prepared.ReviewRun, ReturnedFinding.IdKey(refuteHeader), RefutationOutcome.Parse(ev.ToolOutput),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Records each secondary trigger's decision behind the primary.</summary>
    private async Task<List<ArbiterSecondaryEvaluation>> RecordSecondariesAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterModelTargetingConfig targeting,
        ArbiterModelTargets? table,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        long elapsed,
        DispatchPreparation prepared,
        DispatchPlanning planning,
        string primaryOutcome,
        CancellationToken cancellationToken)
    {
        List<ArbiterSecondaryEvaluation> secondaryResults = new(planning.Secondaries.Count);
        foreach (TriggerPlan plan in planning.Secondaries)
        {
            ArbiterSecondaryEvaluation evaluated = await Recording.RecordSecondaryAsync(
                _decisionLog, _ids, _clock, classification, ev, plan, planning.SecondaryCall, primaryOutcome, config,
                prepared.EventRules, targeting, prepared.Facts, table, effective, ledgerId, prepared.PlanDigest,
                prepared.PriorDecisionId, elapsed, cancellationToken).ConfigureAwait(false);
            secondaryResults.Add(evaluated);
        }

        return secondaryResults;
    }

    /// <summary>Combines the primary and secondary outcomes into the dispatch's one outcome.</summary>
    private static string CombineDispatchOutcome(
        string trigger,
        string primaryOutcome,
        List<ArbiterSecondaryEvaluation> secondaryResults)
    {
        // The primary "allowed" when it reached its family's allow-equivalent (the neutral effect), not the literal allow:
        // a gate's neutral is applies and a shortcut's is spawn.
        bool primaryAllows = string.Equals(
            primaryOutcome, ArbiterTriggerFamilies.NeutralEffect(ArbiterTriggerFamilies.ForTrigger(trigger)), StringComparison.Ordinal);
        // An answered shortcut is the one secondary effect that gates: it withholds the primary's worker, so the
        // dispatch is answered when the primary allowed it. Every other secondary outcome leaves the primary's alone.
        bool answered = primaryAllows
            && secondaryResults.Any(result => string.Equals(result.Outcome, RuleEffects.Answer, StringComparison.Ordinal));
        // A same-failure advisory is the other secondary effect that reaches the dispatch, when the primary allowed it.
        bool advised = primaryAllows
            && secondaryResults.Any(result => string.Equals(result.Outcome, RuleEffects.SameFailure, StringComparison.Ordinal));
        return answered ? RuleEffects.Answer : advised ? RuleEffects.SameFailure : primaryOutcome;
    }

    /// <summary>The longest report tail one stored return keeps.</summary>
    private const int ReturnTailChars = 2000;

    /// <summary>The most stored returns the <c>ledger.task-returns</c> fact carries, newest kept.</summary>
    private const int MaxTaskReturns = 4;










    /// <summary>The answer a present-but-empty <c>ForEach</c> records. It is not <c>undecidable</c>, so the record never counts it as uncertainty.</summary>
    private const string NoCandidatesAnswer = "no-candidates";

    /// <summary>The evidence a present-but-empty <c>ForEach</c> rule records.</summary>
    private const string NoCandidatesEvidence = "no candidates";










    /// <summary>The note a lens return or spawn carries when its REVIEW_RUN header is missing.</summary>
    private const string ReviewRunMissingNote =
        "review-run-missing: no REVIEW_RUN header, so the findings are judged and not stored";







    /// <summary>The most stray paths <c>KW-ARB-CHECKOUT-001</c>'s evidence names before it counts the rest.</summary>
    private const int MaxStrayPathsNamed = 20;



















    /// <summary>
    /// The model fields of a delegate pre event. A caller-named model is the model that runs, so it is the token
    /// and carries no profile. Otherwise the target's table entry gives the profile and token, and the source says
    /// whether the model was the caller's, the rendered agent's, or neither.
    /// </summary>
    /// <summary>
    /// Whether the harness callback reports the model write held. The callback is caller-supplied, so its failure
    /// types cannot be named here; a throw means the write did not happen, which the decision then records as
    /// <see cref="ModelTargetResolver.ModelWriteFailed"/>. Cancellation is not a write failure and propagates.
    /// </summary>
    private static bool ConfirmHeld(Func<string, bool> confirm, string token)
    {
        try
        {
            return confirm(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }





    /// <summary>Planning, step-0 and step-1 judgement: pure helpers over the dispatch records.</summary>
    private static class Judging
    {
        /// <summary>Plans one trigger: its step-0 answers, and the step-1 questions the step-0 answers leave open.</summary>
        internal static TriggerPlan PlanTrigger(
            string trigger,
            ArbiterFactSet facts,
            IReadOnlyList<ArbiterRule> rules,
            Dictionary<string, ArbiterFact> candidateFacts,
            string? workerRoster)
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
            List<Question> unanswerable = [];
            List<Question> noCandidates = [];
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
                        AddQuestions(rule, facts, candidateFacts, questions, unanswerable, noCandidates);
                    }
                }
            }

            return new TriggerPlan(
                trigger, family, step0, step0Answers, shortCircuits, questions, unanswerable, noCandidates,
                Step0EvidenceOf(step0Answers, facts, workerRoster));
        }

        /// <summary>
        /// Adds a rule's question. A <c>ForEach</c> rule asks once per item of its list fact, each
        /// keyed by rule id plus candidate, with the item bound to a per-item <c>candidate[i].*</c> fact.
        /// A list that is missing or has the wrong shape gives no question to ask; the rule goes to
        /// <paramref name="unanswerable"/> so it still records, as undecidable. A present, empty list is a
        /// definite "no candidates": the rule goes to <paramref name="noCandidates"/>, records with the
        /// family's neutral effect, and is not sent.
        /// </summary>
        internal static void AddQuestions(
            ArbiterRule rule,
            ArbiterFactSet facts,
            Dictionary<string, ArbiterFact> candidateFacts,
            List<Question> questions,
            List<Question> unanswerable,
            List<Question> noCandidates)
        {
            RuleAsk ask = rule.Ask ?? throw new InvalidOperationException(
                $"Rule '{rule.Id}' has no ask to expand.");
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
                    Id = rule.Id + RuleValidator.ExpansionSeparator + index,
                    Ask = ask with { State = state, ForEach = null },
                };
                questions.Add(new Question(rule.Id, item, expanded));
                index++;
            }

            if (index == 0)
            {
                // Present and empty: nothing to ask, and nothing is uncertain. The rule records once as no candidates.
                noCandidates.Add(new Question(rule.Id, null, rule));
            }
        }

        /// <summary>
        /// An answer whose confidence is missing or below its rule's own <c>confidence-at-least</c> floor is undecidable.
        /// The provider client applies the floor itself; the evaluator applies it again so no provider can gate a dispatch
        /// on a score it never proved above its floor. The floor is the rule's, not the score's threshold: a <c>noul</c>
        /// score carries a probability ceiling and no confidence, and its answer stands.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> FloorAnswers(
            IReadOnlyList<ArbiterRule> rules,
            IReadOnlyDictionary<string, string> answers,
            IReadOnlyDictionary<string, ArbiterStep1Score>? scores)
        {
            if (scores is null)
            {
                return answers;
            }

            Dictionary<string, string> floored = new(answers, StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (rule.Ask?.ConfidenceAtLeast is double floor
                    && scores.TryGetValue(rule.Id, out ArbiterStep1Score? score)
                    && (score.Confidence is null || score.Confidence < floor)
                    && floored.TryGetValue(rule.Id, out string? answer)
                    && !string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
                {
                    floored[rule.Id] = RuleEngine.Undecidable;
                }
            }

            return floored;
        }

        internal static bool IsProviderFailure(Exception ex, bool guardAll, CancellationToken cancellationToken) =>
            ex is HttpRequestException or IOException or System.Text.Json.JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            || (guardAll && ex is not OperationCanceledException);

        internal static Dictionary<string, string> EmptyAnswers() => new(StringComparer.Ordinal);

        /// <summary>The part of a combined call that answered one trigger's questions.</summary>
        internal static Step1Call Restrict(Step1Call combined, List<Question> questions)
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
        /// Records each rule whose <c>ForEach</c> list is present and empty: one entry, the family's neutral
        /// effect, never sent. It joins the combination as that neutral effect, unless the rule is shadow.
        /// </summary>
        /// <summary>The step-0 and step-1 rules one secondary contributes, with its answer payload.</summary>
        internal static (List<ArbiterDecisionRule> Rules, List<string> Effects, List<string> Payload) SecondaryRules(
            TriggerPlan plan,
            Step1Call call,
            string neutral,
            string primaryOutcome,
            bool primaryAllows,
            string? priorDecisionId,
            string secondaryRecordId)
        {
            List<ArbiterDecisionRule> rules = Step0Rules(plan);
            List<string> effects = new(Step0EffectsOf(plan));
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
                    // Same shadow state the primary path records: a shadow question keeps shadow and its would-effect.
                    (string notEvaluatedEffect, bool? notEvaluatedShadow, string? notEvaluatedWould) = ShadowState(
                        question.Rule, mapped, neutral);
                    rules.Add(Step1Rule(question, answer, notEvaluatedEffect, score, notEvaluatedShadow, notEvaluatedWould, null));
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
                    string? evidence = string.Equals(mapped, RuleEffects.SameFailure, StringComparison.Ordinal) && priorDecisionId is not null
                        ? ArbiterFailureNotices.SameFailureLine(priorDecisionId, secondaryRecordId)
                        : null;
                    rules.Add(Step1Rule(question, answer, mapped, score, null, null, evidence));
                    effects.Add(mapped);
                }
            }

            return (rules, effects, payload);
        }

        internal static void RecordNoCandidates(TriggerPlan plan, List<ArbiterDecisionRule> rules, List<string> effects)
        {
            string neutral = ArbiterTriggerFamilies.NeutralEffect(plan.Family);
            foreach (Question question in plan.NoCandidates)
            {
                bool shadow = question.Rule.Shadow;
                rules.Add(Step1Rule(
                    question, NoCandidatesAnswer, neutral, null, shadow ? true : null, shadow ? neutral : null, NoCandidatesEvidence));
                if (!shadow)
                {
                    effects.Add(neutral);
                }
            }
        }

        /// <summary>
        /// Combines the entries that take part: not shadow, and evaluated. A shadow or not-evaluated entry
        /// records its neutral effect but stays out of the combination. When nothing takes part, the family's
        /// neutral effect is the outcome, the same effect those entries record.
        /// </summary>
        internal static string CombineGating(ArbiterTriggerFamily family, List<string> gating) =>
            gating.Count == 0
                ? ArbiterTriggerFamilies.NeutralEffect(family)
                : ArbiterTriggerFamilies.Combine(family, gating);

        internal static string ProviderStatusFor(TriggerPlan plan, Step1Call call) =>
            plan.ShortCircuits
                ? ArbiterProviderStatuses.ShortCircuited
                : plan.Questions.Count == 0
                    ? ArbiterProviderStatuses.NotEvaluated
                    : call.Status;

        /// <summary>
        /// A drop that another rule's annotation outranks stays in the record, marked suppressed, so the decision
        /// log still shows the filter's view of the finding.
        /// </summary>
        internal static void MarkSuppressedDrops(List<ArbiterDecisionRule> rules, string outcome)
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

        internal static string AnswerFor(Question question, Step1Call call) =>
            call.Answers.TryGetValue(question.Rule.Id, out string? answer) ? answer : RuleEngine.Undecidable;

        /// <summary>The note a return carries for the findings past the question budget that were kept unjudged.</summary>
        internal static string FindingsOverCapNote(int notJudged) =>
            $"findings-over-cap: {notJudged} finding(s) past the {StateBudget.MaxQuestions}-question budget were not judged and are kept";

        /// <summary>
        /// A log-only row: a note the record carries that no question asked. It is shadow, so it never joins the combination.
        /// </summary>
        internal static ArbiterDecisionRule NoteRow(string id, string neutral, string evidence) =>
            new(id, 0, "note", null, null, null, neutral, [], evidence, shadow: true, wouldEffect: null);

        /// <summary>
        /// The step-0 effects a plan contributes to its outcome. A shortcut's undecidable step-0 answer is not a verdict
        /// when step 1 asks that rule: the step-1 answer decides it, so the step-0 spawn it would map to must not block an answer.
        /// </summary>
        internal static List<string> Step0EffectsOf(TriggerPlan plan)
        {
            List<string> effects = [];
            foreach (ArbiterRuleAnswer answer in plan.Step0.RuleAnswers)
            {
                // A shadow entry is log-only: it never joins the combination.
                if (!answer.Shadow && !DecidedByStepOne(plan, answer))
                {
                    effects.Add(answer.Effect);
                }
            }

            return effects;
        }

        /// <summary>
        /// Whether a rule's undecidable step-0 answer is superseded by the step-1 question the same rule asked. The
        /// shortcut, same-failure, conductor and investigate families hold this shape: their undecidable maps to a
        /// spawn or an allow that must not stand in for the answer step 1 gives, and the record keeps one row per rule.
        /// </summary>
        internal static bool DecidedByStepOne(TriggerPlan plan, ArbiterRuleAnswer answer) =>
            plan.Family is ArbiterTriggerFamily.Shortcut or ArbiterTriggerFamily.FailureCluster
                or ArbiterTriggerFamily.Conductor or ArbiterTriggerFamily.Investigate
            && string.Equals(answer.Answer, RuleEngine.Undecidable, StringComparison.Ordinal)
            && plan.Questions.Any(question => string.Equals(question.RuleId, answer.RuleId, StringComparison.Ordinal));

        /// <summary>
        /// The evidence line of the step-0 answers whose fix needs the fact they read. A step-0 answer otherwise
        /// carries no evidence: its rule and answer say enough. <c>KW-ARB-CHECKOUT-001</c> names the stray paths,
        /// because whoever cleans up the canonical checkout needs the list; <c>KW-ARB-ROSTER-001</c> names the developer
        /// tier a tiered run replaced the refused technology developer with, because the conductor's next act is to
        /// dispatch it.
        /// </summary>
        internal static Dictionary<string, string>? Step0EvidenceOf(
            IReadOnlyDictionary<string, string> step0Answers,
            ArbiterFactSet facts,
            string? workerRoster)
        {
            Dictionary<string, string> evidence = new(StringComparer.Ordinal);

            if (string.Equals(workerRoster, WorkerRosters.Tiered, StringComparison.Ordinal)
                && step0Answers.TryGetValue(RosterDelegatesTo.RuleId, out string? rosterAnswer)
                && string.Equals(rosterAnswer, "not-listed", StringComparison.Ordinal)
                && RosterDelegatesTo.TechnologyDeveloperRefusal(
                    TextOf(facts, "delegation.target"),
                    workerRoster,
                    TextOf(facts, ModelTargetingRules.RecordedDifficultyFact),
                    ArbiterSquadCatalog.LoadEmbedded()) is { } refusal)
            {
                evidence[RosterDelegatesTo.RuleId] = refusal;
            }

            if (step0Answers.TryGetValue("KW-ARB-CHECKOUT-001", out string? answer)
                && string.Equals(answer, "stray", StringComparison.Ordinal)
                && facts.TryGet("checkout.stray-paths", out ArbiterFact? fact)
                && fact.Value is IEnumerable<string> paths)
            {
                List<string> all = [.. paths];
                string named = string.Join(", ", all.Take(MaxStrayPathsNamed));
                string more = all.Count > MaxStrayPathsNamed ? $" and {all.Count - MaxStrayPathsNamed} more" : string.Empty;
                evidence["KW-ARB-CHECKOUT-001"] =
                    $"written in the canonical checkout instead of the run worktree: {named}{more}. Move each into the run " +
                    "worktree, then restore those paths in the canonical checkout.";
            }

            return evidence.Count == 0 ? null : evidence;
        }

        /// <summary>The string value of a fact, or null when the fact is absent or not text.</summary>
        internal static string? TextOf(ArbiterFactSet facts, string name) =>
            facts.TryGet(name, out ArbiterFact? fact) && fact.Value is string text ? text : null;

        /// <summary>The step-0 decision entries of a plan, without the undecidable entry a step-1 question superseded.</summary>
        internal static List<ArbiterDecisionRule> Step0Rules(TriggerPlan plan)
        {
            List<ArbiterDecisionRule> rules = new(plan.Step0.RuleAnswers.Count);
            foreach (ArbiterRuleAnswer answer in plan.Step0.RuleAnswers)
            {
                if (DecidedByStepOne(plan, answer))
                {
                    continue;
                }

                string? evidence = plan.Step0Evidence is not null
                    && plan.Step0Evidence.TryGetValue(answer.RuleId, out string? found) ? found : null;
                rules.Add(new ArbiterDecisionRule(
                    answer.RuleId, 0, answer.Answer, null, null, null, answer.Effect, [], evidence,
                    shadow: answer.Shadow ? true : null,
                    wouldEffect: answer.WouldEffect));
            }

            return rules;
        }

        /// <summary>One step-1 rule's decision entry: its answer, its effect, and the score when it was scored.</summary>
        internal static ArbiterDecisionRule Step1Rule(
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
        internal static (string Effect, bool? Shadow, string? WouldEffect) ShadowState(
            ArbiterRule rule,
            string mapped,
            string neutral) =>
            rule.Shadow ? (neutral, true, mapped) : (mapped, null, null);

        /// <summary>
        /// A question the provider did not evaluate records its family's neutral effect. It is not counted in
        /// the combination, and <c>allow</c> is not a legal effect for every family.
        /// </summary>
        internal static string MapOrNotEvaluated(ArbiterRule rule, string answer, ArbiterTriggerFamily family) =>
            string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
                ? ArbiterTriggerFamilies.NeutralEffect(family)
                : MapStep1AnswerToEffect(rule, answer, family);

        internal static string MapStep1AnswerToEffect(ArbiterRule rule, string answer, ArbiterTriggerFamily family)
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

        /// <summary>The id of the earlier finding a stored candidate text names, or null when the text names none.</summary>
        internal static string? EarlierIdOf(string? candidate)
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
        internal static JudgedFinding JudgeFinding(
            FindingJudgement judgement,
            string recordId,
            string? refutedSlug)
        {
            ArbiterTriggerFamily family = judgement.Plan.Family;
            string neutral = ArbiterTriggerFamilies.NeutralEffect(family);
            List<ArbiterDecisionRule> rules = Step0Rules(judgement.Plan);
            List<string> effects = new(Step0EffectsOf(judgement.Plan));
            List<(string RuleId, string? Candidate, bool Decisive, ArbiterDecisionRule Entry)> rows = [];
            HashSet<string> decisiveRules = new(StringComparer.Ordinal);
            // Unanswerable questions (a missing or wrong-shape list) are not sent: they record undecidable and count in the combination.
            foreach (Question question in judgement.Plan.Questions.Concat(judgement.Plan.Unanswerable))
            {
                string answer = AnswerFor(question, judgement.Call);
                string mapped = MapOrNotEvaluated(question.Rule, answer, family);
                ArbiterStep1Score? score = judgement.Call.Scores is not null
                    && judgement.Call.Scores.TryGetValue(question.Rule.Id, out ArbiterStep1Score? found)
                    ? found
                    : null;
                (string effect, bool? shadow, string? wouldEffect) = ShadowState(question.Rule, mapped, neutral);
                string? evidence = string.Equals(mapped, RuleEffects.Duplicate, StringComparison.Ordinal)
                    && refutedSlug is not null
                    && EarlierIdOf(question.Candidate) is { } earlierId
                    ? ArbiterFindingNotices.DuplicateLine(refutedSlug, earlierId, recordId)
                    : null;
                ArbiterDecisionRule entry = Step1Rule(question, answer, effect, score, shadow, wouldEffect, evidence);
                // Decisive means the answer changes the family's outcome, so a neutral answer (keep, spawn, applies) is not one.
                bool decisive = !string.Equals(mapped, neutral, StringComparison.Ordinal);
                if (decisive && question.Candidate is not null)
                {
                    decisiveRules.Add(question.RuleId);
                }

                rows.Add((question.RuleId, question.Candidate, decisive, entry));
                if (!string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
                    && !question.Rule.Shadow)
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

            // A present-but-empty for-each list records its no-candidates entry on the finding's judgement, as the secondary path does.
            RecordNoCandidates(judgement.Plan, rules, effects);

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
        /// Appends a lens return's findings to the review store, in return order. Each carries its own drop status,
        /// the one its own judgement reached.
        /// </summary>
        internal static async Task RecordReturnedFindingsAsync(
            IArbiterRunStore store,
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

    }

    /// <summary>Ledger and event projection: the ids, phases, headers and digests the records carry.</summary>
    private static class LedgerProjection
    {
        internal static List<string> PromptsFor(ArbiterEvent ev)
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

        internal static ArbiterLedgerEvent BuildLedgerEvent(
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
                Headers = BuildHeaders(first, ev.Prompt),
                PairDigest = ArbiterLedgerQueries.PairDigest(ev.Target, ev.DeliveredPrompt ?? ev.Prompt),
                PreId = phase == ArbiterLedgerPhases.Post ? ev.PreId : null,
                TaskFiles = taskFiles,
                ModelProfile = modelProfile,
                ModelToken = modelToken,
                ModelSource = modelSource,
                Returns = phase == ArbiterLedgerPhases.Post
                    ? ReturnMarkerParser.Parse(
                        ev.ToolOutput,
                        readAttestations: string.Equals(first.Trigger, ArbiterTriggers.DelegatePlannerReturned, StringComparison.Ordinal)
                            || string.Equals(first.TargetClass, "planner", StringComparison.Ordinal))
                    : null,
            };
            return ledgerEvent;
        }

        /// <summary>
        /// The task files of a <c>delegate</c> dispatch, recorded on its pre event so a later
        /// dispatch can tell which files it holds. An unreadable plan records none, and the
        /// rules that need the plan answer for that themselves.
        /// </summary>
        /// <summary>Builds the ledger entry for the event's first dispatch, with its phase's run when it has one.</summary>
        internal static ArbiterLedgerEvent BuildLedgerEntry(
            string ledgerId,
            DateTimeOffset now,
            string source,
            ArbiterEvent ev,
            IReadOnlyList<TriggerClassification> classifications,
            IArbiterPlanReader planReader,
            ArbiterModelTargets? table,
            ArbiterRunScope? run)
        {
            TriggerClassification first = classifications.Count > 0
                ? classifications[0]
                : new TriggerClassification(
                    null, null, ArbiterCallerSources.None, ev.Target, "not-squad",
                    new Dictionary<string, string>(StringComparer.Ordinal), null, true);
            return BuildLedgerEvent(
                ledgerId, now, source,
                ev with { RenderedModelToken = RenderedTokenOf(ev, first.Target, table) },
                first, TaskFilesFor(planReader, ev, first), table) with
            { Run = MapPhase(ev) == ArbiterLedgerPhases.Pre ? run?.Root : null };
        }

        internal static IReadOnlyList<string>? TaskFilesFor(
            IArbiterPlanReader planReader,
            ArbiterEvent ev,
            TriggerClassification first)
        {
            if (MapPhase(ev) != ArbiterLedgerPhases.Pre
                || !string.Equals(first.Trigger, ArbiterTriggers.Delegate, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                ArbiterFactSet facts = planReader.Enrich(new ArbiterFactSet(), first);
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
        internal static string? RenderedTokenOf(ArbiterEvent ev, string? target, ArbiterModelTargets? table) =>
            ev.RenderedModelToken
            ?? (table is not null && target is not null && table.Agents.TryGetValue(target, out ArbiterModelTargetsAgent? agent)
                ? agent.Token
                : null);

        internal static (string? Profile, string? Token, string? Source) PreModelOf(
            ArbiterEvent ev,
            TriggerClassification first,
            string phase,
            ArbiterModelTargets? table)
        {
            if (phase != ArbiterLedgerPhases.Pre || !string.Equals(first.Trigger, ArbiterTriggers.Delegate, StringComparison.Ordinal))
            {
                return (null, null, null);
            }

            bool callerNamed = !string.IsNullOrWhiteSpace(ev.Model);
            string source = callerNamed
                ? "caller"
                : ev.RenderedModelToken is not null ? "rendered" : "unknown";
            if (callerNamed)
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

        internal static ArbiterLedgerHeaders? BuildHeaders(TriggerClassification classification, string prompt)
        {
            classification.Headers.TryGetValue(DispatchHeaders.PlanFile, out string? planFile);
            classification.Headers.TryGetValue(DispatchHeaders.Task, out string? task);
            classification.Headers.TryGetValue(DispatchHeaders.Lens, out string? lens);
            classification.Headers.TryGetValue(DispatchHeaders.Refute, out string? refute);
            classification.Headers.TryGetValue(DispatchHeaders.Technology, out string? technology);
            // An escalation dispatch answers a ledger event and opens with STATUS: ARBITER_ESCALATION, so it has no
            // routing header block; the plan it serves is the PLAN_FILE line inside the envelope. Without it the
            // planner's attestation of that plan's tasks could not be scoped to it (see the architecture's return pairing).
            planFile ??= ArbiterLedgerQueries.EnvelopePlan(prompt);
            if (planFile is null && task is null && lens is null && refute is null && technology is null)
            {
                return null;
            }

            return new ArbiterLedgerHeaders(planFile, task, lens, refute, technology);
        }

        internal static string MapPhase(ArbiterEvent ev)
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

        internal static string ProviderKindName(KyberWeaveConfig config) =>
            config.Arbiter.Provider.Kind == ArbiterProviderKind.Systemone ? "systemone" : "none";

        internal static string RuleSetDigest(IReadOnlyList<ArbiterRule> rules)
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

        internal static string? EndpointOrigin(KyberWeaveConfig config)
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

        /// <summary>
        /// The key a plan's stored report tails are kept under. Inside a run it names the run too, so a later run of
        /// a plan at the same path never reads an earlier run's reports as its own.
        /// </summary>
        internal static string ReturnsKey(string planFile, ArbiterRunScope? run) =>
            run is null ? planFile : $"{planFile}\n{run.Root}\n{run.StartedAt:O}";

        /// <summary>The task a stored return names on its first line.</summary>
        internal static string StoredTaskOf(string item)
        {
            const string prefix = "task: ";
            int end = item.IndexOf('\n', StringComparison.Ordinal);
            string first = end < 0 ? item : item[..end];
            return first.StartsWith(prefix, StringComparison.Ordinal) ? first[prefix.Length..].Trim() : string.Empty;
        }

        internal static string CombineEventOutcome(List<ArbiterDispatchEvaluation> dispatches)
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

        /// <summary>
        /// The technology that scopes the dispatch's technology folder rules: the task's own <c>Technology:</c> label
        /// when its plan was read, else the dispatch's <c>TECHNOLOGY</c> header. A header naming a technology the host
        /// does not declare has no folder to load, and a dispatch that names neither is judged by role folder rules and
        /// <c>arbiter.rules</c> alone.
        /// </summary>
        internal static string? EventTechnology(TriggerClassification classification, ArbiterFactSet facts)
        {
            if (facts.TryGet(ArbiterTriggerCatalog.TaskTechnologyFact, out ArbiterFact? fromTask)
                && fromTask.Value is string taskTechnology
                && !string.IsNullOrWhiteSpace(taskTechnology))
            {
                return taskTechnology.Trim();
            }

            return classification.Headers.TryGetValue(DispatchHeaders.Technology, out string? header)
                   && !string.IsNullOrWhiteSpace(header)
                ? header.Trim()
                : null;
        }

    }

    /// <summary>The run store and decision-record writes, over the collaborators the evaluator was built with.</summary>
    private static class Recording
    {
        /// <summary>
        /// Makes one batched call for the questions. A transport or parse failure is an
        /// <c>error</c> with every answer undecidable; with <paramref name="guardAll"/> any failure
        /// is, so a secondary fault never reaches the primary.
        /// </summary>
        internal static async Task<Step1Call> AskAsync(
            IArbiterProvider provider,
            IReadOnlyList<Question> questions,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string> step0Answers,
            bool guardAll,
            CancellationToken cancellationToken)
        {
            if (questions.Count == 0)
            {
                return new Step1Call(Judging.EmptyAnswers(), null, null, null, ArbiterProviderStatuses.NotEvaluated);
            }

            List<ArbiterRule> rules = new(questions.Count);
            foreach (Question question in questions)
            {
                rules.Add(question.Rule);
            }

            ArbiterStep1BatchResult batch;
            try
            {
                batch = await provider.AskStep1Async(rules, facts, step0Answers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Judging.IsProviderFailure(ex, guardAll, cancellationToken))
            {
                // A provider failure is undecidable, not an error result: every question answers
                // undecidable and the family maps it. A provider bug with no guard surfaces as
                // KW-ARB-HOOK-001 instead of silently allowing.
                return new Step1Call(Judging.EmptyAnswers(), null, null, null, ArbiterProviderStatuses.Error);
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
                Judging.FloorAnswers(rules, batch.AnswersByRule, batch.ScoresByRule), batch.ScoresByRule, batch.Model, batch.Usage, status);
        }

        /// <summary>
        /// Records the dispatch's one primary decision: every judged finding's rules and effects, combined into the
        /// outcome. The provider record is the primary call's; a later finding's call is judged but not itemised.
        /// </summary>
        internal static async Task<ArbiterDispatchEvaluation> RecordPrimaryAsync(
            IArbiterDecisionLog decisionLog,
            IIdGenerator ids,
            TimeProvider clock,
            TriggerClassification classification,
            ArbiterEvent ev,
            FindingJudgement primary,
            IReadOnlyList<JudgedFinding> judged,
            IReadOnlyList<ArbiterDecisionRule> notes,
            string recordId,
            KyberWeaveConfig config,
            IReadOnlyList<ArbiterRule> effectiveRules,
            ArbiterEvaluatorOptions effective,
            string ledgerId,
            string? planDigest,
            long elapsed,
            CancellationToken cancellationToken)
        {
            List<ArbiterDecisionRule> rules = [];
            List<string> effects = [];
            foreach (JudgedFinding finding in judged)
            {
                rules.AddRange(finding.Rules);
                effects.AddRange(finding.Effects);
            }

            // Only entries that take part are in effects: JudgeFinding leaves out shadow and not-evaluated entries, and
            // records a no-candidates entry for each for-each rule it planned. CombineGating gives the neutral when none take part.
            string outcome = Judging.CombineGating(primary.Plan.Family, effects);
            // Notes join the record after the combination: they are log-only and never gate the dispatch.
            rules.AddRange(notes);
            Judging.MarkSuppressedDrops(rules, outcome);
            ArbiterProviderRecord provider = ProviderRecord(
                clock, config, primary.Call, Judging.ProviderStatusFor(primary.Plan, primary.Call), elapsed);
            await AppendRecordAsync(
                decisionLog, ids, clock, classification, ev, primary.Plan.Trigger, rules, outcome, provider, primaryLedgerId: null,
                effective, ledgerId, planDigest, config, effectiveRules, elapsed, cancellationToken,
                recordId: recordId).ConfigureAwait(false);
            return new ArbiterDispatchEvaluation(primary.Plan.Trigger, outcome);
        }

        /// <summary>
        /// Records one secondary trigger behind the primary. Its rules shadow or suppress to the
        /// family's neutral effect, keeping the would-be effect on the record; its combined outcome is
        /// reported on the dispatch and the record shares the primary's ledger id.
        /// </summary>
        internal static async Task<ArbiterSecondaryEvaluation> RecordSecondaryAsync(
            IArbiterDecisionLog decisionLog,
            IIdGenerator ids,
            TimeProvider clock,
            TriggerClassification classification,
            ArbiterEvent ev,
            TriggerPlan plan,
            Step1Call call,
            string primaryOutcome,
            KyberWeaveConfig config,
            IReadOnlyList<ArbiterRule> effectiveRules,
            ArbiterModelTargetingConfig targeting,
            ArbiterFactSet facts,
            ArbiterModelTargets? table,
            ArbiterEvaluatorOptions effective,
            string ledgerId,
            string? planDigest,
            string? priorDecisionId,
            long elapsed,
            CancellationToken cancellationToken)
        {
            string neutral = ArbiterTriggerFamilies.NeutralEffect(plan.Family);
            // The primary allows when it reaches its family's allow-equivalent, the neutral effect. A gate's
            // neutral is applies and a shortcut's is spawn, so comparing against allow alone suppressed them.
            ArbiterTriggerFamily primaryFamily = ArbiterTriggerFamilies.ForTrigger(
                classification.Trigger ?? throw new InvalidOperationException(
                    "A secondary is recorded only for a dispatch that classified a trigger."));
            bool primaryAllows = string.Equals(
                primaryOutcome, ArbiterTriggerFamilies.NeutralEffect(primaryFamily), StringComparison.Ordinal);
            // The record's own id is allocated up front: a same-failure advisory names the record it is written on.
            string secondaryRecordId = ids.NewSortableId(clock.GetUtcNow());
            (List<ArbiterDecisionRule> rules, List<string> effects, List<string> payload) =
                Judging.SecondaryRules(plan, call, neutral, primaryOutcome, primaryAllows, priorDecisionId, secondaryRecordId);
            Judging.RecordNoCandidates(plan, rules, effects);
            string outcome = Judging.CombineGating(plan.Family, effects);
            ArbiterModelBlock? model = string.Equals(plan.Trigger, ArbiterTriggers.DelegateModel, StringComparison.Ordinal)
                ? ModelTargetResolver.Resolve(
                    rules, facts, targeting, table,
                    ev with { RenderedModelToken = LedgerProjection.RenderedTokenOf(ev, classification.Target, table) },
                    targeting.Mode)
                : null;
            if (model is { Delivered: true, Token: { } delivered }
                && effective.ConfirmModelWrite is { } confirm
                && !ConfirmHeld(confirm, delivered))
            {
                model = model with { Delivered = false, Reason = ModelTargetResolver.ModelWriteFailed };
            }

            ArbiterProviderRecord provider = ProviderRecord(clock, config, call, Judging.ProviderStatusFor(plan, call), elapsed);
            await AppendRecordAsync(
                decisionLog, ids, clock, classification, ev, plan.Trigger, rules, outcome, provider, primaryLedgerId: ledgerId,
                effective, ledgerId, planDigest, config, effectiveRules, elapsed, cancellationToken, model: model,
                recordId: secondaryRecordId)
                .ConfigureAwait(false);
            string? answered = payload.Count == 0 ? null : string.Join(",", payload);
            return new ArbiterSecondaryEvaluation(plan.Trigger, outcome, model?.Token ?? answered)
            {
                DecisionId = secondaryRecordId,
            };
        }

        internal static async Task AppendRecordAsync(
            IArbiterDecisionLog decisionLog,
            IIdGenerator ids,
            TimeProvider clock,
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
            long elapsed,
            CancellationToken cancellationToken,
            ArbiterModelBlock? model = null,
            string? recordId = null)
        {
            DateTimeOffset now = clock.GetUtcNow();
            classification.Headers.TryGetValue(DispatchHeaders.PlanFile, out string? planFile);
            classification.Headers.TryGetValue(DispatchHeaders.Task, out string? task);
            string repeatKey = rules.Count > 0 ? rules[0].Id : trigger;
            int repeat = decisionLog.Repeat(repeatKey, planFile, planDigest, task, ev.Session);
            ArbiterDecisionRecord record = new(
                recordId ?? ids.NewSortableId(now),
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
                LedgerProjection.RuleSetDigest(effectiveRules),
                rules,
                provider,
                outcome,
                repeat,
                ElapsedMilliseconds(clock, elapsed),
                primaryLedgerId: primaryLedgerId,
                model: model);
            if (!effective.DryRun)
            {
                await decisionLog.AppendAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static ArbiterProviderRecord ProviderRecord(
            TimeProvider clock,
            KyberWeaveConfig config,
            Step1Call call,
            string status,
            long elapsed) =>
            new(
                LedgerProjection.ProviderKindName(config),
                LedgerProjection.EndpointOrigin(config),
                call.Model,
                call.Usage is null ? null : new ArbiterProviderUsage(call.Usage.InputTokens, call.Usage.OutputTokens),
                ElapsedMilliseconds(clock, elapsed),
                status);

        internal static int ElapsedMilliseconds(TimeProvider clock, long startedAt) =>
            (int)Math.Min(int.MaxValue, (long)clock.GetElapsedTime(startedAt).TotalMilliseconds);

        /// <summary>
        /// Adds <c>ledger.task-returns</c>: the stored report tails of earlier returns for this task and the tasks it
        /// depends on, newest last. The model reads them to judge whether a dependency finished or failing tests exist
        /// when the parsed markers alone do not say. Absent when the trigger does not declare it or the plan is unknown.
        /// </summary>
        internal static ArbiterFactSet WithTaskReturns(IArbiterRunStore store, ArbiterFactSet facts, ArbiterRunScope? run)
        {
            if (!facts.Contains("ledger.task-returns")
                || !facts.TryGet("delegation.plan-file", out ArbiterFact? planFact)
                || planFact.Value is not string { Length: > 0 } planFile)
            {
                return facts;
            }

            HashSet<string> tasks = new(StringComparer.Ordinal);
            if (facts.TryGet("plan.task", out ArbiterFact? taskFact) && taskFact.Value is string task)
            {
                tasks.Add(task);
            }

            if (facts.TryGet("plan.task.depends-on", out ArbiterFact? dependsOn) && dependsOn.Value is IEnumerable<string> dependencies)
            {
                tasks.UnionWith(dependencies);
            }

            List<string> returns = [.. store.ReadReturns(LedgerProjection.ReturnsKey(planFile, run))
                .Where(item => tasks.Contains(LedgerProjection.StoredTaskOf(item)))
                .TakeLast(MaxTaskReturns)];
            return facts.With("ledger.task-returns", returns, ArbiterFactLabel.Derived);
        }

        /// <summary>The run store under this evaluator's Arbiter directory, sized by <c>arbiter.stores</c>.</summary>
        internal static IArbiterRunStore ReviewStore(
            IArbiterStateFactory state,
            IArbiterLedger ledger,
            TimeProvider clock,
            KyberWeaveConfig config) =>
            state.OpenRunStore(
                ledger.DirectoryPath,
                new ArbiterStoreOptions(config.Arbiter.Stores.MaxItemBytes, config.Arbiter.Stores.RetainDays),
                clock);

        /// <summary>The parent technology of the dispatch's TECHNOLOGY header, resolved as the gate run does; null when the host has no file system or the dispatch names none.</summary>
        internal static string? ParentTechnologyOf(
            IFileSystem? files,
            KyberWeaveConfig config,
            TriggerClassification classification,
            string? repoRoot) =>
            files is not null && repoRoot is not null && classification.Headers.TryGetValue(DispatchHeaders.Technology, out string? technology)
                ? TaskGateResolver.ParentTechnologyOrNone(files, repoRoot, config, technology)
                : null;

        /// <summary>The run the event's dispatch belongs to, when the git facts can read the run records; null outside any run.</summary>
        internal static ArbiterRunScope? RunScopeOf(IArbiterGitFacts gitFacts, ArbiterEvent ev) =>
            (gitFacts as IArbiterRunScopeSource)?.RunScopeOf(ev.Cwd);

        internal static void ResolveKey(Func<Uri, string?> keyResolver, KyberWeaveConfig config)
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
                _ = keyResolver(endpoint);
            }
        }

    }

}
