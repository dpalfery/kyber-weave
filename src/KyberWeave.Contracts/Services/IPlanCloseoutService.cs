using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;

namespace KyberWeave.Contracts.Services;

/// <summary>Closes out a finished plan: checks the closeout evidence and moves the plan to the archive.</summary>
public interface IPlanCloseoutService
{
    /// <summary>Checks the plan's closeout evidence and, with <paramref name="archive"/>, archives it without blocking a thread.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="planPath">The plan, relative to the repository root.</param>
    /// <param name="archiveDate">The archive date.</param>
    /// <param name="archive">True to move the plan; false to check and report what would happen.</param>
    /// <param name="cancellationToken">Cancels the git staging command.</param>
    Task<PlanCloseoutOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string planPath,
        DateOnly archiveDate,
        bool archive,
        CancellationToken cancellationToken = default);
}

/// <summary>Finalizes an approved Draft plan: records the approval, sets it Ready, writes its task file and commits those files.</summary>
public interface IPlanFinalizeService
{
    /// <summary>Finalizes the approved Draft plan at <paramref name="planPath"/> without blocking a thread, or refuses and changes nothing.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="planPath">The plan, relative to the repository root.</param>
    /// <param name="mode">Who approved it.</param>
    /// <param name="approval">One line saying what approved it, recorded in the Decisions table.</param>
    /// <param name="approvalDate">The approval date.</param>
    /// <param name="cancellationToken">Cancels the model picks and git commands.</param>
    Task<PlanFinalizeOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string planPath,
        PlanApprovalMode mode,
        string? approval,
        DateOnly approvalDate,
        CancellationToken cancellationToken = default);
}
