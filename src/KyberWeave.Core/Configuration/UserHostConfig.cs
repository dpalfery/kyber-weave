namespace KyberWeave.Core.Configuration;

/// <summary>
/// Temporary contract stub for the user-level host config path and global target ledger.
/// Real behavior is intentionally left for the green implementation.
/// </summary>
public static class UserHostConfig
{
    public static string GetPath(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return Path.Combine(home, ".kyber-weave", "kyber-weave.yml");
    }

    public static string EnsureUserConfig(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return string.Empty;
    }

    public static void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(targets);
    }

    public static IReadOnlyList<string> ReadGlobalTargets(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return [];
    }
}
