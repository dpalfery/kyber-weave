namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The hook timeout the harness hooks are rendered with, derived from the provider timeout, and the check that
/// the provider timeout in force at evaluation still fits inside it.
/// </summary>
public static class ArbiterHookTimeout
{
    /// <summary>Seconds of headroom the rendered hook timeout keeps beyond the provider budget.</summary>
    public const int HeadroomSeconds = 2;

    /// <summary>
    /// The shortest timeout any rendered Arbiter hook carries.
    /// </summary>
    /// <remarks>
    /// A provider budget alone is not enough. A hook also starts a process, reads the plan and the git facts, and
    /// appends to the per-project ledger, and under a fan-out twelve to twenty of them run at once on a machine that
    /// is already building. A live Copilot CLI run rendered five seconds from the shipped three-second budget and
    /// logged 55 hook timeouts, almost all in same-second bursts; two of them cost a dispatch its return row and
    /// blocked the run. The floor gives that burst the room the work actually needs, and a provider budget that is
    /// itself longer still keeps its own headroom above this floor.
    /// </remarks>
    public const int MinimumRenderedSeconds = 30;

    /// <summary>The rendered hook timeout in whole seconds: <c>ceil(timeout-ms / 1000) + 2</c>, never below the floor.</summary>
    public static int SecondsFor(int providerTimeoutMs)
    {
        long timeoutMs = Math.Max(1, providerTimeoutMs);
        long seconds = (timeoutMs + 999) / 1000 + HeadroomSeconds;
        return (int)Math.Min(int.MaxValue, Math.Max(MinimumRenderedSeconds, seconds));
    }

    /// <summary>
    /// Whether a provider timeout in force at evaluation leaves the evaluation's own work room inside a hook
    /// rendered from <paramref name="renderedFromTimeoutMs"/>: the effective provider budget plus the headroom must
    /// fit within the rendered seconds. A user override that raises the timeout without re-rendering the hooks
    /// fails this, and the harness then kills the hook before the provider answers.
    /// </summary>
    public static bool Fits(int renderedFromTimeoutMs, int effectiveTimeoutMs)
    {
        long budgetMs = (long)SecondsFor(renderedFromTimeoutMs) * 1000;
        return (long)Math.Max(1, effectiveTimeoutMs) + (HeadroomSeconds * 1000L) <= budgetMs;
    }
}
