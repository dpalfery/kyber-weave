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
/// one decision record per dispatch is appended after. At most one provider
/// call runs per trigger, and none once step 0 has produced the family's
/// strongest effect. A provider failure degrades to <c>undecidable</c> (which
/// the family maps); any other exception becomes an error result carrying
/// <c>KW-ARB-HOOK-001</c>, never <c>allow</c>. The key is resolved to prove the
/// origin is served but is never logged.
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

    /// <summary>Creates an evaluator over already-constructed collaborators.</summary>
    public ArbiterEvaluator(
        IArbiterProvider provider,
        Func<Uri, string?> keyResolver,
        InFlightLedger ledger,
        DecisionLog decisionLog,
        IArbiterGitFacts gitFacts,
        IArbiterPlanReader planReader,
        TimeProvider clock)
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
            ArbiterLedgerEvent ledgerEvent = BuildLedgerEvent(ledgerId, now, effective.Source, ev, first);
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

        ArbiterTriggerFamily family = ArbiterTriggerFamilies.ForTrigger(trigger);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, config);
        facts = _gitFacts.Enrich(facts, classification, ev);
        facts = _planReader.Enrich(facts, classification);

        // Read once here and reused for both REPEAT and the record, so the count and the
        // entry it is written beside are keyed on the same digest.
        string? planDigest = facts.TryGet("plan.digest", out ArbiterFact? digestFact)
            ? digestFact.Value as string
            : null;

        List<ArbiterRule> step0Rules = config.Arbiter.Rules
            .Where(rule => rule.Enabled
                && string.Equals(rule.Trigger, trigger, StringComparison.Ordinal)
                && rule.Decide is { Count: > 0 })
            .ToList();
        ArbiterOutcome step0 = RuleEngine.EvaluateStep0(step0Rules, facts, trigger);
        bool shortCircuits = step0.RuleAnswers.Count > 0 && step0.Step0ShortCircuitsStep1;
        Dictionary<string, string> step0Answers = step0.RuleAnswers
            .ToDictionary(answer => answer.RuleId, answer => answer.Answer, StringComparer.Ordinal);

        List<ArbiterRule> candidates = config.Arbiter.Rules
            .Where(rule => rule.Enabled
                && string.Equals(rule.Trigger, trigger, StringComparison.Ordinal)
                && rule.Ask is not null)
            .ToList();
        List<ArbiterRule> selected = new(candidates.Count);
        foreach (ArbiterRule rule in candidates)
        {
            string decideAnswer = step0Answers.TryGetValue(rule.Id, out string? answered)
                ? answered
                : RuleEngine.Undecidable;
            bool shouldAsk = rule.Decide is null || rule.ShouldAsk(decideAnswer);
            if (shouldAsk && SystemOneContracts.WhenMatches(rule.Ask!, step0Answers))
            {
                selected.Add(rule);
            }
        }

        ArbiterStep1BatchResult? batch = null;
        List<KeyValuePair<ArbiterRule, string>> step1 = new(selected.Count);
        string providerStatus;
        string? model = null;
        ArbiterStep1Usage? usage = null;
        if (shortCircuits)
        {
            providerStatus = ArbiterProviderStatuses.ShortCircuited;
        }
        else if (selected.Count == 0)
        {
            providerStatus = ArbiterProviderStatuses.NotEvaluated;
        }
        else
        {
            DateTimeOffset before = _clock.GetUtcNow();
            try
            {
                batch = await _provider.AskStep1Async(selected, facts, step0Answers, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A provider failure is undecidable, not an error result: every
                // selected rule answers undecidable and the family maps it.
                foreach (ArbiterRule rule in selected)
                {
                    step1.Add(new KeyValuePair<ArbiterRule, string>(rule, RuleEngine.Undecidable));
                }

                providerStatus = ArbiterProviderStatuses.Error;
                string outcomeAfterError = CombineEffects(family, step0, step1);
                await AppendDecisionAsync(
                    classification, ev, config, effective, ledgerId, family, step0, step1,
                    ProviderKindName(config),
                    EndpointOrigin(config), model, usage, providerStatus, outcomeAfterError,
                    elapsed, planDigest, cancellationToken)
                    .ConfigureAwait(false);
                _ = before;
                return new ArbiterDispatchEvaluation(trigger, outcomeAfterError);
            }

            foreach (ArbiterRule rule in selected)
            {
                string answer = batch.AnswersByRule.TryGetValue(rule.Id, out string? value)
                    ? value
                    : RuleEngine.Undecidable;
                step1.Add(new KeyValuePair<ArbiterRule, string>(rule, answer));
            }

            bool allNotEvaluated = step1.Count > 0
                && step1.All(entry => string.Equals(entry.Value, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal));
            if (allNotEvaluated)
            {
                providerStatus = ArbiterProviderStatuses.NotEvaluated;
            }
            else if (!batch.RequestSent)
            {
                providerStatus = ArbiterProviderStatuses.OverBudget;
            }
            else
            {
                providerStatus = ArbiterProviderStatuses.Answered;
            }

            model = batch.Model;
            usage = batch.Usage;
        }

        string outcome = CombineEffects(family, step0, step1);
        await AppendDecisionAsync(
            classification, ev, config, effective, ledgerId, family, step0, step1,
            ProviderKindName(config),
            EndpointOrigin(config), model, usage, providerStatus, outcome,
            elapsed, planDigest, cancellationToken)
            .ConfigureAwait(false);
        return new ArbiterDispatchEvaluation(trigger, outcome);
    }

    private async Task AppendDecisionAsync(
        TriggerClassification classification,
        ArbiterEvent ev,
        KyberWeaveConfig config,
        ArbiterEvaluatorOptions effective,
        string ledgerId,
        ArbiterTriggerFamily family,
        ArbiterOutcome step0,
        List<KeyValuePair<ArbiterRule, string>> step1,
        string providerKind,
        string? endpointOrigin,
        string? model,
        ArbiterStep1Usage? usage,
        string providerStatus,
        string outcome,
        Stopwatch elapsed,
        string? planDigest,
        CancellationToken cancellationToken)
    {
        string trigger = classification.Trigger ?? string.Empty;
        DateTimeOffset now = _clock.GetUtcNow();
        List<ArbiterDecisionRule> rules = new(step0.RuleAnswers.Count + step1.Count);
        foreach (ArbiterRuleAnswer answer in step0.RuleAnswers)
        {
            rules.Add(new ArbiterDecisionRule(
                answer.RuleId, 0, answer.Answer, null, null, null, answer.Effect, [], null));
        }

        foreach (KeyValuePair<ArbiterRule, string> entry in step1)
        {
            ArbiterRule rule = entry.Key;
            string answer = entry.Value;
            string effect = string.Equals(answer, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal)
                ? RuleEffects.Allow
                : MapStep1AnswerToEffect(rule, answer, family);
            rules.Add(new ArbiterDecisionRule(
                rule.Id, 1, answer, null, null, null, effect, [], null));
        }

        classification.Headers.TryGetValue("PLAN_FILE", out string? planFile);
        classification.Headers.TryGetValue("TASK", out string? task);
        string repeatKey = rules.Count > 0 ? rules[0].Id : trigger;
        int repeat = _decisionLog.Repeat(repeatKey, planFile, planDigest, task);
        ArbiterProviderRecord provider = new(
            providerKind,
            endpointOrigin,
            model,
            usage is null ? null : new ArbiterProviderUsage(usage.InputTokens, usage.OutputTokens),
            (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds),
            providerStatus);
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
            (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds));
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

    private static string CombineEffects(
        ArbiterTriggerFamily family,
        ArbiterOutcome step0,
        List<KeyValuePair<ArbiterRule, string>> step1)
    {
        List<string> effects = new(step0.RuleAnswers.Count + step1.Count);
        foreach (ArbiterRuleAnswer answer in step0.RuleAnswers)
        {
            effects.Add(answer.Effect);
        }

        foreach (KeyValuePair<ArbiterRule, string> entry in step1)
        {
            if (string.Equals(entry.Value, ArbiterStep1BatchResult.NotEvaluated, StringComparison.Ordinal))
            {
                continue;
            }

            effects.Add(MapStep1AnswerToEffect(entry.Key, entry.Value, family));
        }

        return ArbiterTriggerFamilies.Combine(family, effects);
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
        TriggerClassification first)
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
        };
        return ledgerEvent;
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
