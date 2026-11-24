using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Services;

/// <summary>Runs the deterministic gates a host declares and normalizes what they reported.</summary>
/// <remarks>This is the only place a reviewer executes anything; every command comes from the host's configuration, already split into argv.</remarks>
public interface IGateRunService
{
    /// <summary>Runs every declared gate and returns the normalized report.</summary>
    /// <param name="config">The host's review configuration.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="stopOnBlockingFailure">Whether to skip the remaining gates once a blocking one fails.</param>
    /// <param name="changedPaths">Repository-relative paths the change touches, or null when no base was given; null runs every gate.</param>
    /// <param name="baseRef">The commit the change was measured against, recorded on the report.</param>
    /// <param name="deadline">The wall-clock budget for the whole run, or null for none.</param>
    GateReport Run(
        ReviewConfig config,
        string workingDirectory,
        bool stopOnBlockingFailure = false,
        IReadOnlyCollection<string>? changedPaths = null,
        string? baseRef = null,
        TimeSpan? deadline = null);

    /// <summary>Lists the paths the gates that would start require and that do not exist.</summary>
    /// <remarks>
    /// "Would start" is decided by the same <c>applies-when</c> rule and the same path resolution <see cref="Run"/> uses,
    /// so a preflight on this list cannot disagree with the run.
    /// </remarks>
    /// <param name="config">The host's review configuration.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="changedPaths">Repository-relative paths the change touches, or null when no base was given; null admits every gate.</param>
    /// <returns>One entry per missing path, in gate declaration order; empty when every required path exists.</returns>
    IReadOnlyList<MissingGateRequirement> MissingRequirements(
        ReviewConfig config,
        string workingDirectory,
        IReadOnlyCollection<string>? changedPaths);

    /// <summary>Runs one agent's planned task gates in the order they were declared.</summary>
    /// <param name="gates">The gates that will start, already expanded by the task-gate selection.</param>
    /// <param name="workingDirectory">The repository root the gates run against.</param>
    /// <param name="fix">Whether to run each gate's fix command before its verify command.</param>
    /// <param name="stopOnBlockingFailure">Whether to skip the remaining gates once a blocking one fails.</param>
    /// <param name="observer">Sees each verify command's output and result, and may skip a later gate.</param>
    TaskGateRun RunTaskGates(
        IReadOnlyList<PlannedTaskGate> gates,
        string workingDirectory,
        bool fix,
        bool stopOnBlockingFailure = false,
        TaskGateObserver? observer = null);
}
