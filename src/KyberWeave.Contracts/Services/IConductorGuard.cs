namespace KyberWeave.Contracts.Services;

/// <summary>
/// The Copilot conductor's role boundary: denies the tools the conductor may not use itself and restates the workflow on
/// every model-facing message. A deny is always explicit, so an unreadable event is denied rather than ignored.
/// </summary>
public interface IConductorGuard
{
    /// <summary>Answers one hook event. Always returns 0: a deny rides stdout.</summary>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the decision document, and nothing else.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="allowedMcpServer">An MCP server whose tools the conductor may call, or null.</param>
    /// <param name="copilotHome">Copilot's home directory, or null to resolve it from the environment.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records, or null for the user profile.</param>
    /// <returns>The process exit code.</returns>
    int Run(
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? allowedMcpServer = null,
        string? copilotHome = null,
        string? homeDirectory = null);
}
