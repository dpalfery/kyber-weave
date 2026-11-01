namespace KyberWeave.Core.Doctor;

/// <summary>The budgets and executable names one doctor run uses.</summary>
/// <param name="IndexTimeout">
///     The bound on one <c>codegraph init</c> or <c>index</c> run, and the longest a second doctor waits for the
///     index lock. The executor that runs the CodeGraph child must be bounded by the same value.
/// </param>
/// <param name="McpTimeout">The total budget of the live MCP session.</param>
/// <param name="CodeGraphExecutable">The CodeGraph executable, resolved through PATH.</param>
/// <param name="McpExecutable">The MCP executable, resolved through PATH with no repository-build fallback.</param>
public sealed record DoctorOptions(
    TimeSpan IndexTimeout,
    TimeSpan McpTimeout,
    string CodeGraphExecutable,
    string McpExecutable);
