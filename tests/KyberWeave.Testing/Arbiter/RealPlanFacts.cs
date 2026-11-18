using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="PlanFacts"/> over the real adapters for the tests that read a plan from a temporary
/// repository. Every other test injects the in-memory file system instead.
/// </summary>
internal static class RealPlanFacts
{
    /// <summary>The plan reader over the plan path resolver and the real file system.</summary>
    /// <param name="planPaths">The plan path resolver; the production one when null.</param>
    internal static PlanFacts Create(IPlanFileResolver? planPaths = null) =>
        new(planPaths ?? new PlanPathResolver(), new PhysicalFileSystem());
}
