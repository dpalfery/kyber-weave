namespace KyberWeave.Contracts.Repositories;

/// <summary>Answers whether the Copilot CLI user MCP config declares a named server.</summary>
/// <remarks>
/// The answer is honest rather than optimistic: any failure to establish that the server is declared returns
/// <c>false</c>, because a tool entry the agent cannot call is worse than a missing overlay.
/// </remarks>
public interface ICopilotMcpServerProbe
{
    /// <summary>Gets whether the config under a Copilot root declares a server.</summary>
    /// <param name="copilotRoot">The Copilot home directory.</param>
    /// <param name="serverName">The server name to look for, as an exact key.</param>
    /// <returns>
    /// True only when the config resolves to a non-empty, bounded regular file holding valid JSON whose
    /// <c>mcpServers</c> object contains the name; false in every other case.
    /// </returns>
    bool HasServer(string copilotRoot, string serverName);
}
