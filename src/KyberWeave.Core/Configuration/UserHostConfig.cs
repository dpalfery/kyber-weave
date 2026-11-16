using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Configuration;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// User-level host config path and global target ledger.
/// </summary>
/// <remarks>
/// The path is <see cref="UserHostConfigPath"/> in the domain layer and the file access is
/// <see cref="FileUserHostConfigStore"/> in the data layer, behind <see cref="IUserHostConfigStore"/>. This static entry
/// point stays for the callers that have no container (the Arbiter hook host, and the CLI commands not yet moved onto
/// it), and answers over the real file system.
/// </remarks>
public static class UserHostConfig
{
    private static readonly IUserHostConfigStore Store = new FileUserHostConfigStore();

    public static string GetPath(string home) => UserHostConfigPath.Of(home);

    public static string EnsureUserConfig(string home) => Store.EnsureUserConfig(home);

    public static void WriteGlobalTargets(string home, IReadOnlyList<string> targets) =>
        Store.WriteGlobalTargets(home, targets);

    public static void RemoveGlobalTarget(string home, string target) => Store.RemoveGlobalTarget(home, target);

    public static IReadOnlyList<string> ReadGlobalTargets(string home) => Store.ReadGlobalTargets(home);
}
