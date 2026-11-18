using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Resolves the canonical physical identity of a deployment root, and the key derived from it.</summary>
public static class SquadPhysicalRootIdentities
{
    /// <summary>Resolves the physical identity of an existing deployment root.</summary>
    /// <param name="targetRoot">The directory a deployment is bound to.</param>
    /// <param name="semantics">The filesystem's canonical spelling and alias rules.</param>
    /// <returns>The canonical physical path and the registry key derived from it.</returns>
    /// <exception cref="SquadDeploymentConflictException">
    ///     The root is not an existing directory, or it changed while its physical path was resolved.
    /// </exception>
    public static SquadPhysicalRootIdentity Resolve(string targetRoot, IFileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        string physicalPath = semantics.ResolvePhysicalRoot(targetRoot);
        return new SquadPhysicalRootIdentity(physicalPath, KeyOf(physicalPath));
    }

    /// <summary>
    /// The stable key for an already-canonical physical path. Registry entries and per-project
    /// state directories are named by it, so it must be derived from the canonical spelling only.
    /// </summary>
    public static string KeyOf(string physicalPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalPath)));

    /// <summary>
    /// Canonicalizes a path whose trailing segments may not exist yet. The home directory's
    /// <c>.kyber-weave</c> tree is created by the first transaction, so it cannot require existence.
    /// </summary>
    /// <param name="path">The path to canonicalize.</param>
    /// <param name="semantics">The filesystem's canonical spelling and alias rules.</param>
    /// <returns>The canonical spelling of the path.</returns>
    public static string CanonicalizeAllowingMissing(string path, IFileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return semantics.Canonicalize(path);
    }
}
