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
    public static HarnessAdapterRegistry CreateDefault(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new HarnessAdapterRegistry(
            CommandHookAdapters.All(engine).Concat(PluginHookAdapters.All()));
    }

    /// <summary>Finds the adapter for <paramref name="token"/>.</summary>
    public bool TryGet(string token, out IHarnessHookAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _adapters.TryGetValue(token, out adapter);
    }
}
