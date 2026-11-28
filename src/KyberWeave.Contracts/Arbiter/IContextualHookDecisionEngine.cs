using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// A hook decision engine that evaluates with the event's full context: the harness
/// token, the repository root, the original prompt on return events, and the
/// harness tool-call and session ids for ledger pairing.
/// </summary>
/// <remarks>
/// The base <see cref="IHookDecisionEngine"/> port stays untouched so the existing
/// scripted engines keep compiling and the fast-path tests keep proving no config
/// load: adapters prefer these overloads when the engine implements them and fall
/// back to the base methods otherwise. The base methods alone cannot reach the
/// repository root (which the adapter holds in <see cref="HookContext"/>), the
/// harness token, or the return event's prompt, so a context-free call evaluates
/// from the process directory with no prompt history.
/// </remarks>
public interface IContextualHookDecisionEngine : IHookDecisionEngine
{
    /// <summary>Decides a pre-dispatch event with the harness and repository context, without blocking a thread.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="prompt">The dispatch prompt.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="toolCallId">The harness tool-call id, when it has one.</param>
    /// <param name="sessionId">The harness session id, when it has one.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the dispatch.</returns>
    Task<HookOutcome> DecidePreDispatchAsync(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default) =>
        DecidePreDispatchAsync(
            harness,
            caller,
            target,
            prompt,
            config,
            context,
            toolCallId,
            sessionId,
            dispatchModel: null,
            deliveredPrompt: null,
            cancellationToken);

    /// <summary>
    /// Decides a pre-dispatch event that also carries the model the dispatch input names for its
    /// sub-agent, without blocking a thread. The default forwards to the overload above and drops
    /// the model, so an engine that predates the model field keeps compiling and decides as it did.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="prompt">The dispatch prompt.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="toolCallId">The harness tool-call id, when it has one.</param>
    /// <param name="sessionId">The harness session id, when it has one.</param>
    /// <param name="dispatchModel">The <c>model</c> the caller put on the dispatch input, or null when it named none.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the dispatch.</returns>
    Task<HookOutcome> DecidePreDispatchAsync(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId,
        string? dispatchModel = null,
        CancellationToken cancellationToken = default) =>
        DecidePreDispatchAsync(
            harness,
            caller,
            target,
            prompt,
            config,
            context,
            toolCallId,
            sessionId,
            dispatchModel,
            deliveredPrompt: null,
            cancellationToken);

    /// <summary>
    /// Decides a pre-dispatch event whose hook delivers a different prompt to the worker than the
    /// caller wrote, without blocking a thread.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="prompt">The dispatch prompt as the caller wrote it.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="toolCallId">The harness tool-call id, when it has one.</param>
    /// <param name="sessionId">The harness session id, when it has one.</param>
    /// <param name="dispatchModel">The <c>model</c> the caller put on the dispatch input, or null when it named none.</param>
    /// <param name="deliveredPrompt">The prompt the hook inserts for the worker, or null when it inserts none.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the dispatch.</returns>
    Task<HookOutcome> DecidePreDispatchAsync(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId,
        string? dispatchModel,
        string? deliveredPrompt,
        CancellationToken cancellationToken = default);

    /// <summary>Decides a post-dispatch (return) event with the harness and repository context, without blocking a thread.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="prompt">The original dispatch prompt, best effort.</param>
    /// <param name="toolOutput">The harness's serialized tool result.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="toolCallId">The harness tool-call id, when it has one.</param>
    /// <param name="sessionId">The harness session id, when it has one.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the return.</returns>
    Task<HookOutcome> DecidePostDispatchAsync(
        string harness,
        string caller,
        string? target,
        string prompt,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default);
}
