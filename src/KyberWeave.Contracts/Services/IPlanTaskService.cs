using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Contracts.Services;

/// <summary>Derives, checks and writes the task file beside a plan or specification task list, and finds the plans in flight.</summary>
public interface IPlanTaskService
{
    /// <summary>Derives the task file for one plan or specification task list and writes, checks or previews it.</summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, with its ontology applied.</param>
    /// <param name="artifactPath">The plan, or a specification's <c>tasks.md</c>, repository-relative.</param>
    /// <param name="mode">What to do with the derived file.</param>
    /// <param name="picker">Chooses each unassigned task's agent on a write; null when no decision model is configured.</param>
    /// <returns>The findings and the derived file.</returns>
    PlanTaskFileOutcome Run(string repoRoot, KyberWeaveConfig config, string artifactPath, PlanTaskFileMode mode, IPlanTaskAgentPicker? picker = null);

    /// <summary>
    /// Derives the task file for one plan or specification task list and writes, checks or previews it without
    /// blocking a thread: on a write, each unassigned task's agent is asked of the model asynchronously.
    /// </summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, with its ontology applied.</param>
    /// <param name="artifactPath">The plan, or a specification's <c>tasks.md</c>, repository-relative.</param>
    /// <param name="mode">What to do with the derived file.</param>
    /// <param name="picker">Chooses each unassigned task's agent on a write; null when no decision model is configured.</param>
    /// <param name="cancellationToken">Cancels the model calls a write makes.</param>
    /// <returns>The findings and the derived file.</returns>
    Task<PlanTaskFileOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string artifactPath,
        PlanTaskFileMode mode,
        IPlanTaskAgentPicker? picker = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the plans and specifications that are Ready or In progress.</summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <returns>Repository-relative paths, sorted.</returns>
    IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config);

    /// <summary>
    /// Lists the plans and specifications under the planning folders that are still in review: those marked
    /// <c>Review required</c> or <c>Blocked</c>. A plan in flight is found by <see cref="ActivePlans"/> instead,
    /// and a closed-out plan is left out.
    /// </summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <returns>Repository-relative paths, sorted.</returns>
    IReadOnlyList<string> ReviewPlans(string repoRoot, KyberWeaveConfig config);
}
