using KyberWeave.Arbiter.Hooks.Adapters;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The harnesses whose hooks spawn the binary by argv, with no shell.</summary>
public static class CommandHookAdapters
{
    /// <summary>The command-hook adapters: the Claude entry starts the list, followed by the two Copilot entries and the Codex, Cursor, Antigravity, Factory and Devin entries.</summary>
    public static IReadOnlyList<IHarnessHookAdapter> All(IHookDecisionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return
        [
            new ClaudeHookAdapter(engine),
            new CopilotHookAdapter(engine, CopilotHookAdapter.VsCodeToken),
            new CopilotHookAdapter(engine, CopilotHookAdapter.CliToken),
            new CodexHookAdapter(engine),
            new CursorHookAdapter(engine),
            new AntigravityHookAdapter(engine),
            new FactoryHookAdapter(engine),
            new DevinHookAdapter(engine),
        ];
    }
}
