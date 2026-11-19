using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>One harness's hook dialect: fast-path detection without configuration,
/// decision rendering for classified events, and a block document for a fault.</summary>
public interface IHarnessHookAdapter
{
    /// <summary>The <c>--harness</c> token this adapter serves (for example <c>claude</c>).</summary>
    string HarnessToken { get; }

    /// <summary>
    /// Whether the harness delivers a sub-agent's return to the post-dispatch hook. When
    /// false, <c>READY-001</c> answers <c>returns-unobservable</c>
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

    /// <summary>
    /// The document that allows this particular event, for a harness whose allow differs by event (Antigravity
    /// answers a pre-tool event with a decision and a post-tool event with <c>{}</c>). Defaults to
    /// <see cref="DisabledAllowDocument"/>. Must not touch configuration.
    /// </summary>
    /// <param name="payload">The harness event, or the default element when it could not be parsed.</param>
    string? AllowDocument(JsonElement payload) => DisabledAllowDocument;

    /// <summary>
    /// The question that finds the hook sources which would also fire for this payload, or null when the payload is
    /// not a sub-agent dispatch or the harness has no per-dispatch probe. The user-level hook asks the coverage reader
    /// with it to decide whether a project hook already covers the dispatch. Must not touch configuration.
    /// </summary>
    /// <param name="payload">The harness event.</param>
    HookCoverageQuery? CoverageQuery(JsonElement payload) => null;

    /// <summary>
    /// The claim that one evaluation of this payload takes, or null when the payload is not a sub-agent
    /// dispatch event or carries no tool use id. Must not touch configuration.
    /// </summary>
    DispatchClaimKey? ClaimKey(JsonElement payload) => null;

    /// <summary>
    /// The document that carries the prompt in place of a dispatch's identity line, or null when the
    /// payload needs no insertion or this harness cannot rewrite a dispatch.
    /// </summary>
    /// <remarks>
    /// The host calls this when the Arbiter is off, so prompt insertion is not part of the Arbiter. With the
    /// Arbiter on the adapter calls the same insertion from the allow path, after the rules judged the short
    /// dispatch. A prompt that cannot be built is answered with the harness's block document, which names the
    /// repair command, so no worker starts with only a task id.
    /// </remarks>
    /// <param name="payload">The harness event.</param>
    /// <param name="renderedCaller">The <c>--caller</c> the hook command was given, when it was.</param>
    /// <param name="context">The hook context carrying the prompt service.</param>
    string? RenderPromptInsertion(JsonElement payload, string? renderedCaller, HookContext context) => null;

    /// <summary>Handles a classified event. Returns the exact stdout text:
    /// empty means proceed.</summary>
    string Handle(
        JsonElement payload,
        string rawJson,
        string? renderedCaller,
        KyberWeaveConfig config,
        HookContext context);
}
