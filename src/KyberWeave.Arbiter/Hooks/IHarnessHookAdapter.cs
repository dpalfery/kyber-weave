using System.Text.Json;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>What the hook host decided for one harness event.</summary>
public enum HookOutcomeKind
{
    /// <summary>The event passes untouched: empty stdout, exit 0.</summary>
    PassThrough,

    /// <summary>Allow. On Claude <c>PreToolUse</c> to an implementation specialist this
    /// also carries the stripped input; elsewhere it writes nothing.</summary>
    Allow,

    /// <summary>Allow with an explicit rewritten input document.</summary>
    AllowWithRewrite,

    /// <summary>Deny a pre-dispatch event with <c>Reason</c> as the block reason.</summary>
    Deny,

    /// <summary>Block a post-dispatch event with <c>Reason</c> as the block reason.</summary>
    PostBlock,

    /// <summary>Post-dispatch annotation delivered as additional context.</summary>
    PostAnnotation,
}

/// <summary>One decision from the hook decision engine.</summary>
/// <param name="Kind">The kind of decision.</param>
/// <param name="Reason">The block reason (envelope or note), for deny and post outcomes.</param>
/// <param name="RewrittenInputJson">The complete rewritten input, for allow-with-rewrite.</param>
public sealed record HookOutcome(
    HookOutcomeKind Kind,
    string? Reason = null,
    string? RewrittenInputJson = null);

/// <summary>What a classified event is evaluated against.</summary>
/// <param name="RepoRoot">The repository root configuration is loaded from.</param>
/// <param name="NewDecisionId">Builds the decision id carried by error envelopes.</param>
/// <param name="Log">Diagnostics sink. Always stderr in production, never stdout.</param>
public sealed record HookContext(
    string RepoRoot,
    Func<string> NewDecisionId,
    TextWriter Log);

/// <summary>The dispatch-gating decision behind a hook: later tasks replace the default
/// allow-all engine with step-0/step-1 evaluation (the <c>serve</c>/<c>eval</c> entry
/// points). The hook host fails closed around it: an engine throw becomes that
/// harness's block carrying <c>KW-ARB-HOOK-001</c>, never a pass.</summary>
public interface IHookDecisionEngine
{
    /// <summary>Decides a pre-dispatch event for <paramref name="caller"/> dispatching
    /// <paramref name="target"/> with <paramref name="prompt"/>.</summary>
    HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config);

    /// <summary>Decides a post-dispatch (return) event. <paramref name="toolOutput"/> is
    /// the harness's serialized tool result.</summary>
    HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config);
}

/// <summary>One harness's hook dialect: fast-path detection without configuration,
/// decision rendering for classified events, and the fail-closed block.</summary>
public interface IHarnessHookAdapter
{
    /// <summary>The <c>--harness</c> token this adapter serves (for example <c>claude</c>).</summary>
    string HarnessToken { get; }

    /// <summary>True when the event passes untouched. Must not touch configuration:
    /// the host returns before <c>.kyber-weave/kyber-weave.yml</c> is loaded.</summary>
    bool IsPassThrough(JsonElement payload, string? renderedCaller);

    /// <summary>Handles a classified event. Returns the exact stdout text:
    /// empty means proceed.</summary>
    string Handle(
        JsonElement payload,
        string rawJson,
        string? renderedCaller,
        KyberWeaveConfig config,
        HookContext context);

    /// <summary>Renders the fail-closed block for <paramref name="detail"/>, carrying
    /// <c>KW-ARB-HOOK-001</c>. Never throws: the host falls back further if it does.</summary>
    string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId);
}
