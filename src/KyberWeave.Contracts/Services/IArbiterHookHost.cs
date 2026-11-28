using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Contracts.Services;

/// <summary>The hook host: decides one harness hook event and writes the harness's decision document.</summary>
public interface IArbiterHookHost
{
    /// <summary>
    /// Runs one hook event. Returns 0 for every event: blocks ride stdout. An unknown harness token is a usage error with
    /// no harness document to write; it returns 1 with stderr diagnostics.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="caller">The rendered <c>--caller</c>, or null.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the decision document, and nothing else.</param>
    /// <param name="log">Receives diagnostics: stderr in production.</param>
    /// <param name="scope">The project hook or the user-level hook.</param>
    /// <param name="homeDirectory">The operator's home, or null for the user profile.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The process exit code.</returns>
    Task<int> RunAsync(
        string harness,
        string? caller,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        HookScope scope = HookScope.Project,
        string? homeDirectory = null,
        CancellationToken cancellationToken = default);
}
