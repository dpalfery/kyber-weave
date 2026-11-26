using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The plugin harnesses (OpenCode, Kilo, Pi, Command Code), which get a rendered TypeScript shim
/// or mod instead of a spawned command. The registry already composes this list, so each
/// new shim is a single entry here.
/// </summary>
public static class PluginHookAdapters
{
    // One row per plugin harness: its token and the tool its shim dispatches on.
    // OpenCode 2.2.5 renamed the dispatch tool to subagent and its target argument to agent (its own
    // migration note: "The subagent tool takes agent instead of subagent_type"), so it lists both shapes.
    // Command Code dispatches custom agents through the agent tool with the target at
    // args.subagent_type, verified against the command-code npm package 1.84.0 on 2026-10-17.
    private static readonly (string Token, string[] DispatchTools, string[] TargetArguments)[] Entries =
    [
        ("opencode", ["task", "subagent"], ["subagent_type", "agent"]),
        ("kilo", ["task"], ["subagent_type"]),
        ("pi", ["Agent"], ["subagent_type"]),
        ("commandcode", ["agent"], ["subagent_type"]),
    ];

    /// <summary>The plugin-hook adapters: OpenCode and Kilo dispatch on <c>task</c>; Pi dispatches on <c>Agent</c>; Command Code dispatches on <c>agent</c>.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return
        [
            .. Entries.Select(entry => (IHarnessHookAdapter)new PluginHookAdapter(
                entry.Token,
                entry.DispatchTools,
                entry.TargetArguments,
                "prompt",
                engine)),
        ];
    }
}
