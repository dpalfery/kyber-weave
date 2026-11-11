using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Core.Configuration;
using ModelContextProtocol.Server;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>The collaborators one serve process answers with. The composition root builds it; tests inject fakes.</summary>
/// <param name="RepoRoot">The resolved root every response names.</param>
/// <param name="Engine">The hook's own decision engine, so a served decision is the hook's decision.</param>
/// <param name="Log">Diagnostics sink: stderr in production, never stdout.</param>
/// <param name="LoadConfig">Loads the host configuration for the root; throws when it is missing or malformed.</param>
/// <param name="KeyResolved">Whether a key resolves for the effective provider. It reports presence only, never the key.</param>
/// <param name="EffectiveProvider">Applies the user override to the repository provider, the provider the hook resolves its key for. Null means the repository provider is the effective one.</param>
public sealed record ArbiterServeContext(
    string RepoRoot,
    IContextualHookDecisionEngine Engine,
    TextWriter Log,
    Func<string, KyberWeaveConfig> LoadConfig,
    Func<KyberWeaveConfig, bool> KeyResolved,
    Func<KyberWeaveConfig, ArbiterProviderConfig>? EffectiveProvider = null);

/// <summary>
/// The facts a calling agent asserts about one event. The server derives everything else
/// (caller, trigger, rules and ledger state) from these, so the decision is advisory: the
/// agent being gated chose what to assert.
/// </summary>
public sealed record ArbiterFacts
{
    /// <summary>The Squad agent being dispatched, or the lens or refutation target.</summary>
    [JsonPropertyName("target")]
    [Description("The Squad agent the event dispatches to, for example review-lens for a lens fan-out.")]
    public string? Target { get; init; }

    /// <summary>The plan file the dispatch works under, when there is one.</summary>
    [JsonPropertyName("plan-file")]
    [Description("The plan file the dispatch works under, when there is one.")]
    public string? PlanFile { get; init; }

    /// <summary>The plan task the dispatch implements, when there is one.</summary>
    [JsonPropertyName("task")]
    [Description("The plan task the dispatch implements, when there is one.")]
    public string? Task { get; init; }

    /// <summary>The dispatch prompt, without any header block the server adds.</summary>
    [JsonPropertyName("prompt")]
    [Description("The dispatch prompt body.")]
    public string? Prompt { get; init; }

    /// <summary>The lens names of a batched fan-out, one evaluation each, before the fan-out.</summary>
    [JsonPropertyName("lens")]
    [Description("The lens names of a batched review fan-out, one evaluation per lens.")]
    public IReadOnlyList<string>? Lens { get; init; }

    /// <summary>The finding a lens reported, as YAML.</summary>
    [JsonPropertyName("finding")]
    [Description("The finding a lens reported, as YAML.")]
    public string? Finding { get; init; }

    /// <summary>The event phase: pre (before dispatch, the default), or post and return (after it returns).</summary>
    [JsonPropertyName("phase")]
    [Description("The event phase: pre before a dispatch (the default), or post or return after it returns.")]
    public string? Phase { get; init; }

    /// <summary>The harness tool-call id, when the caller has one to pair a return with its dispatch.</summary>
    [JsonPropertyName("call-id")]
    [Description("The tool-call id that pairs a return with its dispatch, when the caller has one.")]
    public string? CallId { get; init; }

    /// <summary>The returned output, on a post or return event.</summary>
    [JsonPropertyName("output")]
    [Description("The returned output, on a post or return event.")]
    public string? Output { get; init; }
}

