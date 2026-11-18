using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The deployment planner over the real adapters, for the tests whose subject is what a real disk holds. A test
/// that does not need a disk builds the planner over the shared in-memory fakes instead.
/// </summary>
public static class RealSquadDeployment
{
    /// <summary>The planner over the physical file system, path resolver and process environment.</summary>
    public static SquadDeploymentPlanner Planner { get; } = new(
        new PhysicalFileSystem(),
        SquadFileSystemPathSemantics.Instance,
        new PhysicalPathResolver(new PhysicalFileSystem()));
}
