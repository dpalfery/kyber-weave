using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
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
/// The three Arbiter tools the D4 fallback serves over stdio: evaluate an event, list the
/// rules, and report readiness. Every response leads with the provenance line.
/// </summary>
/// <remarks>
/// <c>arbiter_evaluate</c> routes through the hook's own decision engine rather than a
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

    [McpServerTool(Name = "arbiter_evaluate", ReadOnly = false, OpenWorld = false)]
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
            return Failure(provenance, $"Unknown phase '{facts.Phase}'. Use pre, post or return.");
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

        string? target = facts.Target ?? RoutingTarget(facts);
        List<PendingEvaluation> pending = [];
        foreach (string? lens in LensesOf(facts))
        {
            string prompt = PromptFor(facts, lens);
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
                    $"The facts classify as '{classification.Trigger ?? "no trigger"}', not '{trigger}'. " +
                    "Pass the trigger the event classifies as.");
            }

            pending.Add(new PendingEvaluation(classification.Caller ?? UnidentifiedCaller, target, prompt));
        }

        HookContext hookContext = new(context.RepoRoot, () => ArbiterRecordId.New(DateTimeOffset.UtcNow), context.Log);
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

            if (outcome.Kind is not (HookOutcomeKind.Allow or HookOutcomeKind.PassThrough or HookOutcomeKind.AllowWithRewrite))
            {
                return provenance + "\noutcome: " + OutcomeName(outcome.Kind) + "\n" + outcome.Reason;
            }
        }

        return provenance + "\noutcome: allow";
    }

    [McpServerTool(Name = "arbiter_rules", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Lists the Arbiter rules the effective configuration sets, one row per rule, with its step, question, answers and effects. Use when asked which rules decide a trigger such as delegate, lens.spawn or gate.select, or what a named rule asks and changes. Use to explain a block before retrying a dispatch. Do not use to decide whether a live dispatch is allowed; use arbiter_evaluate for that.
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

    [McpServerTool(Name = "arbiter_status", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Reports whether the Arbiter is ready for this repository: the root it answers for, the rule-set hash and rule count, the provider, and whether its key resolves. Use when checking configuration before relying on arbiter_evaluate, or when a dispatch failed closed on a configuration or key error. The key value is never reported, only whether one was found.
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
        string key = !remote
            ? "not needed (none or loopback provider)"
            : context.KeyResolved(config)
                ? "found"
                : "missing (KW-ARB-KEY-001)";
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

    private static string UnknownTrigger(string? trigger) =>
        $"Unknown Arbiter trigger '{trigger}'. Known triggers: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}.";

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
    /// Builds the routing header block the hook path would see for the same event:
    /// the marker first, then the facts as headers. A finding synthesizes
    /// <c>REFUTE:</c> from the finding ids it carries, so a code-reviewer following
    /// its own contract gets a classified refute.spawn without writing a header.
    /// </summary>
    private static string PromptFor(ArbiterFacts facts, string? lens)
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

        if (Present(lens))
        {
            lines.Add("LENS: " + Flat(lens));
        }

        string? refute = RefuteHeader(facts.Finding);
        if (refute is not null)
        {
            lines.Add("REFUTE: " + refute);
        }

        if (Present(facts.Finding))
        {
            lines.Add("FINDING: " + Flat(facts.Finding));
        }

        if (facts.Prompt is not null)
        {
            lines.Add(facts.Prompt);
        }

        return string.Join('\n', lines);
    }

    /// <summary>The REFUTE header value for a finding: the finding ids it carries, as
    /// <c>lens/slug</c>. Null when the finding names no id — the event then fails
    /// classification instead of recording a made-up refutation.</summary>
    private static string? RefuteHeader(string? finding)
    {
        if (string.IsNullOrWhiteSpace(finding))
        {
            return null;
        }

        List<string> ids = [];
        foreach (Match match in FindingIdPattern.Matches(finding))
        {
            string id = Flat(match.Groups["id"].Value);
            if (id.Length > 0 && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0 ? string.Join(", ", ids) : null;
    }

    private static readonly Regex FindingIdPattern = new(
        @"^\s*-?\s*id:\s*(?<id>\S+)",
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

    private sealed record PendingEvaluation(string Caller, string? Target, string Prompt);
}
