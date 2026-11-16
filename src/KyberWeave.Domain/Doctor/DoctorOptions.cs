namespace KyberWeave.Domain.Doctor;

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
    string McpExecutable)
{
    /// <summary>
    ///     The pauses before each further <c>codegraph sync</c> when one finds CodeGraph's own index lock held. A
    ///     test replaces them so it does not wait in real time.
    /// </summary>
    public IReadOnlyList<TimeSpan> HeldLockRetryPauses { get; init; } = CodeGraphIndexRules.HeldLockRetryPauses;

    /// <summary>The default bound on one CodeGraph index run, in seconds. Run-start and review commands reuse it.</summary>
    public const int DefaultIndexTimeoutSeconds = 600;

    /// <summary>The default total budget of the live MCP session, in seconds.</summary>
    public const int DefaultMcpTimeoutSeconds = 20;

    /// <summary>The CodeGraph executable name, resolved through PATH.</summary>
    public const string DefaultCodeGraphExecutable = "codegraph";

    /// <summary>The docs MCP executable name, resolved through PATH. Tests replace it so they never spawn the real server.</summary>
    public const string DefaultMcpExecutableName = "kyber-weave-mcp";

    /// <summary>The budgets and executables <c>doctor --ensure</c> uses, for callers that prepare the index without a doctor run.</summary>
    public static DoctorOptions Defaults { get; } = new(
        TimeSpan.FromSeconds(DefaultIndexTimeoutSeconds),
        TimeSpan.FromSeconds(DefaultMcpTimeoutSeconds),
        DefaultCodeGraphExecutable,
        DefaultMcpExecutableName);
}