/// <summary>
/// The Arbiter tools the serve surface answers over stdio: the three decision tools for the D4 fallback (evaluate an
/// event, list the rules, report readiness), and two fail-safe answer tools (select relevant document sections,
/// classify a bot review comment). The three decision tools lead every response with the provenance line; the two
/// answer tools return JSON.
/// </summary>
/// <remarks>
/// <c>arbiter_evaluate_agent_dispatch_or_return</c> routes through the hook's own decision engine rather than a
/// parallel evaluator, so an event decided here is decided exactly as the hook would
/// decide it. The rendering of the envelope and the notes stays in the engine for the
/// same reason. A serve evaluation has no harness, so it is recorded under the
/// <c>serve</c> token; the classifier treats that token as project-wide, the same as any
/// harness that does not fire per-agent hooks.
/// </remarks>
[McpServerToolType]
public sealed class ArbiterTools(ArbiterServeContext context)
{
    private const string Harness = "serve";
    private const string UnidentifiedCaller = "unidentified";

    /// <summary>The most evaluations one MCP call may fan out to.</summary>
    private const int MaxFanOutUnits = 64;

    /// <summary>The largest client text one MCP call may embed, in characters: the shared Arbiter limit.</summary>
    private const int MaxTextChars = ArbiterTextLimits.MaxTextChars;

    [McpServerTool(Name = "arbiter_evaluate_agent_dispatch_or_return", ReadOnly = false, OpenWorld = false)]
    [Description("""
        Evaluates one Arbiter event for the D4 fallback, where no harness hook fires, and returns the outcome the hook would return for the same event: allow, an escalation envelope, or a review note. Use when the conductor is about to dispatch a Squad agent or has received a return, and when code-reviewer is about to fan out lenses or refute.spawn refutations. Pass the trigger the event classifies as and the asserted facts: target, plan-file, task, prompt, the lens names as lens, the findings to be refuted as finding, the phase and the output. A lens call or a finding call needs no target: the tool routes both to review-lens and synthesizes the routing header block (KYBER-ARBITER, LENS, REFUTE) itself, exactly as the hook path would see it. The facts are asserted by the calling agent, so the decision is advisory. Each call appends to the decision ledger and log.
        """)]
    public string Evaluate(
        [Description("The trigger the event classifies as, for example delegate, delegate.returned, lens.spawn, refute.spawn or investigate.")]
        string trigger,
        [Description("The asserted facts of the event.")]
        ArbiterFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        (KyberWeaveConfig? config, string? error) = Load();
        string provenance = Provenance(config);
        if (config is null)
        {
            return Failure(provenance, error ?? "The host configuration could not be loaded.");
        }

        if (string.IsNullOrWhiteSpace(trigger) || !KnownTrigger(trigger))
        {
            return Failure(provenance, UnknownTrigger(trigger));
        }

        if (!config.Arbiter.Enabled)
        {
            return provenance + "\noutcome: allow\nArbiter is disabled (arbiter.enabled: false): allowing without evaluation.";
        }

        if (!TryPhase(facts.Phase, out bool post))
        {
            return Failure(provenance, $"Unknown phase '{OneLine(facts.Phase)}'. Use pre, post or return.");
        }

        // A refutation or lens fan-out call carries its routing in the structured
        // fields, never in a hand-written header: a call with neither says what to
        // pass instead of failing classification (review 20.1, Major 2).
        bool fanOut = string.Equals(trigger, "lens.spawn", StringComparison.Ordinal)
            || string.Equals(trigger, "refute.spawn", StringComparison.Ordinal);
        if (fanOut && facts.Lens is not { Count: > 0 } && !Present(facts.Finding))
        {
            return Failure(
                provenance,
                $"The facts carry no routing for '{trigger}': pass the lens names as `lens`, or the findings to be refuted as `finding`.");
        }

        // A single call is bounded in both count and bytes: each unit is a provider
        // call and a ledger append, and a client-supplied finding or prompt is copied
        // into every unit's prompt.
        if (facts.Prompt is { Length: > MaxTextChars })
        {
            return Failure(provenance, $"The prompt is larger than {MaxTextChars} characters.");
        }

        if (facts.Finding is { Length: > MaxTextChars })
        {
            return Failure(provenance, $"The finding is larger than {MaxTextChars} characters.");
        }

        if (facts.Lens is { Count: > MaxFanOutUnits })
        {
            return Failure(
                provenance,
                $"A lens fan-out carries at most {MaxFanOutUnits} units; split the call.");
        }

        // A refutation call is one evaluation per finding id, because a REFUTE header
        // names one finding. Ids are checked before any evaluation, on the lens+finding
        // path too, so a bad id records nothing (review 20.1-2, Major).
        List<FindingBlock>? findingBlocks = null;
        if (Present(facts.Finding))
        {
            findingBlocks = FindingBlocks(facts.Finding!);
            if (findingBlocks.Count > MaxFanOutUnits)
            {
                return Failure(
                    provenance,
                    $"A refutation carries at most {MaxFanOutUnits} findings; split the call.");
            }

            string? problem = FindingIdProblem(findingBlocks);
            if (problem is not null)
            {
                return Failure(provenance, problem);
            }
        }

        List<EvaluationUnit> units = [];
        if (facts.Lens is not { Count: > 0 } && findingBlocks is not null)
        {
            units.AddRange(findingBlocks.Select(block => new EvaluationUnit(null, block.Id, block.Text)));
        }
        else
        {
            units.AddRange(LensesOf(facts).Select(lens => new EvaluationUnit(lens, null, facts.Finding)));
        }

        string? target = facts.Target ?? RoutingTarget(facts);
        List<PendingEvaluation> pending = [];
        foreach (EvaluationUnit unit in units)
        {
            string prompt = PromptFor(facts, unit);
            ArbiterEvent ev = new()
            {
                Harness = Harness,
                Phase = post ? "post" : "pre",
                Cwd = context.RepoRoot,
                Target = target,
                Prompt = prompt,
                ToolCallId = facts.CallId,
                ToolOutput = facts.Output,
                IsDispatch = true,
            };
            TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, target, prompt);
            if (!string.Equals(classification.Trigger, trigger, StringComparison.Ordinal))
            {
                return Failure(
                    provenance,
                    $"The facts classify as '{OneLine(classification.Trigger) ?? "no trigger"}', not '{OneLine(trigger)}'. " +
                    "Pass the trigger the event classifies as.");
            }

            pending.Add(new PendingEvaluation(classification.Caller ?? UnidentifiedCaller, target, prompt, unit.Refute));
        }

