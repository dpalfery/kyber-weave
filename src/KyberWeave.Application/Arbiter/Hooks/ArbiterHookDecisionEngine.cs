using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using YamlDotNet.Core;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The production hook decision engine: runs <see cref="ArbiterEvaluator"/> over the
/// classified event and maps the result onto the engine port the adapters render.
/// </summary>
/// <remarks>
/// Every collaborator is built per evaluation from the loaded host configuration:
/// the provider (<c>none</c> or <c>systemone</c>), the origin-bound key resolver over
/// the OS credential stores, the ledger and decision log under
/// <c>artifacts/arbiter/</c>, real git facts and the plan reader. The user override
/// (<c>~/.config/kyber-weave/arbiter.yml</c>) applies field by field to the provider,
/// as in every other host. A non-allow outcome renders the escalation envelope on
/// conductor and investigate triggers, the post-return block, or the review note;
/// an error result, an evaluator fault and a malformed user override all write
/// <c>KW-ARB-HOOK-001</c> to the log and the chat-visible note and let the dispatch
/// continue: the Arbiter blocks only on a finding, never on a failure to decide.
/// Nothing but the returned outcome ever reaches stdout:
/// diagnostics go to <see cref="HookContext.Log"/> (stderr in production).
/// Evaluation is synchronous at this boundary because the adapters are; the wait
/// stays inside the harness's per-hook timeout.
/// </remarks>
public sealed class ArbiterHookDecisionEngine : IContextualHookDecisionEngine, IBackgroundDispatchEngine, IArbiterProviderSource
{
    private const string ModelTrigger = "delegate.model";
    private const string TriageShortcutTrigger = "triage.shortcut";
    private const string IntakeShortcutTrigger = "intake.shortcut";
    private const string DocsImpactShortcutTrigger = "docs-impact.shortcut";
    private const string IntakeMarkerPrefix = "INTAKE:";
    private const string ModelTargetingOff = "off";

    private readonly Func<KyberWeaveConfig, IArbiterProvider> _providerFactory;
    private readonly Func<string, IArbiterGitFacts>? _gitFactsFactory;
    private readonly IHookGitFactsFactory _hookGitFacts;
    private readonly Func<string, IArbiterPlanReader> _planReaderFactory;
    private readonly Func<string> _homeDirectory;
    private readonly Func<ICredentialStore> _credentialStoreFactory;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly IArbiterEvaluatorFactory _evaluators;
    private readonly IArbiterStateFactory _state;
    private readonly IArbiterProviderFactory _providers;
    private readonly IArbiterUserSettings _userSettings;
    private readonly IPlanRootResolver _planRoots;
    private readonly IArbiterHostConfig _hostConfig;
    private readonly IProcessEnvironment _process;
    private readonly IBackgroundDispatchStore _background;
    private readonly IIdGenerator _ids;

    /// <summary>
    /// Creates an engine over the ports it decides through. Every factory is injectable so tests run the real
    /// evaluation end to end with a fake provider against a temp repo; omitted factories are the production ones.
    /// </summary>
    /// <param name="evaluators">Builds the evaluator for one event.</param>
    /// <param name="state">Opens the decision log the mapping reads.</param>
    /// <param name="providers">Creates the decision-model client.</param>
    /// <param name="userSettings">The user override applied to the repository's provider.</param>
    /// <param name="planFacts">The plan facts the evaluator merges in.</param>
    /// <param name="planRoots">Finds the worktree that holds a dispatch's plan.</param>
    /// <param name="gitFacts">Builds the git facts for one evaluation.</param>
    /// <param name="credentials">The operating system credential store the provider key is read from.</param>
    /// <param name="hostConfig">Resolves the home directory a global-scope evaluation keeps its state under.</param>
    /// <param name="process">The process the engine reads its home, directory and environment variables from.</param>
    /// <param name="background">Keeps the background dispatches whose turns are still to return.</param>
    /// <param name="ids">Builds the decision id for a hook decision that has none of its own.</param>
    /// <param name="providerFactory">Replaces the provider the configuration names.</param>
    /// <param name="gitFactsFactory">Replaces the git facts.</param>
    /// <param name="planReaderFactory">Replaces the plan reader.</param>
    /// <param name="homeDirectory">Replaces the operator's home.</param>
    /// <param name="clock">Replaces the system clock.</param>
    /// <param name="credentialStoreFactory">Replaces the credential store.</param>
    /// <param name="environment">Replaces the environment variables.</param>
    public ArbiterHookDecisionEngine(
        IArbiterEvaluatorFactory evaluators,
        IArbiterStateFactory state,
        IArbiterProviderFactory providers,
        IArbiterUserSettings userSettings,
        IPlanFacts planFacts,
        IPlanRootResolver planRoots,
        IHookGitFactsFactory gitFacts,
        ICredentialStore credentials,
        IArbiterHostConfig hostConfig,
        IProcessEnvironment process,
        IBackgroundDispatchStore background,
        IIdGenerator ids,
        Func<KyberWeaveConfig, IArbiterProvider>? providerFactory = null,
        Func<string, IArbiterGitFacts>? gitFactsFactory = null,
        Func<string, IArbiterPlanReader>? planReaderFactory = null,
        Func<string>? homeDirectory = null,
        TimeProvider? clock = null,
        Func<ICredentialStore>? credentialStoreFactory = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(evaluators);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(userSettings);
        ArgumentNullException.ThrowIfNull(planFacts);
        ArgumentNullException.ThrowIfNull(planRoots);
        ArgumentNullException.ThrowIfNull(gitFacts);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(hostConfig);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(background);
        _background = background;
        ArgumentNullException.ThrowIfNull(ids);
        _ids = ids;
        _evaluators = evaluators;
        _state = state;
        _providers = providers;
        _userSettings = userSettings;
        _planRoots = planRoots;
        _hostConfig = hostConfig;
        _process = process;
        _homeDirectory = homeDirectory ?? (() => process.HomeDirectory);
        Func<string> home = _homeDirectory;
        _providerFactory = providerFactory ?? (config => CreateProvider(ApplyUserProvider(config, home)));
        _hookGitFacts = gitFacts;
        _gitFactsFactory = gitFactsFactory;
        _planReaderFactory = planReaderFactory ?? (root => new HookPlanReader(planFacts, root));
        _clock = clock ?? TimeProvider.System;
        _credentialStoreFactory = credentialStoreFactory ?? (() => credentials);
        _environment = environment ?? process.GetVariable;
    }

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
        DecidePreDispatch(
            "claude",
            caller,
            target,
            prompt,
            config,
            new HookContext(_process.CurrentDirectory, () => _ids.NewGuid().ToString("N"), TextWriter.Null));

