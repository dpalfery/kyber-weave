namespace KyberWeave.Contracts.Repositories;

/// <summary>Resolves the command an MCP server entry launches to the file a launch would use.</summary>
public interface ILaunchCommandResolver
{
    /// <summary>
    /// The full path a launch of <paramref name="command"/> would use, or null when it names no file this resolver can find.
    /// </summary>
    /// <param name="command">A command name, or a path.</param>
    string? Resolve(string command);
}

/// <summary>Builds the launch-command resolver for this process's own environment.</summary>
public interface ILaunchCommandResolverFactory
{
    /// <summary>A resolver over this process's <c>PATH</c>, resolving a relative command against <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The directory a relative command is resolved against.</param>
    ILaunchCommandResolver FromEnvironment(string repositoryRoot);
}
