using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="PlanRootResolver"/> over the real adapters for the tests whose subject is what a
/// checkout on disk holds. The class itself takes its seams, so every test that does not need a checkout injects
/// fakes instead.
/// </summary>
internal static class RealPlanRoots
{
    /// <summary>The plan root resolver over the real file system, the real path resolver and the real git facts.</summary>
    internal static PlanRootResolver Create()
    {
        PhysicalFileSystem files = new();
        return new PlanRootResolver(RealGitFacts.Create(), PhysicalPlanPaths.Create(), files, new PhysicalPathResolver(files));
    }
}
