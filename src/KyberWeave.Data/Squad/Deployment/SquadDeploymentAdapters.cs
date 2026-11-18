using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Loads canonical Squad source from disk.</summary>
public sealed class FileSquadSourceLoader : ISquadSourceLoader
{
    /// <inheritdoc />
    public SquadSource Load(string root) => SquadSourceLoader.Load(root);
}

/// <summary>Creates deployment transactions over the state store.</summary>
/// <param name="stateStore">The state store the transactions journal in.</param>
/// <param name="files">The file system the transactions read and write through.</param>
/// <param name="semantics">The path rules the transactions resolve deployment paths with.</param>
/// <param name="lease">The lease that serialises deployments of one root.</param>
/// <param name="ids">Names each transaction and its temporary files.</param>
public sealed class SquadTransactionFactory(
    ISquadStateStore stateStore,
    IWritableFileSystem files,
    IFileSystemPathSemantics semantics,
    ILease lease,
    IIdGenerator ids) : ISquadTransactionFactory
{
    /// <inheritdoc />
    public ISquadTransaction Create(ISquadTransactionObserver? observer) =>
        new SquadTransaction(stateStore, files, semantics, lease, ids, observer);
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

/// <summary>Validates model presets, reading the preset file to point at the offending line.</summary>
public sealed class FileSquadModelPresetValidator : ISquadModelPresetValidator
{
    /// <inheritdoc />
    public void Validate(SquadModelPreset preset, SquadModelProfiles bundled) =>
        SquadModelPresetValidator.Validate(preset, bundled);
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
