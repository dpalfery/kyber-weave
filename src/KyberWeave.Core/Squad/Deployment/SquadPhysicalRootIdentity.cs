using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Canonical physical identity shared by planning, state binding, and transactions.</summary>
internal sealed record SquadPhysicalRootIdentity(string PhysicalPath, string Key)
{
    public static SquadPhysicalRootIdentity Resolve(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        var fullPath = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(fullPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' must be an existing directory.");
        }

        var physicalPath = SquadFileSystemPathSemantics.Canonicalize(fullPath);
        if (!Directory.Exists(physicalPath))
        {
            throw new SquadDeploymentConflictException(
                $"Squad target root '{targetRoot}' changed during physical resolution.");
        }

        var key = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(physicalPath)));
        return new SquadPhysicalRootIdentity(physicalPath, key);
    }
}
