using KyberWeave.Core.FileSystem;

namespace KyberWeave.Domain.Hooks;

/// <summary>Where the edit hook reads its configuration.</summary>
public static class HooksConfigPath
{
    /// <summary>The repository file, which may tune the built-in rules only.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The path of <c>.kyber-weave/hooks.yml</c> beneath it.</returns>
    public static string Repository(string repositoryRoot) =>
        Path.Combine(repositoryRoot, UserStatePaths.DirectoryName, "hooks.yml");

    /// <summary>The user file, which may tune the built-in rules and add commands of its own.</summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <returns>The path of <c>.kyber-weave/hooks.yml</c> beneath it.</returns>
    public static string User(string homeDirectory) =>
        UserStatePaths.HooksConfigFile(homeDirectory);
}
