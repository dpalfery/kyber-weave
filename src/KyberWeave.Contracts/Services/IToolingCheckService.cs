namespace KyberWeave.Contracts.Services;

/// <summary>
/// The run-start tooling check the docs MCP server exposes: the same doctor run as <c>kyber-weave doctor --ensure</c>,
/// for one checkout, so a caller with no shell can prepare and prove the code index and the docs server.
/// </summary>
public interface IToolingCheckService
{
    /// <summary>Runs the ensure-mode doctor against the bound checkout and reports its checks and exit code.</summary>
    /// <returns>The tool's complete response text.</returns>
    string RunToolingCheck();
}
