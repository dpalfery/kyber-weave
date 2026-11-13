using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The plugin harnesses (OpenCode, Kilo, Pi), which get a rendered TypeScript shim
/// instead of a spawned command. The registry already composes this list, so each
/// new shim is a single entry here.
/// </summary>
/// <remarks>
/// The engine parameter is optional so single-adapter tests keep injecting a
/// scripted engine; production passes its real engine through the registry.
/// </remarks>
public static class PluginHookAdapters
{
    // One row per plugin harness: its token and the tool its shim dispatches on.
    // OpenCode 2.2.5 renamed the dispatch tool to subagent and its target argument to agent (its own
    // migration note: "The subagent tool takes agent instead of subagent_type"), so it lists both shapes.
    private static readonly (string Token, string[] DispatchTools, string[] TargetArguments)[] Entries =
    [
        ("opencode", ["task", "subagent"], ["subagent_type", "agent"]),
        ("kilo", ["task"], ["subagent_type"]),
        ("pi", ["Agent"], ["subagent_type"]),
    ];

    /// <summary>The plugin-hook adapters: OpenCode and Kilo dispatch on <c>task</c>; Pi dispatches on <c>Agent</c>.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine? engine = null)
    {
        IHookDecisionEngine resolved = engine ?? new ArbiterHookDecisionEngine();
        return
        [
            .. Entries.Select(entry => (IHarnessHookAdapter)new PluginHookAdapter(
                entry.Token,
                entry.DispatchTools,
                entry.TargetArguments,
                "prompt",
                resolved)),
        ];
    }
}
