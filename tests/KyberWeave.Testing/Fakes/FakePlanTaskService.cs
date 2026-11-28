using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IPlanTaskService"/>: answers the plans and task files a test declares, touching no disk.</summary>
/// <remarks>
/// The default is a checkout with no active plans and a task file that matches nothing, which is the safe answer for a
/// test that converts a caller and does not exercise plan derivation.
/// </remarks>
internal sealed class FakePlanTaskService : IPlanTaskService
{
    /// <summary>The active plans each root reports, by root; a root not named here has none.</summary>
    public Dictionary<string, IReadOnlyList<string>> PlansByRoot { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The plans still in review each root reports, by root: those marked Review required or Blocked. A root not
    /// named here has none. Defaults to the same plans <see cref="PlansByRoot"/> names, since a test that declares
    /// one plan rarely means two.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> ReviewPlansByRoot { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The task file each artifact path reports, by artifact path; an artifact not named here yields an empty file.</summary>
    public Dictionary<string, PlanTaskFileOutcome> Outcomes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Every artifact path a caller asked for.</summary>
    public List<string> RequestedArtifacts { get; } = [];

    /// <inheritdoc />
    public PlanTaskFileOutcome Run(
        string repoRoot,
        KyberWeaveConfig config,
        string artifactPath,
        PlanTaskFileMode mode,
        IPlanTaskAgentPicker? picker = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        RequestedArtifacts.Add(artifactPath);
        return Outcomes.TryGetValue(artifactPath, out PlanTaskFileOutcome? outcome)
            ? outcome
            : new PlanTaskFileOutcome(new DiagnosticReport(), artifactPath, null, null, 0, false);
    }

    /// <inheritdoc />
    public Task<PlanTaskFileOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string artifactPath,
        PlanTaskFileMode mode,
        IPlanTaskAgentPicker? picker = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Run(repoRoot, config, artifactPath, mode, picker));

    /// <inheritdoc />
    public IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return PlansByRoot.TryGetValue(repoRoot, out IReadOnlyList<string>? plans) ? plans : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReviewPlans(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (ReviewPlansByRoot.TryGetValue(repoRoot, out IReadOnlyList<string>? plans))
            return plans;

        return ActivePlans(repoRoot, config);
    }
}
