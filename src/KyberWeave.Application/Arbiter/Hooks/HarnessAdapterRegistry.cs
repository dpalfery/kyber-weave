using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The harness adapters the host serves, composed from the command-hook list (harnesses
/// that spawn the binary by argv) and the plugin-hook list (harnesses that load a
/// rendered shim instead). Lookup is case-insensitive: harness tokens arrive from
/// frontmatter the Squad renders, and a case drift must not fail a gate open.
/// </summary>
public sealed class HarnessAdapterRegistry
{
    private readonly Dictionary<string, IHarnessHookAdapter> _adapters;

    /// <summary>Creates a registry over the given adapters.</summary>
    public HarnessAdapterRegistry(IEnumerable<IHarnessHookAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = new Dictionary<string, IHarnessHookAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (IHarnessHookAdapter adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            _adapters[adapter.HarnessToken] = adapter;
        }
    }

    /// <summary>The served harness tokens.</summary>
    public IReadOnlySet<string> Tokens =>
        _adapters.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Composes the default registry: the command hooks plus the plugin hooks.</summary>
    public static HarnessAdapterRegistry CreateDefault(IHookDecisionEngine engine, IFactoryTranscriptSource transcripts)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transcripts);
        return new HarnessAdapterRegistry(
            CommandHookAdapters.All(engine, transcripts).Concat(PluginHookAdapters.All(engine)));
    }

    /// <summary>
    /// The document a harness's disabled path answers with, found without a decision engine or any service: no
    /// adapter's <see cref="IHarnessHookAdapter.DisabledAllowDocument"/> reads either. The fail-open path asks it
    /// after a fault, when no container may be built.
    /// </summary>
    /// <param name="token">The harness token.</param>
    /// <returns>The document, or null when the harness has none or is not served.</returns>
    public static string? DisabledAllowDocumentOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        HarnessAdapterRegistry registry = CreateDefault(UnavailableDecisionEngine.Instance, UnavailableDecisionEngine.Instance);
        return registry.TryGet(token, out IHarnessHookAdapter? adapter) && adapter is not null
            ? adapter.DisabledAllowDocument
            : null;
    }

    /// <summary>Finds the adapter for <paramref name="token"/>.</summary>
    public bool TryGet(string token, out IHarnessHookAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _adapters.TryGetValue(token, out adapter);
    }
}

/// <summary>The engine and transcript reader of a registry that only answers fixed documents; any decision it is asked for is a fault.</summary>
internal sealed class UnavailableDecisionEngine : IHookDecisionEngine, IFactoryTranscriptSource
{
    internal static UnavailableDecisionEngine Instance { get; } = new();

    public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeave.Core.Configuration.KyberWeaveConfig config) =>
        throw new InvalidOperationException("No decision engine is available.");

    public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeave.Core.Configuration.KyberWeaveConfig config) =>
        throw new InvalidOperationException("No decision engine is available.");

    public IEnumerable<string>? ReadLines(string path, long maxBytes) =>
        throw new InvalidOperationException("No transcript reader is available.");
}