        HookContext hookContext = new(context.RepoRoot, () => ArbiterRecordId.New(DateTimeOffset.UtcNow), context.Log);
        ArbiterModelBlock? retarget = null;
        foreach (PendingEvaluation item in pending)
        {
            HookOutcome outcome;
            try
            {
                outcome = post
                    ? context.Engine.DecidePostDispatch(
                        Harness, item.Caller, item.Target, item.Prompt, facts.Output ?? string.Empty,
                        config, hookContext, facts.CallId)
                    : context.Engine.DecidePreDispatch(
                        Harness, item.Caller, item.Target, item.Prompt, config, hookContext, facts.CallId);
            }
            catch (Exception ex)
            {
                return Failure(
                    provenance,
                    $"{HookCommand.FailClosedCode}: the evaluation failed before a decision: {OneLine(ex.Message)}");
            }

            if (outcome.Kind is not (HookOutcomeKind.Allow or HookOutcomeKind.PassThrough or HookOutcomeKind.AllowWithRewrite or HookOutcomeKind.AllowWithNote))
            {
                string named = item.Refute is null ? string.Empty : $"finding: {item.Refute}\n";
                return provenance + "\noutcome: " + OutcomeName(outcome.Kind) + "\n" + named + outcome.Reason;
            }

            retarget ??= outcome.ModelBlock;
        }

