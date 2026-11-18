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
    public IReadOnlyList<TimeSpan> HeldLockRetryPauses { get; init; } = DefaultHeldLockRetryPauses;

    /// <summary>
    ///     The pauses before the second and third <c>codegraph sync</c> when the first finds CodeGraph's own index
    ///     lock held.
    /// </summary>
    /// <remarks>
    ///     Measured against CodeGraph 1.6.2: a sync that finds the lock held returns in about 0.15 seconds, and a
    ///     sync with nothing to do holds the lock for about 0.3 seconds. Three attempts one second apart in total
    ///     outlast that and the short syncs a file watcher runs, and cost the caller at most about 1.5 seconds. A
    ///     longer hold is an index rebuild, which no wait an interactive call can afford would outlast.
    /// </remarks>
    public static IReadOnlyList<TimeSpan> DefaultHeldLockRetryPauses { get; } =
        [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700)];

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
