using System.Text.Json;
using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Answers the events that pass through untouched without a decision engine or any service, so a hook process that has
/// nothing to decide builds nothing. It asks the same adapters the hook command asks, in the same order.
/// </summary>
public static class HookFastPath
{
    /// <summary>Whether the harness's adapter passes this event through untouched.</summary>
    /// <param name="harness">The <c>--harness</c> token.</param>
    /// <param name="caller">The rendered <c>--caller</c>, or null.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <returns>
    /// True only when the harness is served, the event is a JSON object and its adapter says it passes through. In every
    /// other case, including an adapter that throws, false: the hook command decides, and reports what went wrong.
    /// </returns>
    public static bool IsPassThrough(string harness, string? caller, string stdin)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        try
        {
            HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(
                UnavailableDecisionEngine.Instance, UnavailableDecisionEngine.Instance);
            if (!registry.TryGet(harness, out IHarnessHookAdapter? adapter) || adapter is null)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(stdin);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && adapter.IsPassThrough(document.RootElement, caller);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
