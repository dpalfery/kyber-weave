using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Moves Squad lock and receipt state from its legacy per-user locations into the home-level layout, and
/// imports individual projects into the registry.
/// </summary>
/// <remarks>
/// Every rule here copies rather than moves. The legacy files stay byte-identical, because an older CLI that
/// still reads the per-user location must keep working during a rollback. Nothing is overwritten: a home
/// receipt is the authority once it exists, and a migration that finds one is a no-op. Writes go through a
/// temporary file and a rename, so a reader sees the prior file or the new one, never a partial one.
/// </remarks>
public sealed class SquadStateMigration
{
    private const string ReceiptFileName = "squad.receipt.json";
    private const string LockFileName = "squad.lock.yml";
    private const string TargetRootProperty = "targetRoot";
    private const string InRepoStateDirectoryName = ".kyber-weave";
    private const string LegacyRootsRelativeDirectory = "KyberWeave/squad/roots";

    private const string HomeAuthorityReason =
        "The home global receipt is authoritative, so this legacy root is not read.";

    private const string HomeAuthorityGuidance =
        "It is left in place for an older CLI. Remove it once no older CLI needs it.";

    private const string AmbiguousGuidance =
        "Keep exactly one legacy global receipt. Move the others aside after verifying them, then run the migration " +
        "again. Nothing was copied, so the home store is unchanged.";

