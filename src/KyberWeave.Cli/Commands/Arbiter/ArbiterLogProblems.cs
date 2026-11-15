using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// What <c>arbiter audit</c> and <c>arbiter tune</c> say about a log read that did not show the whole log. The hook
/// reads the same logs and stays quiet about both; a report a person reads must not.
/// </summary>
internal static class ArbiterLogProblems
{
    /// <summary>Why the ledger or the decision log exists but could not be read, or null when both were.</summary>
    internal static string? Unreadable<TLedger, TDecision>(
        ArbiterLogRead<TLedger> ledger,
        ArbiterLogRead<TDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisions);
        return ledger.Unreadable is { } ledgerProblem
            ? $"{InFlightLedger.FileName}: {ledgerProblem}"
            : decisions.Unreadable is { } decisionProblem ? $"{DecisionLog.FileName}: {decisionProblem}" : null;
    }

    /// <summary>The sentence naming the logs that were longer than the read window, or null when neither was.</summary>
    internal static string? TruncationNote<TLedger, TDecision>(
        ArbiterLogRead<TLedger> ledger,
        ArbiterLogRead<TDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisions);
        List<string> cut = [];
        if (ledger.Truncated)
        {
            cut.Add(InFlightLedger.FileName);
        }

        if (decisions.Truncated)
        {
            cut.Add(DecisionLog.FileName);
        }

        return cut.Count == 0
            ? null
            : $"{string.Join(" and ", cut)} is longer than the window one read looks at, so its older records were not read.";
    }
}
