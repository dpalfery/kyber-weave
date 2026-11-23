using KyberWeave.Core.Arbiter;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// The guard on a Squad subagent's own tool calls, from machine facts only: it refuses a write into the repository's
/// canonical checkout while a live run works in a worktree of its own, refuses a call that would carry a secret into
/// the transcript, and ends a dispatch that has run past its budget.
/// </summary>
public interface IWorkerGuard
{
    /// <summary>Answers one tool-call event. Always returns 0: a deny rides stdout.</summary>
    /// <param name="harness">The harness token: <c>copilot-cli</c>, <c>claude</c> or <c>factory</c>.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the deny document, and nothing for an allow.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="homeDirectory">The operator's home, or null for the user profile.</param>
    /// <param name="now">The clock, or null for the system clock.</param>
    /// <param name="budgetLoader">Reads the budget for a working directory, or null to read its <c>kyber-weave.yml</c>.</param>
    /// <param name="seatBudgetLoader">Reads a council seat's budget for a working directory, or null to read its <c>kyber-weave.yml</c>.</param>
    /// <returns>The process exit code.</returns>
    int Run(
        string harness,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? homeDirectory = null,
        Func<DateTimeOffset>? now = null,
        Func<string, ArbiterDispatchBudgetConfig?>? budgetLoader = null,
        Func<string, ArbiterDispatchBudgetConfig?>? seatBudgetLoader = null);
}
