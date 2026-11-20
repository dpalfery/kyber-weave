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

    /// <summary>Lists the plans and specifications that are Ready or In progress.</summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <returns>Repository-relative paths, sorted.</returns>
    IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config);

    /// <summary>
    /// Lists every plan and specification task list under the planning folders, whatever its status: the ones in
    /// flight and the ones in review or already closed out.
    /// </summary>
    /// <param name="repoRoot">The absolute repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    /// <returns>Repository-relative paths, sorted.</returns>
    IReadOnlyList<string> Plans(string repoRoot, KyberWeaveConfig config);
}
