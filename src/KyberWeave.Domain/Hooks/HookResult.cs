namespace KyberWeave.Domain.Hooks;

/// <summary>What the hook tells the harness.</summary>
/// <param name="ExitCode">0 to continue silently, 2 to hand <paramref name="Feedback"/> to the agent.</param>
/// <param name="Feedback">Short text for stderr; empty on success and skip.</param>
public sealed record HookResult(int ExitCode, string Feedback)
{
    /// <summary>Gets the silent success result.</summary>
    public static HookResult Silent { get; } = new(0, string.Empty);
}
