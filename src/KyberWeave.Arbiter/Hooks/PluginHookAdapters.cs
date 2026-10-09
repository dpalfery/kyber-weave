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
    // One row per plugin harness: its token and the tool its shim dispatches on. Adding a
    // harness here also gives it the plugin deny dialect in LastResortBlock.
    private static readonly (string Token, string DispatchTool)[] Entries =
    [
        ("opencode", "task"),
        ("kilo", "task"),
        ("pi", "Agent"),
    ];

    /// <summary>The harness tokens that take a plugin shim (and so read a <c>decision</c> document).</summary>
    public static IReadOnlySet<string> Tokens { get; } =
        Entries.Select(entry => entry.Token).ToHashSet(StringComparer.Ordinal);

    /// <summary>The plugin-hook adapters: OpenCode and Kilo dispatch on <c>task</c>; Pi dispatches on <c>Agent</c>.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine? engine = null)
    {
        IHookDecisionEngine resolved = engine ?? new ArbiterHookDecisionEngine();
        return
        [
            .. Entries.Select(entry => (IHarnessHookAdapter)new PluginHookAdapter(
                entry.Token,
                entry.DispatchTool,
                "subagent_type",
                "prompt",
                resolved)),
        ];
    }
}
