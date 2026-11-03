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
    /// <summary>Decides a pre-dispatch event with the harness and repository context.</summary>
    HookOutcome DecidePreDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null);

    /// <summary>
    /// Decides a post-dispatch (return) event with the harness and repository context.
    /// <paramref name="prompt"/> is the original dispatch prompt, best effort, so the
    /// return classifies to the same trigger family as its dispatch.
    /// </summary>
    HookOutcome DecidePostDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null);
}