        return retarget is null
            ? provenance + "\noutcome: allow"
            : provenance + "\noutcome: allow\n" + RetargetAdvisory(retarget);
    }

    /// <summary>
    /// The retarget the hook would make, as one advisory line. The serve path writes no model field
    /// anywhere, so the line is the whole effect: the caller decides what to dispatch with. A profile
    /// with no token is reported as such, with the resolver's reason, rather than hidden.
    /// </summary>
    private static string RetargetAdvisory(ArbiterModelBlock block) =>
        $"advisory (source: serve): would retarget to profile {OneLine(block.Profile)} " +
        $"token {OneLine(block.Token ?? "<none>")} (reason: {OneLine(block.Reason ?? "none")}). " +
        "Not written: the serve path changes no model.";

    [McpServerTool(Name = "arbiter_list_effective_rules_by_trigger", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Lists the Arbiter rules the effective configuration sets, one row per rule, with its step, question, answers and effects. Use when asked which rules decide a trigger such as delegate, lens.spawn or gate.select, or what a named rule asks and changes. Use to explain a block before retrying a dispatch. Do not use to decide whether a live dispatch is allowed; use arbiter_evaluate_agent_dispatch_or_return for that.
        """)]
    public string Rules(
        [Description("A trigger to list the rules of. Omit to list every trigger's rules.")]
        string? trigger = null)
    {
        (KyberWeaveConfig? config, string? error) = Load();
        string provenance = Provenance(config);
        if (config is null)
        {
            return Failure(provenance, error ?? "The host configuration could not be loaded.");
        }

        IEnumerable<ArbiterRule> rules = config.Arbiter.Rules;
        if (trigger is not null)
        {
            if (!KnownTrigger(trigger))
            {
                return Failure(provenance, UnknownTrigger(trigger));
            }

            rules = rules.Where(rule => string.Equals(rule.Trigger, trigger, StringComparison.Ordinal));
        }

        List<ArbiterRule> listed = [.. rules
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ThenBy(rule => rule.Trigger, StringComparer.Ordinal)];

        StringBuilder sb = new(provenance);
        sb.Append("\noutcome: listed\nrules: ").Append(listed.Count).Append('\n');
        sb.Append("Closed world: this list is the whole effective rule set for the request; no rule outside it applies.\n");
        foreach (ArbiterRule rule in listed)
        {
            sb.Append("- ").Append(rule.Id)
              .Append(" trigger=").Append(rule.Trigger)
              .Append(" step=").Append(StepFor(rule))
              .Append(" question=\"").Append(rule.Question).Append('"')
              .Append(" answers=").Append(string.Join(", ", rule.Answers))
              .Append(" effects=").Append(string.Join(", ", rule.Effects.Select(pair => $"{pair.Key} -> {pair.Value}")))
              .Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "arbiter_check_configuration_and_key_readiness", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Reports whether the Arbiter is ready for this repository: the root it answers for, the rule-set hash and rule count, the provider, and whether its key resolves. Use when checking configuration before relying on arbiter_evaluate_agent_dispatch_or_return, or when a dispatch failed closed on a configuration or key error. The key value is never reported, only whether one was found.
        """)]
    public string Status()
    {
        (KyberWeaveConfig? config, string? error) = Load();
        string provenance = Provenance(config);
        if (config is null)
        {
            return Failure(provenance, error ?? "The host configuration could not be loaded.");
        }

        ArbiterProviderConfig provider;
        try
        {
            provider = context.EffectiveProvider?.Invoke(config) ?? config.Arbiter.Provider;
        }
        catch (Exception ex)
        {
            return Failure(provenance, $"The user override could not be applied: {OneLine(ex.Message)}");
        }

        bool hasEndpoint = !string.IsNullOrWhiteSpace(provider.Endpoint);
        bool remote = provider.Kind != ArbiterProviderKind.None
            && hasEndpoint
            && !ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint);
        string key;
        try
        {
            // The resolver reaches the OS credential store, which fails for reasons the
            // caller cannot act on. It is guarded like the user override above: the
            // message only, never the stack, and never the key - it reports presence.
            key = !remote
                ? "not needed (none or loopback provider)"
                : context.KeyResolved(config)
                    ? "found"
                    : "missing (KW-ARB-KEY-001)";
        }
        catch (Exception ex)
        {
            return Failure(provenance, $"The key presence check failed: {OneLine(ex.Message)}");
        }

        string origin = hasEndpoint ? ArbiterKeyResolver.GetOrigin(provider.Endpoint) ?? "none" : "none";

        StringBuilder sb = new(provenance);
        sb.Append("\noutcome: status\narbiter status\n");
        sb.Append("  root: ").Append(context.RepoRoot).Append('\n');
        sb.Append("  rule-set: ").Append(ArbiterProvenance.RuleSetHash(config.Arbiter.Rules)).Append('\n');
        sb.Append("  rule-count: ").Append(config.Arbiter.Rules.Count).Append('\n');
        sb.Append("  enabled: ").Append(config.Arbiter.Enabled ? "true" : "false").Append('\n');
        sb.Append("  provider: ").Append(provider.Kind == ArbiterProviderKind.Systemone ? "systemone" : "none")
          .Append(" model=").Append(provider.Model)
          .Append(" origin=").Append(origin).Append('\n');
        sb.Append("  key: ").Append(key);
        return sb.ToString();
    }

    /// <summary>
    /// Lists the sections of one repository document a task needs. Returns JSON with <c>sections</c>, <c>failedSafe</c>
    /// and <c>truncated</c>, or an <c>error</c> when the document is missing or outside the root.
    /// </summary>
    /// <param name="path">The repository-relative document path.</param>
    /// <param name="task">The task the sections are selected for.</param>
    /// <param name="maxSections">The most sections to list; null lists every selected section.</param>
    [McpServerTool(Name = "arbiter_select_relevant_document_sections", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Lists the sections of one repository markdown document that a task needs, each with its id, title and line range, so a worker reads only the sections that bear on the task. Fails safe: when the Arbiter is not configured, unsure, or its provider fails, every section is listed. Returns JSON; a missing document is an error.
        """)]
    public string SelectRelevantDocumentSections(
        [Description("The repository-relative path of the markdown document.")]
        string path,
        [Description("The task the worker is about to do, in plain words.")]
        string task,
        [Description("The most sections to list. Omit to list every selected section.")]
        int? maxSections = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ErrorJson("The document path is empty.");
        }

        string? full = FileTextFacts.ResolveContainedFile(context.RepoRoot, path);
        if (full is null)
        {
            return ErrorJson($"The document '{OneLine(path)}' was not found under the repository root.");
        }

        if (new FileInfo(full).Length > SectionSelector.MaxDocumentBytes)
        {
            return ErrorJson($"The document is larger than {SectionSelector.MaxDocumentBytes} bytes.");
        }

        if (maxSections is < 1)
        {
            return ErrorJson("maxSections must be at least 1.");
        }

        SectionSelection selection = WithProvider(provider =>
            new SectionSelector(provider).SelectAsync(full, task ?? string.Empty).GetAwaiter().GetResult());
        IEnumerable<DocumentSection> shown = maxSections is int cap
            ? selection.Selected.Take(cap)
            : selection.Selected;
        bool truncated = maxSections is int limit && selection.Selected.Count > limit;
        return JsonSerializer.Serialize(new
        {
            sections = shown.Select(section => new
            {
                id = section.Id,
                title = section.Title,
                startLine = section.StartLine,
                endLine = section.EndLine,
            }).ToArray(),
            failedSafe = selection.FailedSafe,
            truncated,
        });
    }

    /// <summary>
    /// Classifies one bot review comment as <c>real</c>, <c>nitpick</c> or <c>already-fixed</c> against the code at the
    /// cited line, and records the decision. Returns JSON with <c>class</c>, <c>confidence</c> and <c>decisionId</c>.
    /// </summary>
    /// <param name="path">The repository-relative path the comment cites.</param>
    /// <param name="line">The 1-based line the comment cites.</param>
    /// <param name="body">The comment text.</param>
    /// <param name="commitSha">The commit the comment was made on, when known.</param>
    /// <param name="summary">When true, <paramref name="body"/> is a PR-level summary; <paramref name="path"/> and <paramref name="line"/> are ignored and each table row is classified.</param>
    /// <param name="author">The login that posted the summary, when known. Only a configured bot's summary is classified.</param>
    [McpServerTool(Name = "arbiter_classify_bot_review_comment", ReadOnly = false, OpenWorld = false)]
    [Description("""
        Classifies one bot review comment as real, nitpick or already-fixed, judging the code at the cited line as it is now. Use before fixing a bot review comment, to skip one that is already fixed or a nitpick. Fails safe to real: any answer that is unsure, below the floor, or not obtained keeps the comment real. Each call records a decision and returns its id. With summary true, the body is a bot's PR-level summary and each row of its findings table is classified; the answer carries items. Returns JSON.
        """)]
    public string ClassifyBotReviewComment(
        [Description("The repository-relative path the comment cites. Ignored when summary is true.")]
        string path,
        [Description("The 1-based line the comment cites. Ignored when summary is true.")]
        int line,
        [Description("The comment text, or the summary text when summary is true.")]
        string body,
        [Description("The commit the comment was made on, when known.")]
        string? commitSha = null,
        [Description("True when the body is a PR-level summary with a findings table.")]
        bool summary = false,
        [Description("The login that posted the summary, when known.")]
        string? author = null)
    {
        if (body is null)
        {
            return ErrorJson("The comment body is missing.");
        }

        if (body.Length > MaxTextChars)
        {
            return ErrorJson($"The comment is larger than {MaxTextChars} characters.");
        }

        string arbiterDirectory = Path.Combine(context.RepoRoot, "artifacts", "arbiter");
        string ruleSet = ArbiterProvenance.RuleSetHash(ArbiterConfig.ProductDefaults.Rules);
        if (summary)
        {
            IReadOnlyList<string> botLogins = BotLoginsForRoot();
            CommentSummaryResult summaryResult = WithProvider(provider =>
                new CommentTriageService(provider, context.RepoRoot, arbiterDirectory, TimeProvider.System, ruleSet, botLogins)
                    .ClassifySummaryAsync(new CommentSummaryRequest(body, author))
                    .GetAwaiter()
                    .GetResult());
            return JsonSerializer.Serialize(new
            {
                items = summaryResult.Items.Select(item => new
                {
                    path = item.Path,
                    line = item.Line,
                    issue = item.Issue,
                    @class = item.Class,
                    confidence = item.Confidence,
                    decisionId = item.DecisionId,
                }),
            });
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ErrorJson("The comment path is empty.");
        }

        if (line < 1)
        {
            return ErrorJson("The comment line must be at least 1.");
        }

        // The same containment answer the sections tool gives: a path that leaves the root is an error, not a read.
        if (FileTextFacts.ResolveWithinRoot(context.RepoRoot, path) is null)
        {
            return ErrorJson($"The document '{OneLine(path)}' was not found under the repository root.");
        }

        CommentTriageResult result = WithProvider(provider =>
            new CommentTriageService(provider, context.RepoRoot, arbiterDirectory, TimeProvider.System, ruleSet)
                .ClassifyAsync(new CommentTriageRequest(path, line, body, commitSha))
                .GetAwaiter()
                .GetResult());
        return JsonSerializer.Serialize(new
        {
            @class = result.Class,
            confidence = result.Confidence,
            decisionId = result.DecisionId,
        });
    }

    /// <summary>
    /// The bot logins from <c>arbiter.comment-triage.bot-logins</c>. A root whose configuration cannot be loaded has
    /// none, so every summary stays real: the fail-safe answer.
    /// </summary>
    private IReadOnlyList<string> BotLoginsForRoot()
    {
        (KyberWeaveConfig? config, string? error) = Load();
        return error is null && config is not null ? config.Arbiter.CommentTriage.BotLogins : [];
    }

    /// <summary>
    /// Runs <paramref name="use"/> with the provider the host configuration names. The served surface is synchronous,
    /// as the engine is, so the async service is awaited here. A provider the tool builds is disposed afterwards.
    /// </summary>
    private T WithProvider<T>(Func<IArbiterProvider, T> use)
    {
        IArbiterProvider provider = ProviderForRoot();
        try
        {
            return use(provider);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The provider for the root's configuration. A root with no usable configuration, or with the Arbiter disabled,
    /// answers with <see cref="NoneProvider"/>, so both tools fail safe rather than refuse.
    /// </summary>
    private IArbiterProvider ProviderForRoot()
    {
        if (context.Engine is not IArbiterProviderSource source)
        {
            return new NoneProvider();
        }

        try
        {
            KyberWeaveConfig config = context.LoadConfig(context.RepoRoot);
            return config.Arbiter.Enabled ? source.ProviderFor(config) : new NoneProvider();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or YamlDotNet.Core.YamlException)
        {
            // Unusable configuration answers fail-safe, and the reason goes to the log so it is not silent.
            context.Log.WriteLine($"arbiter: no provider from the configuration, answering without one: {OneLine(ex.Message)}");
            return new NoneProvider();
        }
    }

    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { error = message });

    private (KyberWeaveConfig? Config, string? Error) Load()
    {
        try
        {
            return (context.LoadConfig(context.RepoRoot), null);
        }
        catch (Exception ex)
        {
            return (null, OneLine(ex.Message));
        }
    }

    private string Provenance(KyberWeaveConfig? config) =>
        config is null
            ? ArbiterProvenance.Line(context.RepoRoot, ArbiterProvenance.Unavailable, 0)
            : ArbiterProvenance.Line(context.RepoRoot, ArbiterProvenance.RuleSetHash(config.Arbiter.Rules), config.Arbiter.Rules.Count);

    private static string Failure(string provenance, string message) =>
        provenance + "\noutcome: error\n" + message;

    /// <summary>
    /// Names the triggers there are. The trigger is client-controlled and the response
    /// is line-oriented, so it is flattened here rather than quoted: a newline in it
    /// would otherwise forge an <c>outcome:</c> line the caller reads as the verdict.
    /// </summary>
    private static string UnknownTrigger(string? trigger) =>
        $"Unknown Arbiter trigger '{OneLine(trigger)}'. Known triggers: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}.";

    private static bool KnownTrigger(string trigger) =>
        ArbiterTriggerFamilies.KnownTriggers.Contains(trigger, StringComparer.Ordinal);

    private static bool TryPhase(string? phase, out bool post)
    {
        post = false;
        if (string.IsNullOrWhiteSpace(phase) || string.Equals(phase, "pre", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(phase, "post", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "return", StringComparison.OrdinalIgnoreCase))
        {
            post = true;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string?> LensesOf(ArbiterFacts facts) =>
        facts.Lens is { Count: > 0 } lenses
            ? [.. lenses.Select(lens => (string?)lens)]
            : [null];

    /// <summary>The target a lens or finding call routes to when the caller names none.
    /// The fallback contract passes routing through <c>lens</c> and <c>finding</c> and
    /// writes no routing header, so the tool names the spawn target itself.</summary>
    private static string? RoutingTarget(ArbiterFacts facts) =>
        facts.Lens is { Count: > 0 } || Present(facts.Finding) ? "review-lens" : null;

    /// <summary>
    /// Builds the routing header block the hook path would see for one unit: the marker
    /// first, then the facts as headers. A refutation unit carries exactly one finding id,
    /// so its <c>REFUTE:</c> names one finding and classifies as refute.spawn.
    /// </summary>
    private static string PromptFor(ArbiterFacts facts, EvaluationUnit unit)
    {
        List<string> lines = ["KYBER-ARBITER: true"];
        if (Present(facts.PlanFile))
        {
            lines.Add("PLAN_FILE: " + Flat(facts.PlanFile));
        }

        if (Present(facts.Task))
        {
            lines.Add("TASK: " + Flat(facts.Task));
        }

        if (Present(unit.Lens))
        {
            lines.Add("LENS: " + Flat(unit.Lens));
        }

        if (Present(unit.Refute))
        {
            lines.Add("REFUTE: " + Flat(unit.Refute));
        }

        if (Present(unit.Finding))
        {
            lines.Add("FINDING: " + Flat(unit.Finding));
        }

        if (facts.Prompt is not null)
        {
            // A blank line ends the header block: HeaderBlock.Parse stops at the first
            // line that is not a header, and HeaderBlock.Strip drops that one separator.
            // Without it a client prompt opening with `PLAN_FILE: ...` or `LENS: ...`
            // reads as a duplicate header and silently drops the fact the tool just
            // wrote, so the caller's own routing is what classifies the event.
            lines.Add(string.Empty);
            lines.Add(facts.Prompt);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Splits a finding payload at each <c>id:</c> line, so each finding is refuted with
    /// its own text and its own single-id header.
    /// </summary>
    private static List<FindingBlock> FindingBlocks(string finding)
    {
        MatchCollection starts = FindingIdPattern.Matches(finding);
        List<FindingBlock> blocks = [];
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1].Index : finding.Length;
            blocks.Add(new FindingBlock(starts[i].Groups["id"].Value, finding[starts[i].Index..end]));
        }

        return blocks;
    }

    /// <summary>
    /// The reason a refutation call cannot be evaluated, or null when every finding id is a
    /// single <c>lens/slug</c> and unique. Each message says what to pass instead.
    /// </summary>
    private static string? FindingIdProblem(IReadOnlyList<FindingBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return "The finding names no id: pass each finding as YAML with an `id: lens/slug` line, " +
                "for example `id: security/key-in-argv`.";
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (FindingBlock block in blocks)
        {
            if (block.Id.Length == 0)
            {
                return "A finding has an empty `id:`: give each finding an `id: lens/slug` line, " +
                    "for example `id: security/key-in-argv`.";
            }

            if (!HeaderBlock.Parse("REFUTE: " + block.Id).TryGet("REFUTE", out _))
            {
                return $"The finding id '{block.Id}' is not lens/slug: use one lens name, a slash and a lowercase slug, " +
                    "for example `id: security/key-in-argv`.";
            }

            if (!seen.Add(block.Id))
            {
                return $"The finding id '{block.Id}' appears more than once: pass each finding once, " +
                    "as its own `id: lens/slug`.";
            }
        }

        return null;
    }

    private static readonly Regex FindingIdPattern = new(
        @"^[ \t]*-?[ \t]*id:[ \t]*(?<id>\S*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Flat(string? value) => OneLine(value).Trim();

    private static string OneLine(string? value) =>
        (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static string OutcomeName(HookOutcomeKind kind) => kind switch
    {
        HookOutcomeKind.PostAnnotation => "annotate",
        HookOutcomeKind.Deny or HookOutcomeKind.PostBlock => "block",
        _ => "allow",
    };

    private static string StepFor(ArbiterRule rule)
    {
        bool step0 = rule.Decide is { Count: > 0 };
        bool step1 = rule.Ask is not null;
        return (step0, step1) switch
        {
            (true, true) => "0+1",
            (true, false) => "0",
            (false, true) => "1",
            _ => "-",
        };
    }

    /// <summary>One evaluation: a lens, or one finding id of a refutation (<paramref name="Refute"/>).</summary>
    private sealed record EvaluationUnit(string? Lens, string? Refute, string? Finding);

    private sealed record FindingBlock(string Id, string Text);

    private sealed record PendingEvaluation(string Caller, string? Target, string Prompt, string? Refute);
}
