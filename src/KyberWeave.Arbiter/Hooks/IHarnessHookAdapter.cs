using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
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

    /// <summary>
    /// Allow with <c>Reason</c> surfaced as a visible note: a hook or decision-model
    /// failure writes to the log and lets the dispatch continue loudly instead of
    /// blocking. Every adapter renders the note where its harness shows text.
    /// </summary>
    AllowWithNote,

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
/// <param name="Reason">The block reason (envelope or note), for deny, post, and allow-with-note outcomes.</param>
/// <param name="RewrittenInputJson">The complete rewritten input, for allow-with-rewrite.</param>
/// <param name="ModelToken">The model alias the decision sets on a sub-agent dispatch, when the harness
/// accepts one. Null leaves the dispatch's model as the harness would have chosen it.</param>
/// <param name="ModelBlock">The delegate.model block the evaluation resolved, delivered or not, when model
/// targeting is not off. The serve path reads it to report the retarget as advisory; no harness writes from it.</param>
public sealed record HookOutcome(
    HookOutcomeKind Kind,
    string? Reason = null,
    string? RewrittenInputJson = null,
    string? ModelToken = null,
    ArbiterModelBlock? ModelBlock = null);

/// <summary>
/// Whether a harness lets the Arbiter set the model of a dispatched sub-agent: the field that carries
/// the model, the model aliases it accepts, and where that declaration was read from.
/// </summary>
/// <param name="Supported">True when the harness honours a model override on a sub-agent dispatch.</param>
/// <param name="Field">The dispatch field that carries the model. Set only when <paramref name="Supported"/> is true.</param>
/// <param name="AcceptedAliases">The model aliases the harness accepts. Set only when <paramref name="Supported"/> is true.</param>
/// <param name="Source">The documentation the declaration was read from, so a reviewer can check the aliases against it.</param>
public sealed record ModelOverrideCapability(
    bool Supported,
    string? Field,
    IReadOnlySet<string>? AcceptedAliases,
    string Source)
{
    /// <summary>
    /// The declaration of a harness that does not let the Arbiter set a sub-agent's model. A rule that
    /// asks for an override on such a harness gets none to apply.
    /// </summary>
    public static ModelOverrideCapability Unsupported { get; } = new(false, null, null, string.Empty);
}

/// <summary>What a classified event is evaluated against.</summary>
/// <param name="RepoRoot">The repository root configuration is loaded from.</param>
/// <param name="NewDecisionId">Builds the decision id carried by error envelopes.</param>
/// <param name="Log">Diagnostics sink. Always stderr in production, never stdout.</param>
/// <param name="ObservesReturns">Whether the serving harness observes sub-agent
/// returns (task 16.9). The hook host copies it from the harness adapter, so
/// <c>harness.observes-returns</c> always carries a value: true for every harness
/// except the adapters that declare otherwise. Never null-by-absence — a missing
/// fact must not silently disable an enforced rule.</param>
public enum HookScope
{
    Project,
    Global,
}

/// <summary>What a classified event is evaluated against, copied from the serving harness's adapter.</summary>
/// <param name="ModelOverride">The serving harness's model-override declaration. Null when the caller
/// supplies none, which a rule reads as unsupported.</param>
/// <param name="ConfirmModelWrite">Writes an accepted model token into the harness input and reports whether
/// the write held. The decision asks it before recording a delivered model. Null when the adapter writes later.</param>
public sealed record HookContext(
    string RepoRoot,
    Func<string> NewDecisionId,
    TextWriter Log,
    bool ObservesReturns = true,
    string? HomeDirectory = null,
    HookScope Scope = HookScope.Project,
    ModelOverrideCapability? ModelOverride = null,
    Func<string, bool>? ConfirmModelWrite = null);

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

    /// <summary>
    /// Whether the harness delivers a sub-agent's return to the post-dispatch hook. When
    /// false, <c>READY-001</c> and <c>MODE-001</c> answer <c>returns-unobservable</c>
    /// (Q17 option (a)): pre-dispatch gating still runs, completion order is advisory.
    /// </summary>
    bool ObservesReturns => true;

    /// <summary>
    /// Whether the harness lets the Arbiter set a sub-agent's model, and through which field. The default
    /// declares no override, so a harness only offers one by saying so here.
    /// </summary>
    ModelOverrideCapability ModelOverride => ModelOverrideCapability.Unsupported;

    /// <summary>True when the event passes untouched. Must not touch configuration:
    /// the host returns before <c>.kyber-weave/kyber-weave.yml</c> is loaded.</summary>
    bool IsPassThrough(JsonElement payload, string? renderedCaller);

    /// <summary>The repository root this harness's payload names, or null when it
    /// carries none. The host prefers it over the payload's <c>cwd</c> and the process
    /// directory, so configuration loads from the workspace the event belongs to:
    /// Antigravity names it in <c>workspacePaths[0]</c> ([F10]) and has no
    /// <c>cwd</c> at all.</summary>
    string? RepoRootOf(JsonElement payload) => null;

    /// <summary>
    /// The document an event is answered with before configuration is loaded, or null
    /// when the event must go down the classified path. Must not touch configuration.
    /// Separate from <see cref="IsPassThrough"/> because most harnesses signal allow by
    /// writing nothing, while harnesses whose only documented output is a document
    /// (Antigravity writes <c>{}</c> for every event, [F10]) must still write it here.
    /// </summary>
    string? AnswerWithoutConfig(JsonElement payload, string? renderedCaller) => null;

    /// <summary>
    /// The document the disabled-arbiter path answers with, or null when the harness
    /// documents proceeding by writing nothing. Most harnesses fall silent on allow;
    /// harnesses whose only documented output is a document (Antigravity, [F10]) must
    /// write their allow shape here too, so a disabled Arbiter is indistinguishable
    /// from an allowed one in that harness's own protocol.
    /// </summary>
    string? DisabledAllowDocument => null;

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
