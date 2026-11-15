namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The hook timeout the harness hooks are rendered with, derived from the provider timeout, and the check that
/// the provider timeout in force at evaluation still fits inside it.
/// </summary>
public static class ArbiterHookTimeout
{
    /// <summary>Seconds of headroom the rendered hook timeout keeps beyond the provider budget.</summary>
    public const int HeadroomSeconds = 2;

    /// <summary>The rendered hook timeout in whole seconds: ceil(timeout-ms / 1000) + 2.</summary>
    public static int SecondsFor(int providerTimeoutMs)
    {
        long timeoutMs = Math.Max(1, providerTimeoutMs);
        return (int)Math.Min(int.MaxValue, (timeoutMs + 999) / 1000 + HeadroomSeconds);
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
