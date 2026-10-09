using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The harnesses whose hooks spawn the binary by argv, with no shell.</summary>
public static class CommandHookAdapters
{
    /// <summary>The command-hook adapters: the Claude entry starts the list.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return [new ClaudeHookAdapter(engine)];
    }
}
