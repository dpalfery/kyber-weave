using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Canonical physical identity shared by planning, state binding, and transactions.</summary>
internal sealed record SquadPhysicalRootIdentity(string PhysicalPath, string Key)
{
    public static SquadPhysicalRootIdentity Resolve(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string fullPath = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(fullPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' must be an existing directory.");
        }

        string physicalPath = SquadFileSystemPathSemantics.Canonicalize(fullPath);
        if (!Directory.Exists(physicalPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' changed during physical resolution.");
        }

        return new SquadPhysicalRootIdentity(physicalPath, KeyOf(physicalPath));
    }

    /// <summary>
    /// The stable key for an already-canonical physical path. Registry entries and per-project
    /// state directories are named by it, so it must be derived from the canonical spelling only.
    /// </summary>
    internal static string KeyOf(string physicalPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalPath)));

    /// <summary>
    /// Canonicalizes a path whose trailing segments may not exist yet. The home directory's
    /// <c>.kyber-weave</c> tree is created by the first transaction, so it cannot require existence.
    /// </summary>
    internal static string CanonicalizeAllowingMissing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return SquadFileSystemPathSemantics.Canonicalize(path);
    }
}
