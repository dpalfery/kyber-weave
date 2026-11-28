using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The dispatch-gating decision behind a hook: later tasks replace the default
/// allow-all engine with step-0/step-1 evaluation (the <c>serve</c>/<c>eval</c> entry
/// points). The hook host fails open around it: an engine throw is logged with
/// <c>KW-ARB-HOOK-001</c> and the dispatch proceeds (ADR 0072).</summary>
public interface IHookDecisionEngine
{
    /// <summary>Decides a pre-dispatch event for <paramref name="caller"/> dispatching
    /// <paramref name="target"/> with <paramref name="prompt"/>.</summary>
    HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config);

    /// <summary>Decides a post-dispatch (return) event. <paramref name="toolOutput"/> is
    /// the harness's serialized tool result.</summary>
    HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config);

    /// <summary>
    /// Decides a pre-dispatch event for <paramref name="caller"/> dispatching
    /// <paramref name="target"/> with <paramref name="prompt"/>, without blocking a thread.
    /// </summary>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="prompt">The dispatch prompt.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the dispatch.</returns>
    Task<HookOutcome> DecidePreDispatchAsync(
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>Decides a post-dispatch (return) event, without blocking a thread.</summary>
    /// <param name="caller">The dispatching agent.</param>
    /// <param name="target">The dispatched agent, or null when the event names none.</param>
    /// <param name="toolOutput">The harness's serialized tool result.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the return.</returns>
    Task<HookOutcome> DecidePostDispatchAsync(
        string caller,
        string? target,
        string toolOutput,
        KyberWeaveConfig config,
        CancellationToken cancellationToken = default);
}
