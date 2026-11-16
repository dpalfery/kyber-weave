namespace KyberWeave.Core.Review;

/// <summary>A task gate with its argv expanded for one run, ready to start.</summary>
/// <param name="Id">Slug identifying the gate within its agent.</param>
/// <param name="Blocking">Whether a failing verify command blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds before the runner kills the process tree.</param>
/// <param name="Fix">The expanded fix command; empty when the gate declares none.</param>
/// <param name="Run">The expanded verify command.</param>
/// <param name="WorkingDirectory">Repository-relative directory the gate runs in; null means the repository root.</param>
public sealed record PlannedTaskGate(
    string Id,
    bool Blocking,
    int TimeoutSeconds,
    IReadOnlyList<string> Fix,
    IReadOnlyList<string> Run,
    string? WorkingDirectory = null)
{
    /// <summary>Whether the verify command also fails when it exits zero but prints to standard output.</summary>
    public bool FailOnOutput { get; init; }
}

/// <summary>What one agent's task gates resolve to for one changed set.</summary>
/// <param name="Gates">The gates that will start, in declaration order.</param>
/// <param name="Skipped">The gates that do not apply to this host or to this change.</param>
/// <param name="Refusals">
/// Reasons the run must not start at all. A non-empty list means no gate process is started.
/// </param>
public sealed record TaskGatePlan(
    IReadOnlyList<PlannedTaskGate> Gates,
    IReadOnlyList<SkippedTaskGate> Skipped,
    IReadOnlyList<string> Refusals);

/// <summary>The outcome of running planned task gates.</summary>
/// <param name="Report">The gate report, in the same schema as the repository-wide gates.</param>
/// <param name="FixFailures">Fix commands that did not succeed. They are warnings, not verdicts.</param>
public sealed record TaskGateRun(GateReport Report, IReadOnlyList<GateResult> FixFailures);

/// <summary>What a caller sees while task gates run: each command's output and result, and a say in skipping later gates.</summary>
public sealed class TaskGateObserver
{
    /// <summary>Receives a verify command's gate id and its standard output followed by standard error.</summary>
    public Action<string, string>? Output { get; init; }

    /// <summary>Called after a gate's verify command has run, with its result.</summary>
    public Action<PlannedTaskGate, GateResult>? After { get; init; }

    /// <summary>Called before a gate starts; a non-null reason skips it and is recorded as the gate's not-applicable reason.</summary>
    public Func<PlannedTaskGate, string?>? SkipReason { get; init; }
}
