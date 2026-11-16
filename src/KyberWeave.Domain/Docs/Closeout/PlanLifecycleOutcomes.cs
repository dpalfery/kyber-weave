using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>Who approved a plan for execution.</summary>
public enum PlanApprovalMode
{
    /// <summary>The conductor accepted the recommendation under autonomy <c>autonomous</c>.</summary>
    Autonomous,

    /// <summary>The owner approved the plan at the approve-and-execute gate.</summary>
    Supervised
}

/// <summary>What a plan finalize did.</summary>
/// <param name="Report">The findings, and one Info line per step taken.</param>
/// <param name="Finalized">True when this run moved the plan from Draft to Ready.</param>
/// <param name="AlreadyReady">True when the plan was already Ready with a current task file, so nothing changed.</param>
/// <param name="TaskFilePath">The task file beside the plan, repository-relative.</param>
/// <param name="ReadyQueue">One line per task, from the task file, then the tasks that may start now; null when refused.</param>
public sealed record PlanFinalizeOutcome(
    DiagnosticReport Report, bool Finalized, bool AlreadyReady, string TaskFilePath, string? ReadyQueue = null);

/// <summary>What a plan closeout did, or would do.</summary>
/// <param name="Report">The findings, and one Info line per step taken or planned.</param>
/// <param name="Archived">Whether the plan was moved to the archive. False for a preview and for a refusal.</param>
public sealed record PlanCloseoutOutcome(DiagnosticReport Report, bool Archived);
