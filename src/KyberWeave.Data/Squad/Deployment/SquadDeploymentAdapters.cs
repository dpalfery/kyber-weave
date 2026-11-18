using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Preflights deployments against the tree on disk.</summary>
public sealed class FileSquadDeploymentPlanner : ISquadDeploymentPlanner
{
    /// <inheritdoc />
    public SquadDeploymentPlan CreateInstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        IReadOnlyList<SquadDegradation> degradations,
        bool adopt,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null) =>
        SquadDeploymentPlanner.CreateInstall(
            targetRoot, scope, squadLock, renderedFiles, degradations, adopt, timeProvider, globalRoots, siblingGlobalReceipts, blocks);

    /// <inheritdoc />
    public SquadDeploymentPlan CreateUpdate(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        SquadReceipt previousReceipt,
        IReadOnlyList<SquadDegradation> degradations,
        bool replaceManaged,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null) =>
        SquadDeploymentPlanner.CreateUpdate(
            targetRoot, scope, squadLock, renderedFiles, previousReceipt, degradations, replaceManaged, timeProvider, globalRoots, siblingGlobalReceipts, blocks);

    /// <inheritdoc />
    public SquadDeploymentPlan CreateUninstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadReceipt receipt,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        SquadTarget? onlyTarget = null) =>
        SquadDeploymentPlanner.CreateUninstall(targetRoot, scope, receipt, globalRoots, siblingGlobalReceipts, onlyTarget);

    /// <inheritdoc />
    public IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
        string targetRoot,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt? receipt = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null) =>
        SquadDeploymentPlanner.CollectUnmanagedCollisions(
            targetRoot, scope, renderedFiles, globalRoots, receipt, siblingGlobalReceipts);
}

/// <summary>Creates deployment transactions over the state store.</summary>
/// <param name="stateStore">The state store the transactions journal in.</param>
public sealed class SquadTransactionFactory(ISquadStateStore stateStore) : ISquadTransactionFactory
{
    /// <inheritdoc />
    public ISquadTransaction Create(ISquadTransactionObserver? observer) => new SquadTransaction(stateStore, observer);
}

/// <summary>Reads and deploys the response guidance under the user's home.</summary>
public sealed class FileSquadResponseGuidanceStore : ISquadResponseGuidanceStore
{
    /// <inheritdoc />
    public string ReadEffective(string homeDirectory, ICollection<string> warnings) =>
        SquadResponseGuidanceReader.ReadEffective(homeDirectory, warnings);

    /// <inheritdoc />
    public ISquadResponseGuidanceDeployer CreateDeployer(string homeStateRoot) => new SquadResponseGuidanceDeployer(homeStateRoot);

    /// <inheritdoc />
    public bool IsDegradableFailure(Exception exception) => SquadResponseGuidanceDeployer.IsDegradableFailure(exception);
}

/// <summary>Resolves Factory hook shadowing from the target's files.</summary>
public sealed class FileFactoryHooksShadowing : ISquadFactoryHooksShadowing
{
    /// <inheritdoc />
    public FactoryHooksShadowingOutcome Resolve(
        string targetRoot,
        IReadOnlyList<SquadTarget> targets,
        SquadArbiterWiring? arbiter,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadRenderedBlock>? blocks) =>
        FactoryHooksShadowing.Resolve(targetRoot, targets, arbiter, scope, blocks);
}

/// <summary>Probes the files of a release's extraction tree and of the checkout the binary runs from.</summary>
public sealed class PhysicalSquadFileAccess : ISquadFileAccess
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _ = Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path) => Directory.Delete(path, true);

    /// <inheritdoc />
    public string NewScratchDirectoryPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory)
    {
        DirectoryInfo baseDir = new(directory);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo entry in baseDir.EnumerateFileSystemInfos("*", options))
        {
            if (entry is DirectoryInfo directoryInfo && entry.LinkTarget is null)
                yield return new SquadDirectoryEntry(directoryInfo.Name, directoryInfo.FullName);
        }
    }

    /// <inheritdoc />
    public string? FindCheckoutSquadSourceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "products", "kyber-squad");
            if (File.Exists(Path.Combine(candidate, "squad.yml"))) return candidate;

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>Resolves canonical profiles from the checkout the binary runs from, then from the home snapshots.</summary>
/// <param name="files">Finds the checkout.</param>
/// <param name="loader">Loads the checkout's Squad source.</param>
/// <param name="store">Reads the home snapshots.</param>
public sealed class CheckoutOrSnapshotCanonicalProfileResolver(
    ISquadFileAccess files,
    ISquadSourceLoader loader,
    ISquadCanonicalProfileStore store) : ISquadCanonicalProfileResolver
{
    /// <inheritdoc />
    public SquadCanonicalProfiles? Resolve(string? preferredVersion)
    {
        SquadSource? source = TryLoadCheckoutSource();
        if (source is not null)
        {
            Dictionary<string, string> checkoutRoster = source.Agents.ToDictionary(
                agent => agent.Name,
                agent => agent.ModelProfile,
                StringComparer.OrdinalIgnoreCase);
            return new SquadCanonicalProfiles(preferredVersion ?? string.Empty, source.ModelProfiles, checkoutRoster);
        }

        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            SquadCanonicalProfiles? versioned = TryReadVersionedSnapshot(preferredVersion);
            if (versioned is not null)
                return versioned;
        }

        return store.TryReadLatest();
    }

    private SquadSource? TryLoadCheckoutSource()
    {
        try
        {
            string? root = files.FindCheckoutSquadSourceRoot();
            return root is null ? null : loader.Load(root);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Validation.SquadSourceValidationException)
        {
            return null;
        }
    }

    private SquadCanonicalProfiles? TryReadVersionedSnapshot(string version)
    {
        try
        {
            return store.TryRead(version);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Resolves targets, probing the root for each harness's marker files.</summary>
public sealed class FileSquadTargetResolver : ISquadTargetResolver
{
    /// <inheritdoc />
    public SquadTargetResolutionDecision Resolve(SquadTargetResolutionRequest request) => SquadTargetResolver.Resolve(request);
}
