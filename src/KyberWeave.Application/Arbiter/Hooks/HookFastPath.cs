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
    public static bool IsPassThrough(string harness, string? caller, string stdin) =>
        TryPassThrough(harness, caller, stdin, out _);

    /// <summary>Whether the harness's adapter passes this event through, and the document it answers with.</summary>
    /// <param name="harness">The <c>--harness</c> token.</param>
    /// <param name="caller">The rendered <c>--caller</c>, or null.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="document">The adapter's allow document for the event, or null when the harness answers with silence.</param>
    /// <returns>True under the same conditions as <see cref="IsPassThrough"/>.</returns>
    public static bool TryPassThrough(string harness, string? caller, string stdin, out string? document)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        document = null;
        try
        {
            HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(
                UnavailableDecisionEngine.Instance, UnavailableDecisionEngine.Instance);
            if (!registry.TryGet(harness, out IHarnessHookAdapter? adapter) || adapter is null)
            {
                return false;
            }

            using JsonDocument parsed = JsonDocument.Parse(stdin);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !adapter.IsPassThrough(parsed.RootElement, caller))
            {
                return false;
            }

            document = adapter.AllowDocument(parsed.RootElement);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
