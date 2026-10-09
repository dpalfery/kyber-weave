namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The plugin harnesses (OpenCode, Kilo, Pi), which get a rendered TypeScript shim
/// instead of a spawned command. Empty until the first shim lands: the registry
/// already composes this list, so adding one is a single entry here.
/// </summary>
public static class PluginHookAdapters
{
    /// <summary>The plugin-hook adapters: none yet.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All() => [];
}
