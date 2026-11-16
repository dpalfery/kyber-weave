namespace KyberWeave.Core.Review;

/// <summary>How the review loop stands after a cycle's verdict.</summary>
public enum ReviewLoopState
{
    /// <summary>Nothing is left to fix: the loop ends and the run goes on to archive.</summary>
    Clean,

    /// <summary>Findings are left to fix and no cap is reached: fix them, then run a verifier cycle.</summary>
    Continue,

    /// <summary>The cycle cap is reached with findings still open.</summary>
    CycleCap,

    /// <summary>Three cycles in a row did not lower the number of findings to fix, with findings still open.</summary>
    NoProgress
}

/// <summary>
/// Decides whether the review loop goes on. A cycle's count is its <c>fix:</c> lines: every failed blocking gate and
/// every finding the active policy sends to a fix round, so a loop that only ever finds minor findings is counted
/// too under the default <c>fix-level: minor</c>.
/// </summary>
public static class ReviewLoop
{
    /// <summary>The most review cycles a loop runs.</summary>
    public const int MaxCycles = 5;

    /// <summary>How many cycles in a row may fail to lower the count before the loop stops.</summary>
    public const int NoProgressCycles = 3;

    /// <summary>Where the loop stands after the last of <paramref name="counts"/>.</summary>
    /// <param name="counts">The count of findings to fix after each cycle, oldest first; the current cycle is last.</param>
    public static ReviewLoopState Decide(IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count == 0 || counts[^1] == 0)
            return ReviewLoopState.Clean;

        if (counts.Count >= MaxCycles)
            return ReviewLoopState.CycleCap;

        // A cycle makes no progress when its count is not below the one before it, so three such cycles in a row
        // need four counts: three fix rounds are done before this stops the loop.
        if (counts.Count > NoProgressCycles
            && Enumerable.Range(counts.Count - NoProgressCycles, NoProgressCycles).All(index => counts[index] >= counts[index - 1]))
        {
            return ReviewLoopState.NoProgress;
        }

        return ReviewLoopState.Continue;
    }
}
