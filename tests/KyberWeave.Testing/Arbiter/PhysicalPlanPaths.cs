using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The plan-path resolver over the real disk, for the tests that stage real files and links.</summary>
public static class PhysicalPlanPaths
{
    /// <summary>Creates a resolver over the physical path and environment adapters.</summary>
    /// <returns>The resolver.</returns>
    public static PlanPathResolver Create() => new(new PhysicalPathResolver(new PhysicalFileSystem()), new ProcessEnvironment());
}