    private static readonly JsonSerializerOptions RewriteOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        NewLine = "\n"
    };

    private readonly SquadStateStore _stateStore;
    private readonly ISquadUserPaths _userPaths;

    /// <summary>Creates a migration over the given state store and user paths.</summary>
    public SquadStateMigration(SquadStateStore stateStore, ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(userPaths);
        _stateStore = stateStore;
        _userPaths = userPaths;
    }

    /// <summary>Migrates legacy global state into the home-level layout.</summary>
    /// <param name="invokingCwd">
    /// The working directory of the invoking command, or <see langword="null"/> when unknown. It
    /// is used to recognise the legacy root that belongs to the invoking project.
    /// </param>
    /// <param name="dryRun">
    /// When <see langword="true"/>, the report says what the next non-dry-run invocation will move, and nothing
    /// is written: no directory, no copy, no registry change.
    /// </param>
    /// <remarks>
    /// Only the legacy <c>roots/&lt;key&gt;</c> directories that hold at least one entry count. An empty one is
    /// not a receipt, so it cannot make a single real receipt ambiguous. When exactly one non-empty legacy
    /// directory exists, it is copied to the home global store. When several exist, none is copied and each is
    /// reported, because copying any one of them could hand the home store the wrong ownership boundary.
    /// </remarks>
    public SquadMigrationReport MigrateGlobal(string? invokingCwd, bool dryRun = false)
    {
        List<string> migrated = [];
        List<SquadUnmigratedLegacyRoot> unmigrated = [];

        string homeGlobalDirectory = _stateStore.ResolveStateDirectory(
            _stateStore.ResolveHomeRoot(),
            SquadDeploymentScope.Global);
        if (!File.Exists(Path.Combine(homeGlobalDirectory, ReceiptFileName)))
        {
            List<string> legacyDirectories = FindNonEmptyLegacyRootDirectories();
            if (legacyDirectories.Count > 1)
            {
                foreach (string legacyDirectory in legacyDirectories)
                {
                    unmigrated.Add(new SquadUnmigratedLegacyRoot(
                        legacyDirectory,
                        "More than one legacy global receipt exists, so none can be chosen safely.",
                        AmbiguousGuidance));
                }
            }
            else if (legacyDirectories.Count == 1)
            {
                SquadUnmigratedLegacyRoot? blocked = PlanGlobalCopy(
                    legacyDirectories[0],
                    invokingCwd,
                    homeGlobalDirectory,
                    out GlobalCopyPlan? plan);
                if (blocked is not null)
                {
                    unmigrated.Add(blocked);
                }
                else if (plan is not null)
                {
                    if (dryRun)
                    {
                        migrated.Add(
                            $"legacy state at '{plan.LegacyDirectory}' will be migrated on the next non-dry-run invocation.");
                    }
                    else
                    {
                        ApplyGlobalCopy(plan);
                        migrated.Add(
                            $"Copied the legacy global receipt and lock from '{plan.LegacyDirectory}' to " +
                            $"'{plan.HomeDirectory}'. The legacy files were left in place.");
                    }
                }
            }
        }
        else
        {
            // The home receipt is the authority, so nothing is copied. Each legacy root that remains is still
            // reported: doctor must be able to say it is on disk and is no longer read.
            foreach (string legacyDirectory in FindNonEmptyLegacyRootDirectories())
            {
                unmigrated.Add(new SquadUnmigratedLegacyRoot(
                    legacyDirectory,
                    HomeAuthorityReason,
                    HomeAuthorityGuidance));
            }
        }

        return new SquadMigrationReport(migrated, unmigrated, [], []);
    }

    /// <summary>Imports one project's legacy state into the home-level registry.</summary>
    /// <param name="root">The project root whose legacy state should be imported.</param>
    /// <param name="dryRun">
    /// When <see langword="true"/>, the report says what the next non-dry-run invocation will import, and nothing
    /// is written.
    /// </param>
    /// <remarks>
    /// Imports only when the project has no home receipt and its repository carries
    /// <c>.kyber-weave/squad.receipt.json</c>. The home receipt is authoritative once it exists, so an in-repo
    /// receipt that disagrees is ignored rather than allowed to roll the home record back. The in-repo files are
    /// the project's own record until its operator removes them, so they are left byte-identical.
    /// </remarks>
    public SquadMigrationReport ImportProject(string root, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        List<string> imported = [];
        List<string> warnings = [];
        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(root);
        string projectDirectory = _stateStore.ResolveStateDirectory(identity.PhysicalPath, SquadDeploymentScope.Project);
        string inRepoDirectory = Path.Combine(identity.PhysicalPath, InRepoStateDirectoryName);
        string inRepoReceiptPath = Path.Combine(inRepoDirectory, ReceiptFileName);
        string inRepoLockPath = Path.Combine(inRepoDirectory, LockFileName);

        bool homeReceiptExists = File.Exists(Path.Combine(projectDirectory, ReceiptFileName));
        if (!homeReceiptExists && File.Exists(inRepoReceiptPath))
        {
            if (!File.Exists(inRepoLockPath))
            {
                warnings.Add(
                    $"The in-repo receipt at '{inRepoReceiptPath}' has no lock beside it, so it was not imported.");
            }
            else if (File.Exists(Path.Combine(projectDirectory, LockFileName)))
            {
                warnings.Add(
                    $"A home lock already exists for '{identity.PhysicalPath}' without a receipt, so nothing was " +
                    "imported. Inspect and remove that lock, then run the import again.");
            }
            else
            {
                ImportInRepoState(
                    identity,
                    projectDirectory,
                    inRepoReceiptPath,
                    inRepoLockPath,
                    dryRun,
                    imported,
                    warnings);
            }
        }

        return new SquadMigrationReport([], [], imported, warnings);
    }

    private void ImportInRepoState(
        SquadPhysicalRootIdentity identity,
        string projectDirectory,
        string inRepoReceiptPath,
        string inRepoLockPath,
        bool dryRun,
        List<string> imported,
        List<string> warnings)
    {
        string receiptText = File.ReadAllText(inRepoReceiptPath, Encoding.UTF8);
        SquadReceipt receipt;
        try
        {
            receipt = _stateStore.DeserializeReceipt(receiptText);
            _stateStore.DeserializeLock(File.ReadAllText(inRepoLockPath, Encoding.UTF8));
        }
        catch (InvalidDataException exception)
        {
            warnings.Add(
                $"The in-repo state at '{inRepoReceiptPath}' is unreadable ({exception.Message}), so it was not imported.");
            return;
        }

        if (receipt.Scope != SquadDeploymentScope.Project)
        {
            warnings.Add(
                $"The in-repo receipt at '{inRepoReceiptPath}' is not a project receipt, so it was not imported.");
            return;
        }

        string rewritten = RewriteTargetRoot(receiptText, identity.PhysicalPath);
        if (dryRun)
        {
            imported.Add(
                $"Dry run: in-repo state at '{Path.GetDirectoryName(inRepoReceiptPath)}' will be imported into " +
                $"'{projectDirectory}' on the next non-dry-run invocation.");
            return;
        }

        WriteAtomically(Path.Combine(projectDirectory, LockFileName), File.ReadAllBytes(inRepoLockPath));
        WriteAtomically(Path.Combine(projectDirectory, ReceiptFileName), new UTF8Encoding(false).GetBytes(rewritten));

        // The target roots a project deploys to come from the receipt itself, so the registry entry
        // describes the same files the receipt owns.
        IReadOnlyList<string> targets = receipt.Files
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _stateStore.RegisterProject(identity.PhysicalPath, targets, DateTimeOffset.UtcNow);
        imported.Add(identity.PhysicalPath);
    }

    /// <summary>
    /// Validates the legacy receipt and lock and resolves the root the copy will be anchored on. It writes
    /// nothing, so a dry run and a real run take the same decision.
    /// </summary>
    private SquadUnmigratedLegacyRoot? PlanGlobalCopy(
        string legacyDirectory,
        string? invokingCwd,
        string homeGlobalDirectory,
        out GlobalCopyPlan? plan)
    {
        plan = null;
        string receiptPath = Path.Combine(legacyDirectory, ReceiptFileName);
        string lockPath = Path.Combine(legacyDirectory, LockFileName);
        if (!File.Exists(receiptPath) || !File.Exists(lockPath))
        {
            return new SquadUnmigratedLegacyRoot(
                legacyDirectory,
                "The legacy directory does not hold both a receipt and a lock.",
                "Restore the missing file beside the other, or remove the directory once its state is verified, " +
                "then run the migration again. Nothing was moved.");
        }

        string receiptText = File.ReadAllText(receiptPath, Encoding.UTF8);
        SquadReceipt receipt;
        try
        {
            receipt = _stateStore.DeserializeReceipt(receiptText);
            _stateStore.DeserializeLock(File.ReadAllText(lockPath, Encoding.UTF8));
        }
        catch (InvalidDataException exception)
        {
            return new SquadUnmigratedLegacyRoot(
                legacyDirectory,
                $"The legacy state is unreadable: {exception.Message}",
                "Inspect the receipt and lock, repair or remove them, then run the migration again. Nothing was moved.");
        }

        if (receipt.Scope != SquadDeploymentScope.Global)
        {
            return new SquadUnmigratedLegacyRoot(
                legacyDirectory,
                "The legacy receipt is project-scoped, so it is not a global receipt.",
                "Import it with the project import instead, from the project root it belongs to. Nothing was moved.");
        }

        string? targetRoot = receipt.Layout == SquadReceiptLayout.PerTargetRoots
            ? _stateStore.ResolveHomeRoot()
            : ResolveSingleRootBinding(receipt, Path.GetFileName(legacyDirectory), invokingCwd);
        if (targetRoot is null)
        {
            return new SquadUnmigratedLegacyRoot(
                legacyDirectory,
                "The single-root receipt names no root that can be resolved. Its target root is not an existing " +
                "directory, no registry entry carries its key, and neither the home nor the invoking directory matches it.",
                "Run the migration from the project root this deployment was installed into, so its key can be " +
                "matched, or register that root and run it again. Nothing was moved.");
        }

        if (File.Exists(Path.Combine(homeGlobalDirectory, LockFileName)))
        {
            return new SquadUnmigratedLegacyRoot(
                legacyDirectory,
                "A home global lock already exists without a home global receipt.",
                "Inspect the home global lock, then remove it or restore its receipt, and run the migration again. " +
                "Nothing was overwritten.");
        }

        plan = new GlobalCopyPlan(
            legacyDirectory,
            homeGlobalDirectory,
            File.ReadAllBytes(lockPath),
            RewriteTargetRoot(receiptText, targetRoot));
        return null;
    }

    /// <summary>
    /// Resolves the root a single-root receipt is bound to, trying the strongest evidence first. An absolute
    /// target root names its root directly. A registry entry whose key is the legacy directory's key is the
    /// home's own record of that root. A home or invoking directory whose key matches is the operator vouching
    /// for it. When none of them resolves, the caller reports the receipt rather than guessing a root.
    /// </summary>
    private string? ResolveSingleRootBinding(SquadReceipt receipt, string legacyKey, string? invokingCwd)
    {
        if (Path.IsPathFullyQualified(receipt.TargetRoot) &&
            TryResolveExistingRoot(receipt.TargetRoot, out string? recorded))
        {
            return recorded;
        }

        foreach (SquadProjectRegistryEntry entry in _stateStore.ListProjectsWithoutRebuild())
        {
            if (string.Equals(entry.RootKey, legacyKey, StringComparison.Ordinal) &&
                TryResolveExistingRoot(entry.Path, out string? registered) &&
                KeyMatches(registered, legacyKey))
            {
                return registered;
            }
        }

        string home = _stateStore.ResolveHomeRoot();
        if (KeyMatches(home, legacyKey))
            return home;

        if (invokingCwd is not null &&
            TryResolveExistingRoot(invokingCwd, out string? invoking) &&
            KeyMatches(invoking, legacyKey))
        {
            return invoking;
        }

        return null;
    }

    private void ApplyGlobalCopy(GlobalCopyPlan plan)
    {
        WriteAtomically(Path.Combine(plan.HomeDirectory, LockFileName), plan.LockBytes);
        WriteAtomically(Path.Combine(plan.HomeDirectory, ReceiptFileName), new UTF8Encoding(false).GetBytes(plan.ReceiptText));
    }

    /// <summary>
    /// Lists the legacy <c>roots/&lt;key&gt;</c> directories that hold at least one entry. Symbolic links are
    /// skipped and never followed, because the walk runs over a directory a user can influence.
    /// </summary>
    private List<string> FindNonEmptyLegacyRootDirectories()
    {
        string rootsDirectory = Path.Combine(
            _userPaths.ApplicationDataDirectory,
            LegacyRootsRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(rootsDirectory))
            return [];

        return new DirectoryInfo(rootsDirectory)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 })
            .OfType<DirectoryInfo>()
            .Where(entry => entry.LinkTarget is null &&
                entry.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).Any())
            .Select(entry => entry.FullName)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Sets the receipt's <c>targetRoot</c> and leaves every other field as the source wrote it, so a copied
    /// receipt keeps its schema version. The result is validated before it can be written.
    /// </summary>
    private string RewriteTargetRoot(string receiptText, string targetRoot)
    {
        JsonObject document = JsonNode.Parse(receiptText)?.AsObject()
            ?? throw new InvalidDataException("Squad receipt must be a JSON object.");
        document[TargetRootProperty] = targetRoot;
        string rewritten = document.ToJsonString(RewriteOptions) + "\n";
        _stateStore.DeserializeReceipt(rewritten);
        return rewritten;
    }

    private static bool TryResolveExistingRoot(string path, [NotNullWhen(true)] out string? physical)
    {
        try
        {
            physical = SquadPhysicalRootIdentity.Resolve(path).PhysicalPath;
            return true;
        }
        catch (Exception exception) when (
            exception is SquadDeploymentConflictException or ArgumentException or IOException or NotSupportedException)
        {
            physical = null;
            return false;
        }
    }

    private static bool KeyMatches(string physicalPath, string key) =>
        string.Equals(SquadPhysicalRootIdentity.KeyOf(physicalPath), key, StringComparison.Ordinal);

    /// <summary>
    /// Writes the bytes to a temporary file beside the target and renames it into place. The target is replaced
    /// only by a complete file, and the directory is created when the home has never held Squad state.
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Squad state path '{path}' has no directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: SquadTransaction.SyncToDisk);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>A validated global copy, ready to be applied or reported as pending.</summary>
    private sealed record GlobalCopyPlan(
        string LegacyDirectory,
        string HomeDirectory,
        byte[] LockBytes,
        string ReceiptText);
}
