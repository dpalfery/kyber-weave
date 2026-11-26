using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Configuration;

/// <summary>Where the per-user host config lives.</summary>
public static class UserHostConfigPath
{
    /// <summary>The user config file under <paramref name="home"/>.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The path of <c>.kyber-weave/kyber-weave.yml</c> beneath it.</returns>
    public static string Of(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return UserStatePaths.HostConfigFile(home);
    }
}
