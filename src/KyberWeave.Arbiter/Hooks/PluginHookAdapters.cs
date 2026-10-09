using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The plugin harnesses (OpenCode, Kilo, Pi), which get a rendered TypeScript shim
/// instead of a spawned command. The registry already composes this list, so each
/// new shim is a single entry here.
/// </summary>
/// <remarks>
/// The engine parameter is optional so the default registry composition keeps
/// compiling unchanged: production answers through the shared allow-all engine
/// until the serve/eval entry points replace it, while tests inject a scripted
/// engine.
/// </remarks>
public static class PluginHookAdapters
{
    /// <summary>The plugin-hook adapters: OpenCode dispatches on <c>task</c>.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine? engine = null) =>
        [new PluginHookAdapter(
            "opencode",
            "task",
            "subagent_type",
            "prompt",
            engine ?? new AllowAllHookDecisionEngine())];
}
