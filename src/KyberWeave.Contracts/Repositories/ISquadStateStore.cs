using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads and writes the deployment state Kyber-Squad keeps beside each target root and in the user's home.</summary>
public interface ISquadStateStore
{
    /// <summary>Gets the user paths the state is anchored to.</summary>
    ISquadUserPaths UserPaths { get; }

    /// <summary>Resolves the directory holding a target root's state.</summary>
    string ResolveStateDirectory(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the home-level state directory.</summary>
    string ResolveHomeStateDirectory();

    /// <summary>Resolves the root of the files the user's home owns, such as the response guidance.</summary>
    string ResolveHomeOwnedFilesRoot();

    /// <summary>Lists the registered projects, rebuilding the registry when it is stale.</summary>
    IReadOnlyList<SquadProjectRegistryEntry> ListProjects();

    /// <summary>Finds a registered project by its root.</summary>
    SquadProjectRegistryEntry? FindProject(string root);

    /// <summary>Says whether the registry itself holds an entry for a project root, whether or not a receipt could rebuild one.</summary>
    /// <param name="root">The project root.</param>
    /// <returns>True when the registry file names the root.</returns>
    bool IsRegistered(string root);

    /// <summary>Lists the registered projects without rebuilding the registry.</summary>
    IReadOnlyList<SquadProjectRegistryEntry> ListProjectsWithoutRebuild();

    /// <summary>Registers a project and the targets installed in it.</summary>
    void RegisterProject(string root, IReadOnlyList<string> targets, DateTimeOffset now);

    /// <summary>Removes a project from the registry.</summary>
    bool UnregisterProject(string root);

    /// <summary>Serializes a lock file.</summary>
    string SerializeLock(SquadLock squadLock);

    /// <summary>Parses a lock file.</summary>
    SquadLock DeserializeLock(string yaml);

    /// <summary>Serializes a receipt.</summary>
    string SerializeReceipt(SquadReceipt receipt);

    /// <summary>Parses a receipt.</summary>
    SquadReceipt DeserializeReceipt(string json);

    /// <summary>Reads a target root's receipt, or null when none is recorded.</summary>
    SquadReceipt? ReadReceipt(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Reads a target root's lock, or null when none is recorded.</summary>
    SquadLock? ReadLock(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the path of a target root's lock file.</summary>
    string ResolveLockPath(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the path of a target root's receipt file.</summary>
    string ResolveReceiptPath(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the directory a deployment transaction journals in.</summary>
    string ResolveTransactionDirectory(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the scratch directory of a deployment transaction.</summary>
    string ResolveTransactionWorkDirectory(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the scratch directory used while writing one file.</summary>
    string ResolveFileWorkDirectory(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the root whose state authorises a deployment.</summary>
    string ResolveStateAuthorityRoot(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the identity a deployment lease is taken under.</summary>
    string ResolveLeaseIdentity(string targetRoot, SquadDeploymentScope scope);

    /// <summary>Resolves the home root.</summary>
    string ResolveHomeRoot();

    /// <summary>Resolves the directory state may be created up to.</summary>
    string ResolveHomeCreationBoundary();
}
