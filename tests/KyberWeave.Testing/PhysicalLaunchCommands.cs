using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests;

/// <summary>The launch-command resolver over the real disk and this process's <c>PATH</c>, for the tests that stage real files.</summary>
public static class PhysicalLaunchCommands
{
    /// <summary>Creates a resolver that resolves a relative command against the working directory.</summary>
    /// <returns>The resolver.</returns>
    public static ILaunchCommandResolver FromEnvironment()
    {
        PhysicalFileSystem files = new();
        return new EnvironmentLaunchCommandResolverFactory(files, new PhysicalPathResolver(files), new ProcessEnvironment()).FromEnvironment();
    }

    /// <summary>Creates a resolver over the given search paths that resolves a relative command against the working directory.</summary>
    /// <param name="searchPaths">The directories to search, in order.</param>
    /// <returns>The resolver.</returns>
    public static LaunchCommandResolver Over(IEnumerable<string> searchPaths) =>
        Over(Directory.GetCurrentDirectory(), searchPaths);

    /// <summary>Creates a resolver over the given repository root and search paths.</summary>
    /// <param name="repositoryRoot">The directory a relative command resolves against.</param>
    /// <param name="searchPaths">The directories to search, in order.</param>
    /// <returns>The resolver.</returns>
    public static LaunchCommandResolver Over(string repositoryRoot, IEnumerable<string> searchPaths)
    {
        PhysicalFileSystem files = new();
        return new LaunchCommandResolver(repositoryRoot, searchPaths, files, new PhysicalPathResolver(files), new ProcessEnvironment());
    }
}