    /// <inheritdoc/>
    public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
        DecidePostDispatch(
            "claude",
            caller,
            target,
            string.Empty,
            toolOutput,
            config,
            new HookContext(_process.CurrentDirectory, () => _ids.NewGuid().ToString("N"), TextWriter.Null));

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null) =>
        DecidePreDispatch(harness, caller, target, prompt, config, context, toolCallId, sessionId, dispatchModel: null);

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId,
        string? dispatchModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        return Decide(
            harness, caller, target, prompt, null, isPost: false, config, context, toolCallId, sessionId, dispatchModel);
    }

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId,
        string? dispatchModel,
        string? deliveredPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        return Decide(
            harness, caller, target, prompt, null, isPost: false, config, context, toolCallId, sessionId, dispatchModel,
            deliveredPrompt: deliveredPrompt);
    }

    /// <inheritdoc/>
    public HookOutcome DecidePostDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(toolOutput);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        return Decide(harness, caller, target, prompt, toolOutput, isPost: true, config, context, toolCallId, sessionId);
    }

    /// <inheritdoc/>
    public void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(context);
        string arbiterDirectory = ResolveArbiterDirectory(context.RepoRoot, context.Scope, context.HomeDirectory);
        string? digest = ArbiterLedgerQueries.PairDigest(dispatch.Target, dispatch.Prompt);
        IReadOnlyList<ArbiterLedgerEvent> events = _state.OpenLedger(arbiterDirectory).ReadAll();

        // The pre event of this dispatch was appended a moment ago, so the newest one still open is its own: an
        // older one with the same prompt is a dispatch that was refused, or one another agent already answers.
        // The harness's call id is exact and survives a prompt the hook rewrote; the digest covers a harness that
        // gives no id, and is taken over the prompt the worker actually runs.
        HashSet<string> taken = events
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .Select(e => e.PreId!)
            .Concat(_background.ForSession(arbiterDirectory, dispatch.SessionId).Select(record => record.PreId))
            .ToHashSet(StringComparer.Ordinal);
        List<ArbiterLedgerEvent> pres = [.. events.Where(e =>
            e.Phase == ArbiterLedgerPhases.Pre
            && string.Equals(e.Harness, dispatch.Harness, StringComparison.Ordinal)
            && !taken.Contains(e.Id))];
        ArbiterLedgerEvent? pre = dispatch.CallId is { Length: > 0 } callId
            ? pres.LastOrDefault(e => string.Equals(e.CallId, callId, StringComparison.Ordinal))
            : null;
        pre ??= digest is null
            ? null
            : pres.LastOrDefault(e => string.Equals(e.PairDigest, digest, StringComparison.Ordinal));
        if (pre is null)
        {
            context.Log.WriteLine(
                $"Arbiter: background agent '{dispatch.AgentId}' has no dispatch on the ledger; its returns are not recorded. " +
                $"Pairing keys compared: call-id '{dispatch.CallId ?? "(none)"}', " +
                $"digest '{digest ?? "(none)"}' against {pres.Count} unpaired pre event(s) of '{dispatch.Harness}'.");
            return;
        }

        _background.Save(arbiterDirectory, new BackgroundDispatchRecord(dispatch, pre.Id));
        context.Log.WriteLine(
            $"Arbiter: background agent '{dispatch.AgentId}' started for dispatch {pre.Id}; each finished turn is recorded as its return.");
    }

    /// <inheritdoc/>
    public HookOutcome DecideBackgroundReturn(
        string harness,
        string sessionId,
        string agentId,
        string response,
        KyberWeaveConfig config,
        HookContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        string arbiterDirectory = ResolveArbiterDirectory(context.RepoRoot, context.Scope, context.HomeDirectory);
        BackgroundDispatchRecord? record = _background.Find(arbiterDirectory, sessionId, agentId);
        if (record is null)
        {
            return new HookOutcome(HookOutcomeKind.PassThrough);
        }

        string digest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(response)));
        if (string.Equals(record.LastResponseDigest, digest, StringComparison.Ordinal))
        {
            context.Log.WriteLine($"Arbiter: this turn of background agent '{agentId}' is already recorded.");
            return new HookOutcome(HookOutcomeKind.PassThrough);
        }

        BackgroundDispatch dispatch = record.Dispatch;
        HookOutcome outcome = Decide(
            harness, dispatch.Caller, dispatch.Target, dispatch.Prompt, response, isPost: true, config, context,
            toolCallId: null, sessionId: null, preId: record.PreId);
        _background.Save(
            arbiterDirectory,
            record with { LastResponseDigest = digest, PendingNote = string.IsNullOrWhiteSpace(outcome.Reason) ? null : outcome.Reason });
        return outcome;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(context);
        string arbiterDirectory = ResolveArbiterDirectory(context.RepoRoot, context.Scope, context.HomeDirectory);
        List<string> notes = [];
        foreach (BackgroundDispatchRecord record in _background.ForSession(arbiterDirectory, sessionId))
        {
            if (record.PendingNote is null || !string.Equals(record.Dispatch.Caller, caller, StringComparison.Ordinal))
            {
                continue;
            }

            notes.Add($"Arbiter note on the return of background agent '{record.Dispatch.Target ?? record.Dispatch.AgentId}':\n{record.PendingNote}");
            _background.Save(arbiterDirectory, record with { PendingNote = null });
        }

        return notes;
    }

    private HookOutcome Decide(
        string harness,
        string caller,
        string? target,
        string prompt,
        string? toolOutput,
        bool isPost,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId,
        string? dispatchModel = null,
        string? preId = null,
        string? deliveredPrompt = null)
    {
        string? resolved = string.IsNullOrWhiteSpace(caller) || IsUnidentified(caller) ? null : caller;

        // A serve event arrives with no harness hook: the whole event is the MCP
        // client's assertion (design 'Fact labels': derived vs asserted, R7), so a
        // caller it names records as asserted. A hook event keeps the harness label —
        // there the caller is what the harness payload itself reported.
        bool asserted = resolved is not null
            && string.Equals(harness, ArbiterServeToken.Harness, StringComparison.Ordinal);
        string repoRoot = context.RepoRoot;
        string arbiterDirectory = ResolveArbiterDirectory(repoRoot, context.Scope, context.HomeDirectory);
        KyberWeaveConfig effective;
        try
        {
            effective = ApplyUserProvider(config, _homeDirectory);
        }
        catch (InvalidOperationException ex)
        {
            // A malformed user override leaves no provider to decide with: the dispatch continues loudly.
            return AllowLoudly(harness, ex.Message, context.NewDecisionId(), context);
        }

        foreach (Diagnostic warning in RuleValidator.CheckProviderTimeout(config.Arbiter.Provider, effective.Arbiter.Provider))
        {
            context.Log.WriteLine($"{warning.Code}: {warning.Message} {warning.Hint}");
        }

        IArbiterProvider provider = _providerFactory(effective);
        IDisposable? owned = provider as IDisposable;
        try
        {
            // The plan, its task file and the git facts are read in the worktree that holds the plan, which in a
            // conductor run is often not the checkout the hook runs in. The invocation resolved that worktree once
            // through the run-context resolver; the resolver call here is the fallback for a caller outside the hook.
            // The ledger stays with the hook's directory so a dispatch and its return still pair.
            string evaluationRoot = EvaluationRootFor(context, repoRoot, prompt, _planRoots);
            if (!string.Equals(evaluationRoot, repoRoot, StringComparison.Ordinal))
            {
                context.Log.WriteLine($"Arbiter: evaluating against worktree '{evaluationRoot}', which holds the dispatch's plan.");
            }

            IArbiterGitFacts gitFacts = _gitFactsFactory is not null
                ? _gitFactsFactory(evaluationRoot)
                : _hookGitFacts.Create(evaluationRoot, _homeDirectory(), arbiterDirectory);
            IArbiterEvaluator evaluator = _evaluators.Create(
                provider,
                KeyResolverFor(effective),
                arbiterDirectory,
                gitFacts,
                _planReaderFactory(evaluationRoot),
                _clock,
                ModelTargetsDirectory(harness, context));
            ArbiterEvent ev = new()
            {
                Harness = harness,
                Phase = isPost ? "post" : "pre",
                Caller = resolved,
                CallerSource = resolved is null
                    ? ArbiterCallerSources.None
                    : asserted ? ArbiterCallerSources.Asserted : ArbiterCallerSources.Harness,
                HarnessCaller = asserted ? null : resolved,
                RenderedCaller = asserted ? null : resolved,
                AssertedCaller = asserted ? resolved : null,
                Target = target,
                Prompt = prompt,
                DeliveredPrompt = deliveredPrompt,
                ToolCallId = toolCallId,
                PreId = preId,
                Session = sessionId,
                Cwd = evaluationRoot,
                ObservesReturns = context.ObservesReturns,
                ModelOverrideField = context.ModelOverride is { Supported: true } capability ? capability.Field : null,
                ModelOverrideAliases = context.ModelOverride is { Supported: true } aliases ? aliases.AcceptedAliases : null,
                Model = dispatchModel,
                ToolOutput = toolOutput,
                IsDispatch = true,
            };
            ArbiterEvaluationResult result = evaluator.EvaluateAsync(
                ev, effective,
                new ArbiterEvaluatorOptions(Source: ArbiterSources.Hook, ConfirmModelWrite: context.ConfirmModelWrite))
                .GetAwaiter().GetResult();
            if (isPost)
            {
                LogUnpairedReturn(result, ev, arbiterDirectory, context);
            }

            return MapResult(result, harness, resolved, prompt, effective, arbiterDirectory, context, toolOutput, isPost);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken decision model lets the dispatch continue loudly: the failure is
            // written to the log and the chat-visible note, never a block. Cancellation
            // stays a cancellation: the harness timed out the hook, not the model.
            return AllowLoudly(harness, exception.Message, context.NewDecisionId(), context);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Says why a return paired with no dispatch, once: an unpaired return is a post event that will never mark a
    /// task complete or release its dispatch's files, and the line names the keys that were compared.
    /// </summary>
    private void LogUnpairedReturn(
        ArbiterEvaluationResult result, ArbiterEvent ev, string arbiterDirectory, HookContext context)
    {
        if (string.IsNullOrEmpty(result.LedgerId))
        {
            return;
        }

        IReadOnlyList<ArbiterLedgerEvent> events = _state.OpenLedger(arbiterDirectory).ReadAll();
        ArbiterLedgerEvent? post = events.FirstOrDefault(e => string.Equals(e.Id, result.LedgerId, StringComparison.Ordinal));
        if (post is null || post.PreId is not null)
        {
            return;
        }

        int candidates = events.Count(e =>
            e.Phase == ArbiterLedgerPhases.Pre
            && string.Equals(e.Harness, ev.Harness, StringComparison.Ordinal));
        context.Log.WriteLine(
            $"Arbiter: the return of '{(ev.Target is { Length: > 0 } target ? target : ev.Caller ?? "unknown")}' paired with no dispatch. " +
            $"Pairing keys compared: call-id '{post.CallId ?? "(none)"}', digest '{post.PairDigest ?? "(none)"}' " +
            $"against {candidates} pre event(s) of '{ev.Harness}'.");
    }

    /// <summary>
    /// The worktree this dispatch is judged in: the plan, its task file and the git facts are read there. One hook
    /// invocation resolves one run context from the event, which is right for a single dispatch; a batch's other calls
    /// carry their own plans, and a plan that lives in another worktree of this repository is read there.
    /// </summary>
    /// <param name="context">The invocation's context.</param>
    /// <param name="repoRoot">The repository root configuration and the ledger are read under.</param>
    /// <param name="prompt">This dispatch's own prompt.</param>
    /// <param name="planRoots">Finds the worktree that holds a dispatch's plan.</param>
    internal static string EvaluationRootFor(
        HookContext context, string repoRoot, string prompt, IPlanRootResolver planRoots)
    {
        HookRunContext? run = context.RunContext;
        if (run is null)
        {
            return planRoots.Resolve(repoRoot, prompt);
        }

        // The run context was resolved from one event, which is this dispatch's own for a single call. A batch's other
        // calls name their own plan; a plan held by another worktree of this repository is read there, because the
        // plan, its task file and the git facts all belong to the worktree that holds it. Anything else keeps the
        // context's worktree, so a plan that resolves nowhere reads where the invocation's evidence put the work.
        string? plan = PlanFileFor(prompt);
        if (plan is null
            || (run.PlanFile is { } anchored && SamePlan(anchored, plan)))
        {
            return run.Worktree;
        }

        string chosen = planRoots.ResolvePlanRoot(repoRoot, plan, prompt, _ => run.Worktrees);
        return !SamePath(chosen, repoRoot) && run.Worktrees.Any(worktree => SamePath(worktree, chosen))
            ? chosen
            : run.Worktree;
    }

    /// <summary>The plan a dispatch names: its routing header, else the <c>PLAN_FILE</c> line of an escalation envelope.</summary>
    private static string? PlanFileFor(string prompt)
    {
        if (HeaderBlock.Parse(prompt).RawValues.TryGetValue("PLAN_FILE", out string? named)
            && !string.IsNullOrWhiteSpace(named))
        {
            return named.Trim();
        }

        return ArbiterLedgerQueries.EnvelopePlan(prompt);
    }

    private static bool SamePlan(string left, string right) =>
        string.Equals(NormalizePlan(left), NormalizePlan(right), StringComparison.Ordinal);

    private static string NormalizePlan(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('.', '/');

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd('/', '\\'),
            right.TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private HookOutcome MapResult(
        ArbiterEvaluationResult result,
        string harness,
        string? caller,
        string prompt,
        KyberWeaveConfig config,
        string arbiterDirectory,
        HookContext context,
        string? toolOutput,
        bool isPost)
    {
        if (result.IsError)
        {
            return AllowLoudly(
                harness, OneLine(result.ErrorMessage), context.NewDecisionId(), context);
        }

        // One read of the decision log serves every lookup below, so the mapping never reads it twice for one event.
        IReadOnlyList<ArbiterDecisionRecord> decisions = string.IsNullOrEmpty(result.LedgerId)
            ? []
            : _state.OpenDecisionLog(arbiterDirectory).ReadAll();

        if (string.Equals(result.Outcome, RuleEffects.Allow, StringComparison.Ordinal)
            || string.Equals(result.Outcome, RuleEffects.Applies, StringComparison.Ordinal)
            || string.Equals(result.Outcome, RuleEffects.NotApplicable, StringComparison.Ordinal))
        {
            // A model that failed or timed out leaves its questions undecidable, and undecidable allows. The
            // dispatch proceeds, and the failure is logged so an allow nothing judged is never silent.
            LogProviderFailures(decisions, result.LedgerId, harness, context);
            ArbiterModelBlock? block = ModelBlockFor(decisions, result.LedgerId);
            bool targeting = !string.Equals(config.Arbiter.ModelTargeting.Mode, ModelTargetingOff, StringComparison.Ordinal);
            return new HookOutcome(
                HookOutcomeKind.Allow,
                ModelToken: block is { Delivered: true } ? block.Token : null,
                ModelBlock: targeting ? block : null);
        }

        // A same-failure advisory never denies: the rework proceeds, with the one line naming the prior failure beside it.
        if (string.Equals(result.Outcome, RuleEffects.SameFailure, StringComparison.Ordinal))
        {
            string? advisory = DecisionWithOutcome(decisions, result.LedgerId, RuleEffects.SameFailure)?.Rules
                .FirstOrDefault(rule => string.Equals(rule.Effect, RuleEffects.SameFailure, StringComparison.Ordinal)
                    && rule.Evidence is not null)?.Evidence;
            // The rework proceeds on the resolved model, so the advisory carries the same model block an allow does.
            ArbiterModelBlock? block = ModelBlockFor(decisions, result.LedgerId);
            bool targeting = !string.Equals(config.Arbiter.ModelTargeting.Mode, ModelTargetingOff, StringComparison.Ordinal);
            return new HookOutcome(
                HookOutcomeKind.AllowWithNote,
                advisory ?? "ARBITER_SAME_FAILURE: yes",
                ModelToken: block is { Delivered: true } ? block.Token : null,
                ModelBlock: targeting ? block : null);
        }

        // An answered shortcut withholds its worker: the dispatch is denied with the substitute digest the worker's
        // report would have carried, so the caller takes that digest as the result.
        if (string.Equals(result.Outcome, RuleEffects.Answer, StringComparison.Ordinal))
        {
            ArbiterDecisionRecord? answered = DecisionWithOutcome(decisions, result.LedgerId, RuleEffects.Answer);
            string? digest = answered is null ? null : SubstituteDigestFor(answered, prompt);
            if (answered is null || digest is null)
            {
                return AllowLoudly(
                    harness,
                    "the shortcut answered with no substitute digest; the dispatch continues.",
                    context.NewDecisionId(),
                    context);
            }

            context.Log.WriteLine($"Arbiter {answered.Trigger} answer: withholding the worker for '{answered.Target ?? "<none>"}'.");
            return isPost
                ? new HookOutcome(HookOutcomeKind.PostBlock, digest)
                : new HookOutcome(HookOutcomeKind.Deny, digest);
        }

        // A secondary (delegate.model) shares the primary's ledger id and is written after it, so the last record on
        // the ledger is not the dispatch's decision. The record that carries the event's outcome is.
        ArbiterDecisionRecord? record = DecisionWithOutcome(decisions, result.LedgerId, result.Outcome)
            ?? decisions.LastOrDefault(decision => string.Equals(decision.LedgerId, result.LedgerId, StringComparison.Ordinal));
        if (record is null)
        {
            return AllowLoudly(
                harness,
                "the decision log holds no decision for this evaluation.",
                context.NewDecisionId(),
                context);
        }

        if (string.Equals(record.Provider.Status, ArbiterProviderStatuses.Error, StringComparison.Ordinal))
        {
            // The model failed before judging: an escalation with no model behind it
            // lets the dispatch continue loudly rather than blocking on a verdict
            // nothing reached.
            return AllowLoudly(
                harness,
                $"the decision model failed (provider {record.Provider.Kind}); " +
                "the escalation was judged by no model.",
                context.NewDecisionId(),
                context);
        }

        if (string.Equals(record.Outcome, RuleEffects.Drop, StringComparison.Ordinal))
        {
            // A drop sets a finding aside after the lens returns; it never blocks. The one line names the finding
            // so the host can see what was set aside. A drop has no pre-dispatch form, so before a return it allows.
            IReadOnlyList<ReturnedFinding> returned = ReturnedFinding.ParseAll(toolOutput);
            if (!isPost || returned.Count == 0)
            {
                return new HookOutcome(HookOutcomeKind.Allow);
            }

            // One line per dropped finding: each drop entry is tagged with the finding it judged.
            List<string> lines = record.Rules
                .Where(rule => string.Equals(rule.Effect, RuleEffects.Drop, StringComparison.Ordinal)
                    && rule.Suppressed != true
                    && rule.Candidate is not null)
                .Select(rule => ArbiterFindingNotices.DroppedLine(rule.Candidate!, record.Id))
                .ToList();
            if (lines.Count == 0)
            {
                lines.Add(ArbiterFindingNotices.DroppedLine(returned[0].Id, record.Id));
            }

            return new HookOutcome(HookOutcomeKind.PostAnnotation, string.Join("\n", lines));
        }

        // A duplicate denies the refutation with the line the duplicate rule wrote on the record.
        string? duplicate = string.Equals(record.Outcome, RuleEffects.Duplicate, StringComparison.Ordinal)
            ? record.Rules.FirstOrDefault(rule => string.Equals(rule.Effect, RuleEffects.Duplicate, StringComparison.Ordinal)
                && rule.Evidence is not null)?.Evidence
            : null;
        string reason = duplicate ?? RenderReason(record, prompt, harness, caller, config);
        context.Log.WriteLine($"Arbiter {record.Trigger} {record.Outcome}: blocking dispatch to '{record.Target ?? "<none>"}'.");
        if (string.Equals(record.Outcome, RuleEffects.Annotate, StringComparison.Ordinal))
        {
            return isPost
                ? new HookOutcome(HookOutcomeKind.PostAnnotation, reason)
                : new HookOutcome(HookOutcomeKind.Deny, reason);
        }

        return isPost
            ? new HookOutcome(HookOutcomeKind.PostBlock, reason)
            : new HookOutcome(HookOutcomeKind.Deny, reason);
    }

    /// <summary>Logs <c>KW-ARB-HOOK-001</c> for each decision on the event whose provider call failed.</summary>
    private static void LogProviderFailures(
        IReadOnlyList<ArbiterDecisionRecord> decisions, string ledgerId, string harness, HookContext context)
    {
        foreach (ArbiterDecisionRecord decision in decisions.Where(decision =>
            string.Equals(decision.LedgerId, ledgerId, StringComparison.Ordinal)
            && string.Equals(decision.Provider.Status, ArbiterProviderStatuses.Error, StringComparison.Ordinal)))
        {
            context.Log.WriteLine(
                $"{HookCommand.FailClosedCode}: the decision model failed (provider {decision.Provider.Kind}) on " +
                $"'{decision.Trigger}' for '{harness}' (decision {decision.Id}); its questions are undecidable and the dispatch proceeds.");
        }
    }

    /// <summary>
    /// The last decision on the ledger event that carries <paramref name="outcome"/>, or null when none does. A
    /// secondary shares the primary's ledger id, so the record that answered is found by its outcome, not its position.
    /// </summary>
    private static ArbiterDecisionRecord? DecisionWithOutcome(
        IReadOnlyList<ArbiterDecisionRecord> decisions, string ledgerId, string outcome) =>
        decisions.LastOrDefault(decision => string.Equals(decision.LedgerId, ledgerId, StringComparison.Ordinal)
            && string.Equals(decision.Outcome, outcome, StringComparison.Ordinal));

    /// <summary>
    /// The substitute digest an answered shortcut hands back, named by the decision that answered it, or null when
    /// the trigger is not a shortcut or an intake answer is not PLAN or SPEC.
    /// </summary>
    private static string? SubstituteDigestFor(ArbiterDecisionRecord answered, string prompt)
    {
        if (string.Equals(answered.Trigger, TriageShortcutTrigger, StringComparison.Ordinal))
        {
            return SubstituteDigests.Triage(answered.Id);
        }

        if (string.Equals(answered.Trigger, DocsImpactShortcutTrigger, StringComparison.Ordinal))
        {
            return SubstituteDigests.DocsImpact(answered.Id);
        }

        if (!string.Equals(answered.Trigger, IntakeShortcutTrigger, StringComparison.Ordinal))
        {
            return null;
        }

        string? recommendation = answered.Rules
            .FirstOrDefault(rule => string.Equals(rule.Effect, RuleEffects.Answer, StringComparison.Ordinal))?.Answer
            ?.ToUpperInvariant();
        // An answer that is not PLAN or SPEC has no digest, so the shortcut's null path lets the dispatch continue loudly.
        return recommendation is null || !SubstituteDigests.IsRecommendation(recommendation)
            ? null
            : SubstituteDigests.Intake(IntakeInputOf(prompt), recommendation, answered.Id);
    }

    /// <summary>The request text an intake marker carries: the text after <c>INTAKE:</c> on the first body line.</summary>
    private static string IntakeInputOf(string prompt)
    {
        string? marked = HeaderBlock.Strip(prompt)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(IntakeMarkerPrefix, StringComparison.Ordinal));
        string input = marked is null ? string.Empty : marked[IntakeMarkerPrefix.Length..].Trim();
        return input.Length > 0 ? input : "unspecified";
    }

    /// <summary>
    /// The delegate.model block the secondary resolved on this evaluation's dispatch, or null when it
    /// resolved none. The secondary shares the primary's ledger id, so the block is read from the decision
    /// that carries it. Whether the block was delivered is the block's own flag, not decided here.
    /// </summary>
    private static ArbiterModelBlock? ModelBlockFor(IReadOnlyList<ArbiterDecisionRecord> decisions, string ledgerId) =>
        decisions.LastOrDefault(decision => string.Equals(decision.LedgerId, ledgerId, StringComparison.Ordinal)
            && string.Equals(decision.Trigger, ModelTrigger, StringComparison.Ordinal))?.Model;

    private static string RenderReason(
        ArbiterDecisionRecord record,
        string prompt,
        string harness,
        string? caller,
        KyberWeaveConfig config)
    {
        string callerText = $"{record.Caller ?? caller ?? "unidentified"} ({harness})";
        IReadOnlyDictionary<string, string> headers = HeaderBlock.Parse(prompt).Headers;

        if (string.Equals(record.Outcome, RuleEffects.Skip, StringComparison.Ordinal))
        {
            headers.TryGetValue("LENS", out string? lens);
            return new ArbiterSkipNote(
                lens ?? "unknown",
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        if (string.Equals(record.Outcome, RuleEffects.Verify, StringComparison.Ordinal))
        {
            headers.TryGetValue("REFUTE", out string? refute);
            return new ArbiterVerifiedNote(
                refute ?? "unknown",
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        if (string.Equals(record.Outcome, RuleEffects.Annotate, StringComparison.Ordinal))
        {
            return new ArbiterAnnotationNote(
                FindingOf(prompt, record),
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        List<ArbiterEnvelopeEntry> entries = EntriesOf(record, config);
        return new ArbiterEscalationEnvelope(
            record.Trigger,
            callerText,
            record.Target,
            record.PlanFile,
            record.Task,
            entries,
            record.Id,
            record.Repeat).Render();
    }

    private static IReadOnlyList<string> RuleIdsOf(ArbiterDecisionRecord record) =>
        [.. record.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal)];

    private static ArbiterAnswerText AnswerOf(ArbiterDecisionRule? rule, ArbiterDecisionRecord record)
    {
        if (rule is null)
        {
            return ArbiterAnswerText.Undecidable(record.Provider.Status);
        }

        if (rule.Step == 0)
        {
            return ArbiterAnswerText.Step0(rule.Answer);
        }

        if (string.Equals(rule.Answer, RuleEngine.Undecidable, StringComparison.Ordinal))
        {
            return ArbiterAnswerText.Undecidable(record.Provider.Status);
        }

        // Decision records carry no step-1 scores, so a scored answer renders in
        // the bare step-1 form rather than with a fabricated probability.
        return new ArbiterAnswerText($"{rule.Answer} (step 1)");
    }

    private static List<ArbiterEnvelopeEntry> EntriesOf(ArbiterDecisionRecord record, KyberWeaveConfig config)
    {
        List<ArbiterDecisionRule> firing = [.. record.Rules.Where(rule =>
            !string.Equals(rule.Effect, RuleEffects.Allow, StringComparison.Ordinal)
            && !string.Equals(rule.Effect, RuleEffects.NotApplicable, StringComparison.Ordinal))];
        if (firing.Count == 0)
        {
            firing.AddRange(record.Rules);
        }

        Dictionary<string, string> questions = config.Arbiter.Rules
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Question, StringComparer.Ordinal);
        return [.. firing.Select(rule => new ArbiterEnvelopeEntry(
            rule.Id,
            questions.TryGetValue(rule.Id, out string? question) ? question : "What did the rule decide?",
            AnswerOf(rule, record),
            string.IsNullOrWhiteSpace(rule.Evidence)
                ? $"rule {rule.Id} answered '{rule.Answer}' on {record.Trigger}"
                : rule.Evidence))];
    }

    private static string FindingOf(string prompt, ArbiterDecisionRecord record)
    {
        using StringReader reader = new(prompt);
        while (reader.ReadLine() is { } line)
        {
            const string prefix = "FINDING:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                string finding = line[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(finding))
                {
                    return finding;
                }
            }
        }

        return record.Task ?? record.Trigger;
    }

    private static bool IsUnidentified(string caller) =>
        string.Equals(caller, "unidentified", StringComparison.Ordinal);

    private static string OneLine(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? "unknown error"
            : message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private string ResolveArbiterDirectory(string repoRoot, HookScope scope, string? homeDirectory)
    {
        if (scope == HookScope.Global)
        {
            string effectiveHome = _hostConfig.ResolveHomeDirectory(homeDirectory);
            string rootKey = ProjectRootKey(repoRoot, _process.CurrentDirectory);
            return Path.Combine(effectiveHome, ".kyber-weave", "arbiter", "projects", rootKey);
        }

        return Path.Combine(repoRoot, "artifacts", "arbiter");
    }

    /// <summary>
    /// The directory holding the harness's model targets table, or null when the state directory already
    /// holds it. A project-scope evaluation writes the table into the project's <c>artifacts/arbiter</c>,
    /// which is its state directory. A global-scope evaluation keeps per-project state under the user's home,
    /// but the table is not per project: a user-scope Squad install deploys one per harness beside that
    /// harness's hooks, under the harness's global root.
    /// </summary>
    /// <param name="harness">The harness the event came from.</param>
    /// <param name="context">The event's context, whose scope decides where the table lives.</param>
    private string? ModelTargetsDirectory(string harness, HookContext context)
    {
        if (context.Scope != HookScope.Global
            || !SquadTargetCatalog.TryResolve(harness, out SquadTarget target))
        {
            return null;
        }

        try
        {
            string home = _hostConfig.ResolveHomeDirectory(context.HomeDirectory);
            string root = new SquadGlobalRoots(_environment, home).ResolveGlobalRoot(target);
            return Path.Combine(root, ".kyber-weave", "arbiter");
        }
        catch (ArgumentException)
        {
            // A target with no verified global root cannot be installed at user scope, so it has no table.
            return null;
        }
    }

    /// <summary>The per-project key a global ledger directory is named by: the hash of the root's full path.</summary>
    /// <param name="projectRoot">The project root, relative or fully qualified.</param>
    /// <param name="baseDirectory">The fully qualified directory a relative root is resolved against.</param>
    internal static string ProjectRootKey(string projectRoot, string baseDirectory)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot, baseDirectory));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Lets a dispatch continue loudly after a hook or decision-model failure: the failure
    /// goes to the log and the chat-visible note, and the outcome allows.
    /// </summary>
    private static HookOutcome AllowLoudly(
        string harness, string message, string decisionId, HookContext context)
    {
        string flat = OneLine(message);
        context.Log.WriteLine(
            $"Arbiter hook failure ({decisionId}) on '{harness}': {HookCommand.FailClosedCode}: {flat}. " +
            "The dispatch continues without a decision.");
        return new HookOutcome(
            HookOutcomeKind.AllowWithNote,
            $"{HookCommand.FailClosedCode}: {flat}. The dispatch continues without an Arbiter decision.");
    }

    private Func<Uri, string?> KeyResolverFor(KyberWeaveConfig config)
    {
        ICredentialStore store = _credentialStoreFactory();
        string? userOverrideEndpoint = UserOverrideEndpoint(config, _homeDirectory);
        return uri => ArbiterKeyResolver.Resolve(uri.ToString(), store, _environment, userOverrideEndpoint);
    }

    /// <inheritdoc/>
    public IArbiterProvider ProviderFor(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _providerFactory(config);
    }

    private IArbiterProvider CreateProvider(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Arbiter.Provider.Kind == ArbiterProviderKind.None)
        {
            return new NoneProvider();
        }

        if (!Uri.TryCreate(config.Arbiter.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: arbiter.provider.endpoint " +
                $"'{config.Arbiter.Provider.Endpoint}' is not an absolute URL.");
        }

        Func<Uri, string?> keyResolver = KeyResolverFor(config);
        return _providers.CreateSystemOne(
            endpoint,
            config.Arbiter.Provider.Model,
            config.Arbiter.Provider.TimeoutMs,
            keyResolver);
    }

    private KyberWeaveConfig ApplyUserProvider(KyberWeaveConfig config, Func<string> homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = _userSettings.ApplyTo(repository, homeDirectory());
            if (merged.Equals(repository))
            {
                return config;
            }

            return new KyberWeaveConfig
            {
                Ontology = config.Ontology,
                Harness = config.Harness,
                DocsAnalysis = config.DocsAnalysis,
                Squad = config.Squad,
                ConfigReg = config.ConfigReg,
                Review = config.Review,
                Arbiter = config.Arbiter with { Provider = merged },
            };
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is YamlException)
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: the user override " +
                $"'{ArbiterUserSettings.GetPath(homeDirectory())}' is malformed: {OneLine(exception.Message)}",
                exception);
        }
    }

    private string? UserOverrideEndpoint(KyberWeaveConfig config, Func<string> homeDirectory)
    {
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = _userSettings.ApplyTo(repository, homeDirectory());
            return string.Equals(merged.Endpoint, repository.Endpoint, StringComparison.Ordinal)
                ? null
                : merged.Endpoint;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is YamlException)
        {
            return null;
        }
    }
}
