using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Applies a preflighted Squad plan through a recoverable intent, staging, and backup protocol.
/// </summary>
/// <remarks>
///     Physical file paths are resolved per-file rather than using a single root for all operations.
///     In Project scope, all files share the project root. In Global scope, each file's target
///     (Claude, Codex, Cursor, etc.) has its own root, so a shared relative path like `skills/x.md`
///     can map to different physical locations depending on the file's target. That resolved root is
///     captured once, at prepare time, into the private <c>IntentFile.PhysicalRoot</c> field of the
///     intent journal — every later step (staging, backup, apply, restore, and claim resolution,
///     including standalone <see cref="Recover" /> with no plan in hand) reads it back verbatim
///     instead of re-deriving it, so recovery never depends on a live
///     <see cref="ISquadGlobalRootResolver" /> or a possibly-changed environment. Directory cleanup
///     during restore operations must stop at each file's own resolved root and never walk above it.
/// </remarks>
public sealed class SquadTransaction : ISquadTransaction
{
    private const string IntentFileName = "intent.json";
    private const string IntentSchema = "kyber-squad.transaction/v1";
    private static readonly JsonSerializerOptions IntentJsonOptions = CreateIntentJsonOptions();

    private static readonly TimeSpan RegistryLeaseTimeout = TimeSpan.FromSeconds(10);

    private const string LeaseBusyMessage =
        "A Squad transaction is active for this deployment root. Wait for it to finish before retrying.";

    private readonly IWritableFileSystem _files;
    private readonly IIdGenerator _ids;
    private readonly ILease _lease;
    private readonly ISquadTransactionObserver? _observer;
    private readonly IFileSystemPathSemantics _semantics;
    private readonly ISquadStateStore _stateStore;
    private readonly List<string> _warnings = [];

    /// <summary>Creates a transaction over the seams it reads and writes through.</summary>
    /// <param name="stateStore">Resolves where the journal, lock and receipt live.</param>
    /// <param name="files">The file system every read, write and move goes through.</param>
    /// <param name="semantics">The canonical spellings and containment rules for deployment paths.</param>
    /// <param name="lease">Serialises deployments of one root, across processes.</param>
    /// <param name="ids">Names a transaction and its temporary files.</param>
    /// <param name="observer">Told of each durable step; none when null.</param>
    public SquadTransaction(
        ISquadStateStore stateStore,
        IWritableFileSystem files,
        IFileSystemPathSemantics semantics,
        ILease lease,
        IIdGenerator ids,
        ISquadTransactionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(ids);
        _stateStore = stateStore;
        _files = files;
        _semantics = semantics;
        _lease = lease;
        _ids = ids;
        _observer = observer;
    }

    private static bool _defaultSyncToDisk = true;
    private static readonly AsyncLocal<bool?> SyncToDiskOverride = new();

    /// <summary>
    ///     Whether journal, restore, and artifact writes are forced to stable storage. Always
    ///     <see langword="true" /> in production; the test assembly turns it off because a full
    ///     device flush costs milliseconds per file on macOS and the tests never power-cycle.
    /// </summary>
    public static bool SyncToDisk
    {
        get => SyncToDiskOverride.Value ?? _defaultSyncToDisk;
        set => _defaultSyncToDisk = value;
    }

    /// <summary>
    ///     Overrides <see cref="SyncToDisk" /> for the duration of the returned scope within the current
    ///     asynchronous execution flow.
    /// </summary>
    /// <param name="enabled">Whether disk synchronization should be enabled in the active scope.</param>
    /// <returns>An <see cref="IDisposable" /> that restores the prior sync override on disposal.</returns>
    public static IDisposable UseSyncToDiskScope(bool enabled)
    {
        bool? prior = SyncToDiskOverride.Value;
        SyncToDiskOverride.Value = enabled;
        return new SyncToDiskScope(prior);
    }

    /// <summary>
    ///     Warnings raised after a transaction has committed. A committed deployment is never rolled back
    ///     for a warning, so a caller must surface these rather than read the outcome as silent success.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Executes a plan and rolls it back before propagating any caught failure.</summary>
    public void Execute(SquadDeploymentPlan plan)
    {
        Execute(plan, underLease: null);
    }

    /// <summary>Executes a plan, running <paramref name="underLease" /> once the deployment lease is held.</summary>
    /// <param name="plan">The preflighted plan to commit.</param>
    /// <param name="underLease">
    ///     Work that must not interleave with another deployment of the same root. It runs after the lease
    ///     is taken and the plan's preconditions are re-checked against the live tree, and before the
    ///     receipt is captured, so whatever it attaches to the plan is what this transaction commits.
    /// </param>
    /// <remarks>
    ///     The callback's own writes sit outside this transaction's rollback. It is for work the caller
    ///     can classify and verify itself, such as home-owned files a plan cannot carry as file mutations.
    /// </remarks>
    public void Execute(SquadDeploymentPlan plan, Action<SquadDeploymentPlan>? underLease)
    {
        ArgumentNullException.ThrowIfNull(plan);

        SquadPhysicalRootIdentity currentIdentity = SquadPhysicalRootIdentity.Resolve(plan.TargetRoot, _semantics);
        if (!string.Equals(currentIdentity.Key, plan.PhysicalRootKey, StringComparison.Ordinal))
            throw new SquadDeploymentConflictException(
                "Squad target root changed physical identity after preflight. Recreate the plan before writing.");

        using TransactionLease lease =
            AcquireLease(_stateStore.ResolveLeaseIdentity(plan.PhysicalRootPath, plan.Scope));
        string root = plan.PhysicalRootPath;
        string journalDirectory = Physical(_stateStore.ResolveTransactionDirectory(root, plan.Scope));
        string workDirectory = Physical(_stateStore.ResolveFileWorkDirectory(root, plan.Scope));
        bool sameTransactionDirectory = _semantics.AreSame(
            journalDirectory,
            workDirectory);
        string intentPath = Path.Combine(journalDirectory, IntentFileName);
        bool workExisted = _files.DirectoryExists(workDirectory);
        string stateAuthorityRoot = Physical(_stateStore.ResolveStateAuthorityRoot(root, plan.Scope));
        string lockPath = PhysicalLeaf(_stateStore.ResolveLockPath(root, plan.Scope));
        string receiptPath = PhysicalLeaf(_stateStore.ResolveReceiptPath(root, plan.Scope));
        string stateDirectory = Path.GetDirectoryName(lockPath)!;
        plan.AnchorReceipt(plan.Scope == SquadDeploymentScope.Global
            ? _stateStore.ResolveHomeRoot()
            : root);
        SquadReceipt receipt = plan.Receipt;

        // Every directory this transaction may create is captured before anything is written, so a
        // failed transaction removes exactly what it created. The state directory lives under the
        // home for both scopes, so it is captured against the home rather than the journal's authority.
        IReadOnlyList<string> journalDirectoriesCreated = CaptureMissingDirectories(
            journalDirectory,
            stateAuthorityRoot);
        IReadOnlyList<string> workDirectoriesCreated = sameTransactionDirectory
            ? Array.Empty<string>()
            : CaptureMissingDirectories(workDirectory, root);
        IReadOnlyList<string> stateDirectoriesCreated = CaptureMissingDirectories(
            stateDirectory,
            _stateStore.ResolveHomeCreationBoundary());
        bool ownsNewTransaction = false;
        bool retainTransaction = false;
        try
        {
            if (_files.FileExists(intentPath) || (!sameTransactionDirectory && workExisted))
                throw new InvalidOperationException(
                    "An unfinished Squad transaction exists. Recover it before starting another deployment.");

            // Directories are created only once this transaction owns the journal, so a refused start
            // never leaves a directory behind that no intent accounts for.
            ownsNewTransaction = true;
            _files.CreateDirectory(journalDirectory);
            ValidatePreconditions(plan);

            // After the preconditions so a refused plan writes nothing, and before the intent captures the
            // receipt so the callback's attachments are the ones committed. The lease is held throughout:
            // a second deployment of this root is refused at AcquireLease and never reaches this point.
            if (underLease is not null)
            {
                underLease(plan);
                receipt = plan.Receipt;
            }

            _files.CreateDirectory(workDirectory);
            _files.CreateDirectory(stateDirectory);

            // A global journal sits beneath the home, not the target root, so when it also holds the staged
            // work its directories are recorded against the state and home authorities alone. Recording them
            // as target work would relate them to a root they are not beneath. A project journal sits beneath
            // its root, so it keeps its historical target-work record.
            IntentDocument intent = CaptureIntent(
                plan,
                receipt,
                lockPath,
                receiptPath,
                sameTransactionDirectory
                    ? plan.Scope == SquadDeploymentScope.Project
                        ? journalDirectoriesCreated
                        : Array.Empty<string>()
                    : workDirectoriesCreated,
                journalDirectoriesCreated,
                stateDirectoriesCreated,
                stateAuthorityRoot);

            // The callback and the capture ran after the first check, so a file that appeared in between would be
            // captured as an original and then overwritten. Checking again, with nothing written yet, refuses it.
            ValidatePreconditions(plan);
            int sequence = 0;
            int checkpointSequence = 0;
            try
            {
                WriteIntent(intentPath, intent, false);
                lease.HoldJournal(intentPath);
                Notify(ref sequence, SquadTransactionStepKind.IntentWritten);

                List<IntentArtifact> artifacts = new();
                IReadOnlyList<string> stagedPaths = StageFiles(
                    plan,
                    intent.TransactionId,
                    workDirectory,
                    artifacts);
                IReadOnlyList<string> backedUpPaths = BackupFiles(plan, intent, workDirectory, artifacts);
                BackupStateFiles(
                    intent,
                    workDirectory,
                    lockPath,
                    receiptPath,
                    artifacts);
                PrepareStateFiles(
                    plan,
                    receipt,
                    intent.TransactionId,
                    journalDirectory,
                    artifacts);

                intent = intent with
                {
                    Phase = TransactionPhase.Prepared,
                    Artifacts = artifacts.ToArray()
                };
                lease.ReleaseJournal();
                WriteIntent(intentPath, intent, true);
                lease.HoldJournal(intentPath);

                foreach (string relativePath in stagedPaths)
                    Notify(
                        ref sequence,
                        SquadTransactionStepKind.FileStaged,
                        relativePath);

                if (backedUpPaths.Count == 0)
                    Notify(ref sequence, SquadTransactionStepKind.FileBackedUp);
                else
                    foreach (string relativePath in backedUpPaths)
                        Notify(
                            ref sequence,
                            SquadTransactionStepKind.FileBackedUp,
                            relativePath);

                NotifyCheckpoint(
                    ref checkpointSequence,
                    _observer,
                    SquadTransactionCheckpointKind.Prepared);

                ValidateSemanticArtifactSet(intent, journalDirectory, workDirectory);
                VerifyArtifacts(
                    intent.TransactionId,
                    intent.Artifacts,
                    intent.ActiveTransitions,
                    journalDirectory,
                    workDirectory);

                ApplyFiles(
                    plan,
                    workDirectory,
                    intentPath,
                    lease,
                    ref intent,
                    ref sequence,
                    ref checkpointSequence);
                ApplyLock(
                    plan,
                    journalDirectory,
                    workDirectory,
                    intentPath,
                    lockPath,
                    lease,
                    ref intent,
                    ref sequence,
                    ref checkpointSequence);
                ApplyReceipt(
                    plan,
                    journalDirectory,
                    workDirectory,
                    intentPath,
                    receiptPath,
                    lease,
                    ref intent,
                    ref sequence,
                    ref checkpointSequence);
            }
            catch (Exception failure)
            {
                IReadOnlyList<string> conflicts;
                try
                {
                    ResolveActiveClaims(
                        intent,
                        journalDirectory,
                        workDirectory,
                        lockPath,
                        receiptPath);
                    conflicts = RestoreIntent(
                        intent,
                        workDirectory,
                        lockPath,
                        receiptPath);
                }
                catch (Exception)
                {
                    // Whatever stopped the rollback, the journal and the backups are the only way back: keeping them
                    // lets the next recovery finish what this one could not.
                    retainTransaction = true;
                    throw;
                }

                if (failure is InvalidDataException or LeafClaimConflictException)
                {
                    retainTransaction = true;
                    throw;
                }

                if (conflicts.Count > 0)
                {
                    retainTransaction = true;
                    throw new SquadDeploymentConflictException(
                        "Squad deployment paths changed after their transaction after-images " +
                        $"were published: {string.Join(", ", conflicts)}. " +
                        "The verified journal was retained for recovery.",
                        failure);
                }

                throw;
            }

            lease.ReleaseJournal();
            MarkCleanupOnly(intentPath, intent);
            CleanupOwnedArtifacts(journalDirectory, workDirectory, keepIntent: true);
            RecordProjectRegistration(plan, stateDirectory);
        }
        catch
        {
            if (ownsNewTransaction && !retainTransaction)
            {
                lease.ReleaseJournal();
                CleanupOwnedArtifacts(journalDirectory, workDirectory);
            }

            throw;
        }
        finally
        {
            // A directory survives only if the committed state is in it, so removal is attempted on
            // every path and skips anything non-empty. On success that is the state directory itself.
            if (ownsNewTransaction && !retainTransaction)
            {
                RemoveJournal(journalDirectory, journalDirectoriesCreated);
                RemoveCreatedDirectories(workDirectoriesCreated);
                RemoveCreatedDirectories(stateDirectoriesCreated);
                RemoveCreatedDirectories(journalDirectoriesCreated);
            }
        }
    }

    /// <summary>Rolls back an interrupted transaction. Repeated recovery is a no-op.</summary>
    public void Recover(string targetRoot, SquadDeploymentScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(targetRoot, _semantics);
        using TransactionLease lease = AcquireLease(_stateStore.ResolveLeaseIdentity(identity.PhysicalPath, scope));
        string root = identity.PhysicalPath;
        string journalDirectory = Physical(_stateStore.ResolveTransactionDirectory(root, scope));
        if (!_files.DirectoryExists(journalDirectory))
            return;

        string workDirectory = Physical(_stateStore.ResolveFileWorkDirectory(root, scope));
        bool recoveryCompleted = false;
        IReadOnlyList<CreatedDirectoryAuthority> createdDirectoriesToRemove =
            Array.Empty<CreatedDirectoryAuthority>();
        try
        {
            string intentPath = Path.Combine(journalDirectory, IntentFileName);

            // A journal generation is written to a temporary file and renamed over the intent, so a temporary
            // file left by a kill was never published and the intent beside it is complete.
            // With no intent the temporary file may be the only copy of the journal, so it is left for the refusal.
            if (_files.FileExists(intentPath))
                DiscardUnpublishedIntentTemporaries(journalDirectory);
            else
            {
                // A kill inside the very first journal write leaves only that unpublished temporary file. With any
                // other file the temporary file may be the only copy of the journal, so it is left for the refusal.
                if (HasAnyFileOtherThanIntentTemporaries(journalDirectory) ||
                    (!_semantics.AreSame(journalDirectory, workDirectory) && HasAnyFile(workDirectory)))
                    throw new InvalidDataException(
                        "Squad transaction has preparation artifacts but no authoritative intent journal.");

                // The intent is written before anything else, so a transaction with no intent and no file has
                // changed nothing: only the folders it made are left, and they are empty.
                DiscardUnpublishedIntentTemporaries(journalDirectory);
                CleanupOwnedArtifacts(journalDirectory, workDirectory);
                RemoveEmptyDirectoriesUpTo(
                    journalDirectory,
                    Physical(_stateStore.ResolveStateAuthorityRoot(root, scope)));
                recoveryCompleted = true;
                return;
            }

            IntentDocument intent = ReadIntent(
                intentPath,
                journalDirectory,
                workDirectory,
                identity.Key,
                root,
                _stateStore.ResolveStateAuthorityRoot(root, scope),
                _stateStore.ResolveHomeCreationBoundary(),
                _stateStore.ResolveHomeStateDirectory());
            string lockPath = PhysicalLeaf(_stateStore.ResolveLockPath(root, scope));
            string receiptPath = PhysicalLeaf(_stateStore.ResolveReceiptPath(root, scope));
            if (intent.Phase == TransactionPhase.Committed)
            {
                // The deployment finished; a kill during cleanup left a partial transaction tree. Finish forward.
                createdDirectoriesToRemove = intent.CreatedDirectories;
                CleanupOwnedArtifacts(journalDirectory, workDirectory);
                recoveryCompleted = true;
                return;
            }

            ResolveActiveClaims(
                intent,
                journalDirectory,
                workDirectory,
                lockPath,
                receiptPath);
            IReadOnlyList<string> conflicts = RestoreIntent(intent, workDirectory, lockPath, receiptPath);
            if (conflicts.Count > 0)
                throw new SquadDeploymentConflictException(
                    "Squad deployment paths changed after their transaction after-images " +
                    $"were published: {string.Join(", ", conflicts)}. " +
                    "The verified journal was retained for recovery.");
            createdDirectoriesToRemove = intent.CreatedDirectories;

            MarkCleanupOnly(intentPath, intent);
            CleanupOwnedArtifacts(journalDirectory, workDirectory);
            recoveryCompleted = true;
        }
        finally
        {
            if (recoveryCompleted)
            {
                DeleteEmptyDirectory(journalDirectory);
                RemoveCreatedDirectories(
                    createdDirectoriesToRemove,
                    root,
                    _stateStore.ResolveStateAuthorityRoot(root, scope),
                    _stateStore.ResolveHomeCreationBoundary());
            }
        }
    }

    private IntentDocument CaptureIntent(
        SquadDeploymentPlan plan,
        SquadReceipt receipt,
        string lockPath,
        string receiptPath,
        IReadOnlyList<string> transactionDirectoriesCreated,
        IReadOnlyList<string> journalDirectoriesCreated,
        IReadOnlyList<string> homeDirectoriesCreated,
        string stateAuthorityRoot)
    {
        IntentFile[] files = plan.FileMutations
            .Select(mutation =>
            {
                string physicalRoot = plan.ResolvePhysicalRoot(mutation.Target);
                string fullPath = SquadPathPolicy.ResolveFile(physicalRoot, mutation.RelativePath, _semantics);

                return new IntentFile(
                    mutation.RelativePath,
                    mutation.Target,
                    physicalRoot,
                    GetEntryKind(fullPath),
                    CaptureMissingDirectories(
                            Path.GetDirectoryName(fullPath)!,
                            physicalRoot)
                        .Select(path => Path.GetRelativePath(physicalRoot, path)
                            .Replace(Path.DirectorySeparatorChar, '/'))
                        .ToArray(),
                    mutation.Kind == SquadFileMutationKind.Write
                        ? OriginalEntryKind.File
                        : OriginalEntryKind.Missing,
                    mutation.Kind == SquadFileMutationKind.Write
                        ? Digest((mutation.Content
                                 ?? throw new InvalidOperationException(
                                     $"Write mutation '{mutation.RelativePath}' has no content.")).ToArray())
                        : string.Empty);
            })
            .ToArray();
        OriginalEntryKind lockKind = GetEntryKind(lockPath);
        OriginalEntryKind receiptKind = GetEntryKind(receiptPath);
        (OriginalEntryKind Kind, string Sha256) lockAfter = StateAfterImage(
            plan.LockMutation,
            lockKind,
            lockPath,
            plan.LockMutation == SquadStateMutation.Write
                ? Encoding.UTF8.GetBytes(_stateStore.SerializeLock(
                    plan.Lock ?? throw new InvalidOperationException(
                        "A lock write requires lock content.")))
                : null);
        (OriginalEntryKind Kind, string Sha256) receiptAfter = StateAfterImage(
            plan.ReceiptMutation,
            receiptKind,
            receiptPath,
            plan.ReceiptMutation == SquadStateMutation.Write
                ? Encoding.UTF8.GetBytes(_stateStore.SerializeReceipt(receipt))
                : null);
        return new IntentDocument(
            IntentSchema,
            TransactionPhase.Preparing,
            plan.PhysicalRootKey,
            _ids.NewGuid().ToString("N"),
            files,
            CaptureCreatedDirectoryAuthority(
                transactionDirectoriesCreated,
                journalDirectoriesCreated,
                homeDirectoriesCreated,
                plan.PhysicalRootPath,
                stateAuthorityRoot,
                _stateStore.ResolveHomeCreationBoundary()),
            lockKind,
            lockAfter.Kind,
            lockAfter.Sha256,
            receiptKind,
            receiptAfter.Kind,
            receiptAfter.Sha256,
            Array.Empty<IntentArtifact>(),
            Array.Empty<IntentTransition>());
    }

    private (OriginalEntryKind Kind, string Sha256) StateAfterImage(
        SquadStateMutation mutation,
        OriginalEntryKind originalKind,
        string originalPath,
        byte[]? content)
    {
        return mutation switch
        {
            SquadStateMutation.Keep => (
                originalKind,
                originalKind == OriginalEntryKind.File
                    ? Digest(_files.ReadAllBytes(originalPath))
                    : string.Empty),
            SquadStateMutation.Write => (
                OriginalEntryKind.File,
                Digest(content ?? throw new InvalidOperationException(
                    "A state write requires prepared content."))),
            SquadStateMutation.Delete => (OriginalEntryKind.Missing, string.Empty),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation), mutation, "Unknown state mutation.")
        };
    }

    private void ValidatePreconditions(SquadDeploymentPlan plan)
    {
        foreach (SquadFilePrecondition precondition in plan.FilePreconditions)
            ValidatePrecondition(plan, precondition);
    }

    private void ValidatePrecondition(
        SquadDeploymentPlan plan,
        SquadFilePrecondition precondition)
    {
        string fullPath = plan.ResolvePhysicalPath(precondition.Target, precondition.RelativePath, _semantics);
        OriginalEntryKind kind = GetEntryKind(fullPath);
        if (precondition.Kind == SquadFilePreconditionKind.Missing)
        {
            if (kind != OriginalEntryKind.Missing)
                throw PreconditionConflict(precondition.RelativePath);
            return;
        }

        if (kind is not (OriginalEntryKind.File or OriginalEntryKind.FileSymbolicLink) ||
            !string.Equals(
                Digest(_files.ReadAllBytes(fullPath)),
                precondition.Sha256,
                StringComparison.Ordinal))
            throw PreconditionConflict(precondition.RelativePath);
    }

    private IReadOnlyList<string> StageFiles(
        SquadDeploymentPlan plan,
        string transactionId,
        string workDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        List<string> staged = new();
        foreach (SquadFileMutation mutation in plan.FileMutations)
        {
            if (mutation.Kind != SquadFileMutationKind.Write)
                continue;

            string stagingRelativePath = TargetWorkRelative("staging", mutation.Target, mutation.RelativePath);
            string stagingPath = TransactionFilePath(
                workDirectory,
                "staging",
                $"{mutation.Target}/{mutation.RelativePath}");
            byte[] content = (mutation.Content
                             ?? throw new InvalidOperationException(
                                 $"Write mutation '{mutation.RelativePath}' has no content.")).ToArray();
            PublishArtifact(
                workDirectory,
                ArtifactArea.Work,
                ArtifactRole.TargetStage,
                transactionId,
                stagingRelativePath,
                stagingPath,
                content,
                artifacts);
            staged.Add(mutation.RelativePath);
        }

        return staged;
    }

    private IReadOnlyList<string> BackupFiles(
        SquadDeploymentPlan plan,
        IntentDocument intent,
        string workDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        List<string> backedUp = new();
        Dictionary<string, IntentFile> intentByPath = intent.Files.ToDictionary(
            file => FileKey(file.Target, file.RelativePath),
            StringComparer.Ordinal);

        foreach (SquadFileMutation mutation in plan.FileMutations)
        {
            IntentFile original = intentByPath[FileKey(mutation.Target, mutation.RelativePath)];
            if (original.OriginalKind == OriginalEntryKind.Missing)
                continue;

            string targetPath = SquadPathPolicy.ResolveFile(original.PhysicalRoot, mutation.RelativePath, _semantics);
            string backupPath = TransactionFilePath(
                workDirectory,
                "backups",
                $"{mutation.Target}/{mutation.RelativePath}");
            BackupEntry(
                targetPath,
                backupPath,
                original.OriginalKind,
                TransactionFilePath(workDirectory, "links", $"{mutation.Target}/{mutation.RelativePath}"),
                workDirectory,
                $"{mutation.Target}/{mutation.RelativePath}",
                intent.TransactionId,
                artifacts);

            backedUp.Add(mutation.RelativePath);
        }

        return backedUp;
    }

    private void BackupStateFiles(
        IntentDocument intent,
        string workDirectory,
        string lockPath,
        string receiptPath,
        ICollection<IntentArtifact> artifacts)
    {
        CopyStateOriginal(
            lockPath,
            StateOriginalPath(workDirectory, "lock"),
            StateOriginalLinkPath(workDirectory, "lock"),
            intent.LockKind,
            workDirectory,
            "state-originals/lock",
            "state-original-links/lock",
            intent.TransactionId,
            artifacts);
        CopyStateOriginal(
            receiptPath,
            StateOriginalPath(workDirectory, "receipt"),
            StateOriginalLinkPath(workDirectory, "receipt"),
            intent.ReceiptKind,
            workDirectory,
            "state-originals/receipt",
            "state-original-links/receipt",
            intent.TransactionId,
            artifacts);
    }

    private void PrepareStateFiles(
        SquadDeploymentPlan plan,
        SquadReceipt receipt,
        string transactionId,
        string journalDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        if (plan.LockMutation == SquadStateMutation.Write)
        {
            SquadLock squadLock = plan.Lock
                                  ?? throw new InvalidOperationException("A lock write requires lock content.");
            PublishArtifact(
                journalDirectory,
                ArtifactArea.Journal,
                ArtifactRole.StateStage,
                transactionId,
                "state-staging/lock",
                StateStagingPath(journalDirectory, "lock"),
                Encoding.UTF8.GetBytes(_stateStore.SerializeLock(squadLock)),
                artifacts);
        }

        if (plan.ReceiptMutation == SquadStateMutation.Write)
            PublishArtifact(
                journalDirectory,
                ArtifactArea.Journal,
                ArtifactRole.StateStage,
                transactionId,
                "state-staging/receipt",
                StateStagingPath(journalDirectory, "receipt"),
                Encoding.UTF8.GetBytes(_stateStore.SerializeReceipt(receipt)),
                artifacts);
    }

    private void ApplyFiles(
        SquadDeploymentPlan plan,
        string workDirectory,
        string intentPath,
        TransactionLease lease,
        ref IntentDocument intent,
        ref int sequence,
        ref int checkpointSequence)
    {
        Dictionary<string, SquadFilePrecondition> preconditions = plan.FilePreconditions.ToDictionary(
            precondition => FileKey(precondition.Target, precondition.RelativePath),
            StringComparer.Ordinal);

        foreach (SquadFileMutation mutation in plan.FileMutations)
        {
            IntentFile fileIntent = intent.Files.Single(file =>
                string.Equals(file.RelativePath, mutation.RelativePath, StringComparison.Ordinal) &&
                string.Equals(file.Target, mutation.Target, StringComparison.Ordinal));
            SquadFilePrecondition precondition = preconditions[FileKey(mutation.Target, mutation.RelativePath)];
            string boundRoot = fileIntent.PhysicalRoot;
            string stagingRelativePath = TargetWorkRelative("staging", mutation.Target, mutation.RelativePath);

            if (mutation.Kind == SquadFileMutationKind.Write)
            {
                string stagingPath = TransactionFilePath(
                    workDirectory,
                    "staging",
                    $"{mutation.Target}/{mutation.RelativePath}");
                VerifyPreparedStage(
                    intent,
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    stagingRelativePath,
                    stagingPath,
                    mutation.RelativePath);
            }

            ClaimExistingNoOverwrite(
                boundRoot,
                mutation.RelativePath,
                FileKey(mutation.Target, mutation.RelativePath),
                workDirectory,
                ArtifactArea.Work,
                fileIntent.OriginalKind,
                precondition.Kind == SquadFilePreconditionKind.ExactFile
                    ? precondition.Sha256 ?? string.Empty
                    : string.Empty,
                intentPath,
                lease,
                _observer,
                mutation.RelativePath,
                ref checkpointSequence,
                ref intent);

            if (mutation.Kind == SquadFileMutationKind.Write)
            {
                string stagingPath = TransactionFilePath(
                    workDirectory,
                    "staging",
                    $"{mutation.Target}/{mutation.RelativePath}");
                BeginArtifactTransition(
                    ArtifactArea.Work,
                    stagingRelativePath,
                    intentPath,
                    lease,
                    ref intent);
                string targetPath = SquadPathPolicy.ResolveFile(boundRoot, mutation.RelativePath, _semantics);
                _files.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                PublishStageNoOverwrite(
                    intent,
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    stagingRelativePath,
                    boundRoot,
                    mutation.RelativePath,
                    stagingPath,
                    targetPath);
                RemoveEmptyArtifactParents(stagingPath, workDirectory);
                VerifyAfterImage(
                    targetPath,
                    OriginalEntryKind.File,
                    fileIntent.AfterSha256,
                    mutation.RelativePath);
                NotifyCheckpoint(
                    ref checkpointSequence,
                    _observer,
                    SquadTransactionCheckpointKind.AfterImagePublished,
                    mutation.RelativePath);
                CompleteArtifactTransition(
                    ArtifactArea.Work,
                    stagingRelativePath,
                    intentPath,
                    lease,
                    ref intent);
            }
            else
            {
                string deletedPath = SquadPathPolicy.ResolveFile(boundRoot, mutation.RelativePath, _semantics);
                VerifyAfterImage(
                    deletedPath,
                    OriginalEntryKind.Missing,
                    string.Empty,
                    mutation.RelativePath);
                NotifyCheckpoint(
                    ref checkpointSequence,
                    _observer,
                    SquadTransactionCheckpointKind.AfterImagePublished,
                    mutation.RelativePath);
            }

            Notify(
                ref sequence,
                SquadTransactionStepKind.FileApplied,
                mutation.RelativePath);
        }
    }

    private void ApplyLock(
        SquadDeploymentPlan plan,
        string journalDirectory,
        string workDirectory,
        string intentPath,
        string lockPath,
        TransactionLease lease,
        ref IntentDocument intent,
        ref int sequence,
        ref int checkpointSequence)
    {
        switch (plan.LockMutation)
        {
            case SquadStateMutation.Keep:
                return;
            case SquadStateMutation.Write:
                ApplyStateFileNoOverwrite(
                    journalDirectory,
                    workDirectory,
                    "lock",
                    lockPath,
                    intent.LockKind,
                    intent.LockAfterSha256,
                    intentPath,
                    lease,
                    _observer,
                    ref checkpointSequence,
                    ref intent);
                break;
            case SquadStateMutation.Delete:
                ClaimStateNoOverwrite(
                    workDirectory,
                    "lock",
                    lockPath,
                    intent.LockKind,
                    intentPath,
                    lease,
                    _observer,
                    ref checkpointSequence,
                    ref intent);
                VerifyAfterImage(
                    lockPath,
                    OriginalEntryKind.Missing,
                    string.Empty,
                    "lock");
                NotifyCheckpoint(
                    ref checkpointSequence,
                    _observer,
                    SquadTransactionCheckpointKind.AfterImagePublished,
                    "lock");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(plan),
                    plan.LockMutation,
                    "Unknown lock mutation.");
        }

        Notify(ref sequence, SquadTransactionStepKind.LockApplied);
    }

    private void ApplyReceipt(
        SquadDeploymentPlan plan,
        string journalDirectory,
        string workDirectory,
        string intentPath,
        string receiptPath,
        TransactionLease lease,
        ref IntentDocument intent,
        ref int sequence,
        ref int checkpointSequence)
    {
        switch (plan.ReceiptMutation)
        {
            case SquadStateMutation.Keep:
                return;
            case SquadStateMutation.Write:
                ApplyStateFileNoOverwrite(
                    journalDirectory,
                    workDirectory,
                    "receipt",
                    receiptPath,
                    intent.ReceiptKind,
                    intent.ReceiptAfterSha256,
                    intentPath,
                    lease,
                    _observer,
                    ref checkpointSequence,
                    ref intent);
                break;
            case SquadStateMutation.Delete:
                ClaimStateNoOverwrite(
                    workDirectory,
                    "receipt",
                    receiptPath,
                    intent.ReceiptKind,
                    intentPath,
                    lease,
                    _observer,
                    ref checkpointSequence,
                    ref intent);
                VerifyAfterImage(
                    receiptPath,
                    OriginalEntryKind.Missing,
                    string.Empty,
                    "receipt");
                NotifyCheckpoint(
                    ref checkpointSequence,
                    _observer,
                    SquadTransactionCheckpointKind.AfterImagePublished,
                    "receipt");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(plan),
                    plan.ReceiptMutation,
                    "Unknown receipt mutation.");
        }

        Notify(ref sequence, SquadTransactionStepKind.ReceiptApplied);
    }

    private void ApplyStateFileNoOverwrite(
        string journalDirectory,
        string workDirectory,
        string stageName,
        string targetPath,
        OriginalEntryKind originalKind,
        string afterSha256,
        string intentPath,
        TransactionLease lease,
        ISquadTransactionObserver? observer,
        ref int checkpointSequence,
        ref IntentDocument intent)
    {
        string stagingPath = StateStagingPath(journalDirectory, stageName);
        string stageRelativePath = $"state-staging/{stageName}";
        VerifyPreparedStage(
            intent,
            ArtifactArea.Journal,
            ArtifactRole.StateStage,
            stageRelativePath,
            stagingPath,
            stageName);
        ClaimStateNoOverwrite(
            workDirectory,
            stageName,
            targetPath,
            originalKind,
            intentPath,
            lease,
            observer,
            ref checkpointSequence,
            ref intent);
        BeginArtifactTransition(
            ArtifactArea.Journal,
            stageRelativePath,
            intentPath,
            lease,
            ref intent);
        PublishStageNoOverwrite(
            intent,
            ArtifactArea.Journal,
            ArtifactRole.StateStage,
            stageRelativePath,
            Path.GetDirectoryName(targetPath)!,
            Path.GetFileName(targetPath),
            stagingPath,
            targetPath);
        RemoveEmptyArtifactParents(stagingPath, journalDirectory);
        VerifyAfterImage(targetPath, OriginalEntryKind.File, afterSha256, stageName);
        NotifyCheckpoint(
            ref checkpointSequence,
            observer,
            SquadTransactionCheckpointKind.AfterImagePublished,
            stageName);
        CompleteArtifactTransition(
            ArtifactArea.Journal,
            stageRelativePath,
            intentPath,
            lease,
            ref intent);
    }

    private void ClaimStateNoOverwrite(
        string workDirectory,
        string stateName,
        string targetPath,
        OriginalEntryKind originalKind,
        string intentPath,
        TransactionLease lease,
        ISquadTransactionObserver? observer,
        ref int checkpointSequence,
        ref IntentDocument intent)
    {
        ClaimExistingNoOverwrite(
            Path.GetDirectoryName(targetPath)!,
            Path.GetFileName(targetPath),
            Path.GetFileName(targetPath),
            _semantics.AreSame(
                workDirectory,
                Path.GetDirectoryName(intentPath)!)
                ? workDirectory
                : Path.GetDirectoryName(intentPath)!,
            _semantics.AreSame(
                workDirectory,
                Path.GetDirectoryName(intentPath)!)
                ? ArtifactArea.Work
                : ArtifactArea.Journal,
            originalKind,
            string.Empty,
            intentPath,
            lease,
            observer,
            stateName,
            ref checkpointSequence,
            ref intent);
    }

    private void ClaimExistingNoOverwrite(
        string boundRoot,
        string relativePath,
        string claimIdentity,
        string claimRoot,
        ArtifactArea claimArea,
        OriginalEntryKind expectedKind,
        string expectedSha256,
        string intentPath,
        TransactionLease lease,
        ISquadTransactionObserver? observer,
        string subject,
        ref int checkpointSequence,
        ref IntentDocument intent)
    {
        string targetPath = SquadPathPolicy.ResolveFile(boundRoot, relativePath, _semantics);
        OriginalEntryKind currentKind = GetEntryKind(targetPath);
        if (currentKind == OriginalEntryKind.Missing)
        {
            if (expectedKind != OriginalEntryKind.Missing)
                throw ClaimConflict(relativePath);
            return;
        }

        string claimRelativePath = ClaimRelativePath(intent.TransactionId, claimIdentity);
        string claimPath = SquadPathPolicy.ResolveFile(claimRoot, claimRelativePath, _semantics);
        IntentArtifact artifact = CaptureNodeArtifact(
            intent.TransactionId,
            claimArea,
            ArtifactRole.ClaimedOriginal,
            claimRelativePath,
            targetPath,
            ArtifactLifecycleState.ActiveTransition);
        string? resolvedDirectoryLinkTarget = currentKind == OriginalEntryKind.DirectorySymbolicLink &&
                                              !Path.IsPathRooted(artifact.LinkTarget)
            ? _files.GetFinalLinkTarget(targetPath)
            : null;
        intent = intent with
        {
            Artifacts = [.. intent.Artifacts, artifact],
            ActiveTransitions =
            [new IntentTransition(ArtifactIdentity(claimArea, claimRelativePath), ArtifactLifecycleState.PreOperation)]
        };
        PublishIntentGeneration(intentPath, intent, lease);
        NotifyCheckpoint(
            ref checkpointSequence,
            observer,
            SquadTransactionCheckpointKind.ActiveTransitionWritten,
            subject);

        string checkedTarget = SquadPathPolicy.ResolveFile(boundRoot, relativePath, _semantics);
        if (!_semantics.AreSame(checkedTarget, targetPath))
            throw ClaimConflict(relativePath);
        MoveEntryNoOverwrite(targetPath, claimPath, currentKind);
        if (resolvedDirectoryLinkTarget is not null)
        {
            DeleteEntry(claimPath, false);
            _files.CreateDirectoryLink(
                claimPath,
                Path.GetRelativePath(
                    Path.GetDirectoryName(claimPath)!,
                    resolvedDirectoryLinkTarget));
            artifact = CaptureNodeArtifact(
                intent.TransactionId,
                claimArea,
                ArtifactRole.ClaimedOriginal,
                claimRelativePath,
                claimPath,
                ArtifactLifecycleState.ActiveTransition);
            intent = intent with
            {
                Artifacts = intent.Artifacts.Select(value =>
                        string.Equals(
                            ArtifactIdentity(value.Area, value.Path),
                            ArtifactIdentity(claimArea, claimRelativePath),
                            StringComparison.Ordinal)
                            ? artifact
                            : value)
                    .ToArray()
            };
            PublishIntentGeneration(intentPath, intent, lease);
        }

        if (!ArtifactFingerprintMatches(claimPath, artifact))
            throw new InvalidDataException(
                $"Squad claimed original '{relativePath}' changed during its atomic claim.");
        NotifyCheckpoint(
            ref checkpointSequence,
            observer,
            SquadTransactionCheckpointKind.OriginalClaimed,
            subject);

        bool matchesExpected = expectedKind != OriginalEntryKind.Missing &&
                               artifact.NodeKind == currentKind &&
                               ArtifactFingerprintMatches(claimPath, artifact) &&
                               (expectedKind != OriginalEntryKind.File || expectedSha256.Length == 0 ||
                                string.Equals(
                                    Digest(_files.ReadAllBytes(claimPath)),
                                    expectedSha256,
                                    StringComparison.Ordinal));
        if (!matchesExpected)
        {
            if (GetEntryKind(targetPath) == OriginalEntryKind.Missing)
            {
                VerifyAndMoveArtifactNoOverwrite(
                    artifact,
                    claimPath,
                    targetPath,
                    relativePath);
                intent = intent with
                {
                    Artifacts = intent.Artifacts.Where(value =>
                            !string.Equals(
                                ArtifactIdentity(value.Area, value.Path),
                                ArtifactIdentity(claimArea, claimRelativePath),
                                StringComparison.Ordinal))
                        .ToArray(),
                    ActiveTransitions = Array.Empty<IntentTransition>()
                };
            }
            else
            {
                intent = SetArtifactLifecycle(
                    intent,
                    claimArea,
                    claimRelativePath,
                    ArtifactLifecycleState.PostOperation);
            }

            PublishIntentGeneration(intentPath, intent, lease);
            throw ClaimConflict(relativePath);
        }

        intent = SetArtifactLifecycle(
            intent,
            claimArea,
            claimRelativePath,
            ArtifactLifecycleState.PostOperation);
        PublishIntentGeneration(intentPath, intent, lease);
    }

    private void PublishStageNoOverwrite(
        IntentDocument intent,
        ArtifactArea area,
        ArtifactRole role,
        string artifactRelativePath,
        string boundRoot,
        string relativePath,
        string stagingPath,
        string targetPath)
    {
        string checkedTarget = SquadPathPolicy.ResolveFile(boundRoot, relativePath, _semantics);
        if (!_semantics.AreSame(checkedTarget, targetPath))
            throw PreconditionConflict(relativePath);

        IntentArtifact[] artifacts = intent.Artifacts.Where(artifact =>
                artifact.Area == area &&
                artifact.Role == role &&
                string.Equals(
                    artifact.Path,
                    artifactRelativePath,
                    StringComparison.Ordinal))
            .ToArray();
        if (artifacts.Length != 1 ||
            GetEntryKind(stagingPath) != artifacts[0].NodeKind ||
            !ArtifactFingerprintMatches(stagingPath, artifacts[0]))
            throw new InvalidDataException(
                $"Squad prepared artifact '{relativePath}' changed before publication.");

        MoveEntryNoOverwrite(stagingPath, targetPath, artifacts[0].NodeKind);
    }

    private void VerifyAfterImage(
        string path,
        OriginalEntryKind expectedKind,
        string expectedSha256,
        string displayPath)
    {
        if (GetEntryKind(path) != expectedKind ||
            (expectedKind == OriginalEntryKind.File &&
             !string.Equals(
                 Digest(_files.ReadAllBytes(path)),
                 expectedSha256,
                 StringComparison.Ordinal)))
            throw new InvalidDataException(
                $"Squad after-image '{displayPath}' failed verification.");
    }

    private IReadOnlyList<string> RestoreIntent(
        IntentDocument intent,
        string workDirectory,
        string lockPath,
        string receiptPath)
    {
        if (intent.Phase == TransactionPhase.Preparing)
            return Array.Empty<string>();

        VerifyRestoreArtifacts(intent);
        List<string> conflicts = new();

        // Each file carries its own resolved physical root (project root under Project scope,
        // the target's global root under Global scope), captured once when the transaction was
        // prepared. Recovery replays it verbatim rather than re-deriving it, so it never needs
        // the originating plan or a live ISquadGlobalRootResolver, and stays correct even if two
        // targets share a relative path under different Global-scope physical roots.
        foreach (IntentFile file in intent.Files.Reverse())
        {
            string targetPath = SquadPathPolicy.ResolveFile(file.PhysicalRoot, file.RelativePath, _semantics);
            string backupPath = TransactionFilePath(
                workDirectory,
                "backups",
                $"{file.Target}/{file.RelativePath}");
            string linkMetadataPath = TransactionFilePath(
                workDirectory,
                "links",
                $"{file.Target}/{file.RelativePath}");
            CompareAndRestoreEntry(
                intent,
                targetPath,
                backupPath,
                file.OriginalKind,
                linkMetadataPath,
                ArtifactRole.TargetBackup,
                TargetWorkRelative("backups", file.Target, file.RelativePath),
                ArtifactRole.TargetLinkMetadata,
                TargetWorkRelative("links", file.Target, file.RelativePath),
                file.AfterKind,
                file.AfterSha256,
                file.RelativePath,
                conflicts);
        }

        // Keyed by (PhysicalRoot, RelativeDirectory): two targets under Global scope can share
        // the same relative missing-parent text (for example "agents") under different physical
        // roots, and deduping by the relative text alone would silently skip cleanup for one of
        // them.
        HashSet<(string PhysicalRoot, string RelativeDirectory)> seenDirs = [];
        foreach (IntentFile file in intent.Files)
            foreach (string relativeDirectory in file.MissingParentDirectories)
            {
                if (!seenDirs.Add((file.PhysicalRoot, relativeDirectory)))
                    continue;

                string directory = SquadPathPolicy.ResolveFile(file.PhysicalRoot, relativeDirectory, _semantics);
                DeleteEmptyDirectory(directory);
                if (GetEntryKind(directory) != OriginalEntryKind.Missing)
                    conflicts.Add(relativeDirectory);
            }

        CompareAndRestoreEntry(
            intent,
            lockPath,
            StateOriginalPath(workDirectory, "lock"),
            intent.LockKind,
            StateOriginalLinkPath(workDirectory, "lock"),
            ArtifactRole.StateOriginal,
            "state-originals/lock",
            ArtifactRole.StateLinkMetadata,
            "state-original-links/lock",
            intent.LockAfterKind,
            intent.LockAfterSha256,
            "squad.lock.yml",
            conflicts);
        CompareAndRestoreEntry(
            intent,
            receiptPath,
            StateOriginalPath(workDirectory, "receipt"),
            intent.ReceiptKind,
            StateOriginalLinkPath(workDirectory, "receipt"),
            ArtifactRole.StateOriginal,
            "state-originals/receipt",
            ArtifactRole.StateLinkMetadata,
            "state-original-links/receipt",
            intent.ReceiptAfterKind,
            intent.ReceiptAfterSha256,
            "squad.receipt.json",
            conflicts);
        return conflicts.Distinct(StringComparer.Ordinal).ToArray();
    }

    private void ResolveActiveClaims(
        IntentDocument intent,
        string journalDirectory,
        string workDirectory,
        string lockPath,
        string receiptPath)
    {
        foreach (IntentArtifact artifact in intent.Artifacts.Where(artifact =>
                     artifact is
                     {
                         Role: ArtifactRole.ClaimedOriginal,
                         LifecycleState: ArtifactLifecycleState.ActiveTransition or ArtifactLifecycleState.PostOperation
                     }))
        {
            string? targetPath = null;
            foreach (IntentFile file in intent.Files)
                if (string.Equals(
                        artifact.Path,
                        ClaimRelativePath(intent.TransactionId, FileKey(file.Target, file.RelativePath)),
                        StringComparison.Ordinal))
                {
                    targetPath = SquadPathPolicy.ResolveFile(file.PhysicalRoot, file.RelativePath, _semantics);
                    break;
                }

            targetPath ??= string.Equals(
                artifact.Path,
                ClaimRelativePath(intent.TransactionId, Path.GetFileName(lockPath)),
                StringComparison.Ordinal)
                ? lockPath
                : null;
            targetPath ??= string.Equals(
                artifact.Path,
                ClaimRelativePath(intent.TransactionId, Path.GetFileName(receiptPath)),
                StringComparison.Ordinal)
                ? receiptPath
                : null;
            if (targetPath is null)
                throw new InvalidDataException(
                    $"Squad active claim '{artifact.Path}' has no transaction subject.");

            string artifactRoot = artifact.Area == ArtifactArea.Journal
                ? journalDirectory
                : workDirectory;
            string claimPath = ResolveArtifactNode(artifactRoot, artifact.Path);
            OriginalEntryKind claimKind = GetEntryKind(claimPath);
            OriginalEntryKind targetKind = GetEntryKind(targetPath);
            if (claimKind == OriginalEntryKind.Missing)
            {
                if (targetKind == OriginalEntryKind.Missing)
                    throw new InvalidDataException(
                        $"Squad active claim '{artifact.Path}' lost both transition alternatives.");

                continue;
            }

            if (targetKind == OriginalEntryKind.Missing)
            {
                _files.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                VerifyAndMoveArtifactNoOverwrite(
                    artifact,
                    claimPath,
                    targetPath,
                    artifact.Path);
            }
        }
    }

    private void VerifyAndMoveArtifactNoOverwrite(
        IntentArtifact artifact,
        string sourcePath,
        string destinationPath,
        string displayPath)
    {
        if (GetEntryKind(sourcePath) != artifact.NodeKind ||
            !ArtifactFingerprintMatches(sourcePath, artifact))
            throw new InvalidDataException(
                $"Squad claim artifact '{displayPath}' changed before restoration.");

        MoveEntryNoOverwrite(sourcePath, destinationPath, artifact.NodeKind);
        if (GetEntryKind(destinationPath) != artifact.NodeKind ||
            !ArtifactFingerprintMatches(destinationPath, artifact))
            throw new InvalidDataException(
                $"Squad claim artifact '{displayPath}' changed during restoration.");
    }

    private bool EntryMatchesOriginal(
        string targetPath,
        CapturedRestorePayload payload)
    {
        OriginalEntryKind currentKind = GetEntryKind(targetPath);
        if (currentKind != payload.OriginalKind)
            return false;

        return payload.OriginalKind switch
        {
            OriginalEntryKind.Missing or OriginalEntryKind.Directory => true,
            OriginalEntryKind.File => string.Equals(
                Digest(_files.ReadAllBytes(targetPath)),
                payload.ContentSha256,
                StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink =>
                string.Equals(
                    _files.GetLinkTarget(targetPath),
                    payload.LinkTarget,
                    StringComparison.Ordinal),
            OriginalEntryKind.DirectorySymbolicLink =>
                string.Equals(
                    _files.GetLinkTarget(targetPath),
                    payload.LinkTarget,
                    StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(
                nameof(payload), payload.OriginalKind, "Unknown original entry kind.")
        };
    }

    private bool EntryMatchesAfter(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload)
    {
        OriginalEntryKind currentKind = GetEntryKind(targetPath);
        if (currentKind != afterKind)
            return false;

        return afterKind switch
        {
            OriginalEntryKind.Missing or OriginalEntryKind.Directory => true,
            OriginalEntryKind.File => string.Equals(
                Digest(_files.ReadAllBytes(targetPath)),
                afterSha256,
                StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink or OriginalEntryKind.DirectorySymbolicLink =>
                EntryMatchesOriginal(targetPath, payload),
            _ => throw new ArgumentOutOfRangeException(
                nameof(afterKind), afterKind, "Unknown after-image entry kind.")
        };
    }

    private void VerifyRestoreArtifacts(IntentDocument intent)
    {
        foreach (IntentFile file in intent.Files)
            VerifyRestoreArtifacts(
                intent,
                file.OriginalKind,
                ArtifactRole.TargetBackup,
                TargetWorkRelative("backups", file.Target, file.RelativePath),
                ArtifactRole.TargetLinkMetadata,
                TargetWorkRelative("links", file.Target, file.RelativePath));

        VerifyRestoreArtifacts(
            intent,
            intent.LockKind,
            ArtifactRole.StateOriginal,
            "state-originals/lock",
            ArtifactRole.StateLinkMetadata,
            "state-original-links/lock");
        VerifyRestoreArtifacts(
            intent,
            intent.ReceiptKind,
            ArtifactRole.StateOriginal,
            "state-originals/receipt",
            ArtifactRole.StateLinkMetadata,
            "state-original-links/receipt");
    }

    private void VerifyRestoreArtifacts(
        IntentDocument intent,
        OriginalEntryKind originalKind,
        ArtifactRole backupRole,
        string backupRelativePath,
        ArtifactRole linkRole,
        string linkRelativePath)
    {
        if (originalKind is OriginalEntryKind.File or
            OriginalEntryKind.Directory or
            OriginalEntryKind.FileSymbolicLink)
            _ = RequireRestoreArtifact(
                intent,
                backupRole,
                backupRelativePath);

        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
            _ = RequireRestoreArtifact(
                intent,
                linkRole,
                linkRelativePath);
    }

    private IntentArtifact RequireRestoreArtifact(
        IntentDocument intent,
        ArtifactRole role,
        string relativePath)
    {
        IntentArtifact[] matches = intent.Artifacts.Where(artifact =>
                artifact.Area == ArtifactArea.Work &&
                artifact.Role == role &&
                string.Equals(artifact.Path, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].LifecycleState != ArtifactLifecycleState.PreOperation)
            throw new InvalidDataException(
                $"Squad restore artifact '{relativePath}' has invalid authority.");

        return matches[0];
    }

    private CapturedRestorePayload CaptureVerifiedRestorePayload(
        IntentDocument intent,
        string backupPath,
        OriginalEntryKind originalKind,
        string linkMetadataPath,
        ArtifactRole backupRole,
        string backupRelativePath,
        ArtifactRole linkRole,
        string linkRelativePath)
    {
        byte[] backupBytes = [];
        if (originalKind is OriginalEntryKind.File or OriginalEntryKind.FileSymbolicLink)
        {
            IntentArtifact authority = RequireRestoreArtifact(intent, backupRole, backupRelativePath);
            OriginalEntryKind beforeKind = GetEntryKind(backupPath);
            backupBytes = _files.ReadAllBytes(backupPath);
            OriginalEntryKind afterKind = GetEntryKind(backupPath);
            if (beforeKind != authority.NodeKind ||
                afterKind != authority.NodeKind ||
                backupBytes.LongLength != authority.ByteLength ||
                !string.Equals(Digest(backupBytes), authority.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Squad restore artifact '{backupRelativePath}' changed during capture.");
        }
        else if (originalKind == OriginalEntryKind.Directory)
        {
            IntentArtifact authority = RequireRestoreArtifact(intent, backupRole, backupRelativePath);
            OriginalEntryKind beforeKind = GetEntryKind(backupPath);
            OriginalEntryKind afterKind = GetEntryKind(backupPath);
            if (beforeKind != authority.NodeKind || afterKind != authority.NodeKind)
                throw new InvalidDataException(
                    $"Squad restore artifact '{backupRelativePath}' changed during capture.");
        }

        string linkTarget = string.Empty;
        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
        {
            IntentArtifact authority = RequireRestoreArtifact(intent, linkRole, linkRelativePath);
            OriginalEntryKind beforeKind = GetEntryKind(linkMetadataPath);
            byte[] linkBytes = _files.ReadAllBytes(linkMetadataPath);
            OriginalEntryKind afterKind = GetEntryKind(linkMetadataPath);
            if (beforeKind != authority.NodeKind ||
                afterKind != authority.NodeKind ||
                linkBytes.LongLength != authority.ByteLength ||
                !string.Equals(Digest(linkBytes), authority.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Squad restore artifact '{linkRelativePath}' changed during capture.");

            try
            {
                linkTarget = new UTF8Encoding(false, true).GetString(linkBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Squad restore artifact '{linkRelativePath}' is not valid UTF-8.",
                    exception);
            }
        }

        return new CapturedRestorePayload(
            originalKind,
            backupBytes,
            backupBytes.Length == 0 ? string.Empty : Digest(backupBytes),
            linkTarget);
    }

    private void RestoreEntry(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload,
        string displayPath)
    {
        _files.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (!VerifyRestoreAfterImage(targetPath, afterKind, afterSha256, payload) ||
            GetEntryKind(targetPath) != afterKind)
            throw new InvalidDataException(
                $"Squad live after-image '{displayPath}' changed before restoration.");

        DeleteEntry(targetPath, true);
        switch (payload.OriginalKind)
        {
            case OriginalEntryKind.Missing:
                break;
            case OriginalEntryKind.File:
                WriteNewFile(targetPath, payload.Content.Span);

                break;
            case OriginalEntryKind.Directory:
                _files.CreateDirectory(targetPath);
                break;
            case OriginalEntryKind.FileSymbolicLink:
                _files.CreateFileLink(targetPath, payload.LinkTarget);
                break;
            case OriginalEntryKind.DirectorySymbolicLink:
                _files.CreateDirectoryLink(targetPath, payload.LinkTarget);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(payload), payload.OriginalKind, "Unknown original entry kind.");
        }

        if (!EntryMatchesOriginal(targetPath, payload))
            throw new InvalidDataException(
                $"Squad restored entry '{displayPath}' failed verification.");
    }

    private bool VerifyRestoreAfterImage(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload)
    {
        return EntryMatchesAfter(targetPath, afterKind, afterSha256, payload);
    }

    private void CompareAndRestoreEntry(
        IntentDocument intent,
        string targetPath,
        string backupPath,
        OriginalEntryKind originalKind,
        string linkMetadataPath,
        ArtifactRole backupRole,
        string backupRelativePath,
        ArtifactRole linkRole,
        string linkRelativePath,
        OriginalEntryKind afterKind,
        string afterSha256,
        string displayPath,
        ICollection<string> conflicts)
    {
        CapturedRestorePayload payload = CaptureVerifiedRestorePayload(
            intent,
            backupPath,
            originalKind,
            linkMetadataPath,
            backupRole,
            backupRelativePath,
            linkRole,
            linkRelativePath);
        if (EntryMatchesOriginal(targetPath, payload))
            return;

        if (!EntryMatchesAfter(targetPath, afterKind, afterSha256, payload))
        {
            conflicts.Add(displayPath);
            return;
        }

        RestoreEntry(targetPath, afterKind, afterSha256, payload, displayPath);
    }

    private void VerifyPreparedStage(
        IntentDocument intent,
        ArtifactArea area,
        ArtifactRole role,
        string relativePath,
        string stagingPath,
        string displayPath)
    {
        IntentArtifact[] matches = intent.Artifacts.Where(artifact =>
                artifact.Area == area &&
                artifact.Role == role &&
                string.Equals(artifact.Path, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].LifecycleState != ArtifactLifecycleState.PreOperation ||
            GetEntryKind(stagingPath) != matches[0].NodeKind ||
            !ArtifactFingerprintMatches(stagingPath, matches[0]))
            throw new InvalidDataException(
                $"Squad prepared artifact '{displayPath}' changed before it was consumed.");
    }

    private void BeginArtifactTransition(
        ArtifactArea area,
        string relativePath,
        string intentPath,
        TransactionLease lease,
        ref IntentDocument intent)
    {
        intent = SetArtifactLifecycle(
                intent,
                area,
                relativePath,
                ArtifactLifecycleState.ActiveTransition) with
        {
            ActiveTransitions =
                [new IntentTransition(ArtifactIdentity(area, relativePath), ArtifactLifecycleState.PreOperation)]
        };
        PublishIntentGeneration(intentPath, intent, lease);
    }

    private void CompleteArtifactTransition(
        ArtifactArea area,
        string relativePath,
        string intentPath,
        TransactionLease lease,
        ref IntentDocument intent)
    {
        intent = SetArtifactLifecycle(
            intent,
            area,
            relativePath,
            ArtifactLifecycleState.PostOperation);
        PublishIntentGeneration(intentPath, intent, lease);
    }

    private IntentDocument SetArtifactLifecycle(
        IntentDocument intent,
        ArtifactArea area,
        string relativePath,
        ArtifactLifecycleState lifecycleState)
    {
        string identity = ArtifactIdentity(area, relativePath);
        int changed = 0;
        IntentArtifact[] artifacts = intent.Artifacts.Select(artifact =>
        {
            if (!string.Equals(
                    ArtifactIdentity(artifact.Area, artifact.Path),
                    identity,
                    StringComparison.Ordinal))
                return artifact;

            changed++;
            return artifact with { LifecycleState = lifecycleState };
        }).ToArray();
        if (changed != 1)
            throw new InvalidDataException(
                $"Squad transition artifact '{relativePath}' is missing or duplicated.");

        return intent with
        {
            Artifacts = artifacts,
            ActiveTransitions = lifecycleState == ArtifactLifecycleState.ActiveTransition
                ? intent.ActiveTransitions
                : Array.Empty<IntentTransition>()
        };
    }

    private IntentArtifact CaptureNodeArtifact(
        string transactionId,
        ArtifactArea area,
        ArtifactRole role,
        string relativePath,
        string sourcePath,
        ArtifactLifecycleState lifecycleState)
    {
        OriginalEntryKind kind = GetEntryKind(sourcePath);
        return kind switch
        {
            OriginalEntryKind.File => new IntentArtifact(
                transactionId,
                area,
                role,
                relativePath,
                kind,
                _files.GetLength(sourcePath),
                Digest(_files.ReadAllBytes(sourcePath)),
                string.Empty,
                lifecycleState),
            OriginalEntryKind.FileSymbolicLink => new IntentArtifact(
                transactionId,
                area,
                role,
                relativePath,
                kind,
                0,
                string.Empty,
                _files.GetLinkTarget(sourcePath)
                ?? throw new InvalidDataException(
                    $"Squad claim '{relativePath}' lost its symbolic-link target."),
                lifecycleState),
            OriginalEntryKind.DirectorySymbolicLink => new IntentArtifact(
                transactionId,
                area,
                role,
                relativePath,
                kind,
                0,
                string.Empty,
                _files.GetLinkTarget(sourcePath)
                ?? throw new InvalidDataException(
                    $"Squad claim '{relativePath}' lost its symbolic-link target."),
                lifecycleState),
            OriginalEntryKind.Directory => new IntentArtifact(
                transactionId,
                area,
                role,
                relativePath,
                kind,
                0,
                string.Empty,
                string.Empty,
                lifecycleState),
            _ => throw new InvalidDataException(
                $"Squad claim '{relativePath}' has no claimable node.")
        };
    }

    private void MoveEntryNoOverwrite(
        string sourcePath,
        string destinationPath,
        OriginalEntryKind kind)
    {
        if (kind is OriginalEntryKind.Directory or OriginalEntryKind.DirectorySymbolicLink)
            _files.MoveDirectory(sourcePath, destinationPath);
        else
            _files.MoveFile(sourcePath, destinationPath);
    }

    private void RemoveEmptyArtifactParents(string artifactPath, string areaRoot)
    {
        string? current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !_semantics.AreSame(current, areaRoot) &&
               _semantics.IsWithin(areaRoot, current))
        {
            DeleteEmptyDirectory(current);
            if (_files.DirectoryExists(current))
                break;
            current = Path.GetDirectoryName(current);
        }
    }

    private void PublishIntentGeneration(
        string intentPath,
        IntentDocument intent,
        TransactionLease lease)
    {
        lease.ReleaseJournal();
        WriteIntent(intentPath, intent, true);
        lease.HoldJournal(intentPath);
    }

    private void CopyStateOriginal(
        string sourcePath,
        string backupPath,
        string linkMetadataPath,
        OriginalEntryKind originalKind,
        string workDirectory,
        string backupRelativePath,
        string linkRelativePath,
        string transactionId,
        ICollection<IntentArtifact> artifacts)
    {
        if (originalKind != OriginalEntryKind.Missing)
            BackupEntry(
                sourcePath,
                backupPath,
                originalKind,
                linkMetadataPath,
                workDirectory,
                backupRelativePath,
                transactionId,
                artifacts,
                linkRelativePath);
    }

    private void BackupEntry(
        string sourcePath,
        string destinationPath,
        OriginalEntryKind kind,
        string linkMetadataPath,
        string workDirectory,
        string sourceRelativePath,
        string transactionId,
        ICollection<IntentArtifact> artifacts,
        string? linkRelativePath = null)
    {
        if (kind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
        {
            string? linkTarget = kind == OriginalEntryKind.FileSymbolicLink
                ? _files.GetLinkTarget(sourcePath)
                : _files.GetLinkTarget(sourcePath);
            if (linkTarget is null)
                throw new InvalidDataException($"Expected '{sourcePath}' to be a symbolic link.");

            PublishArtifact(
                workDirectory,
                ArtifactArea.Work,
                linkRelativePath is null
                    ? ArtifactRole.TargetLinkMetadata
                    : ArtifactRole.StateLinkMetadata,
                transactionId,
                linkRelativePath ?? $"links/{sourceRelativePath}",
                linkMetadataPath,
                Encoding.UTF8.GetBytes(linkTarget),
                artifacts);
            if (kind == OriginalEntryKind.FileSymbolicLink)
                PublishArtifact(
                    workDirectory,
                    ArtifactArea.Work,
                    sourceRelativePath.StartsWith("state-", StringComparison.Ordinal)
                        ? ArtifactRole.StateOriginal
                        : ArtifactRole.TargetBackup,
                    transactionId,
                    sourceRelativePath.StartsWith("state-", StringComparison.Ordinal)
                        ? sourceRelativePath
                        : $"backups/{sourceRelativePath}",
                    destinationPath,
                    _files.ReadAllBytes(sourcePath),
                    artifacts);

            return;
        }

        if (kind == OriginalEntryKind.File)
        {
            PublishArtifact(
                workDirectory,
                ArtifactArea.Work,
                sourceRelativePath.StartsWith("state-", StringComparison.Ordinal)
                    ? ArtifactRole.StateOriginal
                    : ArtifactRole.TargetBackup,
                transactionId,
                sourceRelativePath.StartsWith("state-", StringComparison.Ordinal)
                    ? sourceRelativePath
                    : $"backups/{sourceRelativePath}",
                destinationPath,
                _files.ReadAllBytes(sourcePath),
                artifacts);
            return;
        }

        CopyEntry(sourcePath, destinationPath, kind);
        if (kind == OriginalEntryKind.Directory)
        {
            string relativePath = sourceRelativePath.StartsWith(
                "state-",
                StringComparison.Ordinal)
                ? sourceRelativePath
                : $"backups/{sourceRelativePath}";
            artifacts.Add(CaptureNodeArtifact(
                transactionId,
                ArtifactArea.Work,
                sourceRelativePath.StartsWith("state-", StringComparison.Ordinal)
                    ? ArtifactRole.StateOriginal
                    : ArtifactRole.TargetBackup,
                relativePath,
                destinationPath,
                ArtifactLifecycleState.PreOperation));
        }
    }

    private void CopyEntry(
        string sourcePath,
        string destinationPath,
        OriginalEntryKind kind)
    {
        _files.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        switch (kind)
        {
            case OriginalEntryKind.File:
                CopyFileNoOverwrite(sourcePath, destinationPath);
                break;
            case OriginalEntryKind.Directory:
                _files.CreateDirectory(destinationPath);
                break;
            case OriginalEntryKind.FileSymbolicLink:
            case OriginalEntryKind.DirectorySymbolicLink:
                throw new InvalidOperationException("Symbolic links require backup metadata.");
            case OriginalEntryKind.Missing:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown entry kind.");
        }
    }

    private OriginalEntryKind GetEntryKind(string path)
    {
        if (_files.GetLinkTarget(path) is not null)
        {
            if (OperatingSystem.IsWindows())
                return (_files.GetAttributes(path) & FileAttributes.Directory) != 0
                    ? OriginalEntryKind.DirectorySymbolicLink
                    : OriginalEntryKind.FileSymbolicLink;

            return _files.DirectoryExists(path)
                ? OriginalEntryKind.DirectorySymbolicLink
                : OriginalEntryKind.FileSymbolicLink;
        }

        if (_files.FileExists(path))
            return OriginalEntryKind.File;

        return _files.DirectoryExists(path)
            ? OriginalEntryKind.Directory
            : OriginalEntryKind.Missing;
    }

    private IReadOnlyList<string> CaptureMissingDirectories(
        string directoryPath,
        string boundaryPath)
    {
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundaryPath));
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        List<string> missing = new();
        while (!_semantics.AreSame(current, boundary) &&
               _semantics.IsWithin(boundary, current))
        {
            if (GetEntryKind(current) != OriginalEntryKind.Missing)
                break;

            missing.Add(current);
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidOperationException(
                          $"Could not inspect the parent of transaction directory '{directoryPath}'.");
        }

        return missing;
    }

    private IReadOnlyList<CreatedDirectoryAuthority> CaptureCreatedDirectoryAuthority(
        IReadOnlyList<string> transactionDirectories,
        IReadOnlyList<string> journalDirectories,
        IReadOnlyList<string> homeDirectories,
        string targetRoot,
        string stateAuthorityRoot,
        string homeBoundary)
    {
        CreatedDirectoryAuthority[] authorities = transactionDirectories.Select(path =>
                new CreatedDirectoryAuthority(
                    CreatedDirectoryArea.Target,
                    RelativeAuthorityPath(targetRoot, path)))
            .Concat(journalDirectories.Select(path =>
                new CreatedDirectoryAuthority(
                    CreatedDirectoryArea.State,
                    RelativeAuthorityPath(stateAuthorityRoot, path))))
            .Concat(homeDirectories.Select(path =>
                new CreatedDirectoryAuthority(
                    CreatedDirectoryArea.Home,
                    RelativeAuthorityPath(homeBoundary, path))))
            .Distinct()
            .OrderByDescending(authority => authority.Path.Count(character => character == '/'))
            .ThenBy(authority => authority.Area)
            .ThenBy(authority => authority.Path, StringComparer.Ordinal)
            .ToArray();
        return authorities;
    }

    /// <summary>
    ///     The canonical physical spelling of a path. The store returns global state beneath the home as it
    ///     was configured, and project state beneath the canonical root. The transaction compares and records
    ///     paths, so it works in one spelling only, and every path is canonicalized before it is used.
    /// </summary>
    private string Physical(string path)
    {
        return _semantics.Canonicalize(path);
    }

    /// <summary>
    ///     Canonicalizes the directory a state file lives in but not the file itself. A state file may be a
    ///     symbolic link, and canonicalizing the leaf would claim the link's target instead of the link, so
    ///     the claim, its link metadata and the restore would all address the wrong node.
    /// </summary>
    private string PhysicalLeaf(string path)
    {
        return Path.Combine(Physical(Path.GetDirectoryName(path)!), Path.GetFileName(path));
    }

    private string RelativeAuthorityPath(string authorityRoot, string path)
    {
        if (!_semantics.IsWithin(authorityRoot, path))
            throw new InvalidOperationException(
                "Squad created-directory authority escaped its bound root.");

        // Canonical on both sides, so a path spelled through a symlinked home relates to its authority
        // the same way the canonical spelling does. The recorded relative path is spelling-independent.
        return SquadPathPolicy.NormalizeRelativePath(
            Path.GetRelativePath(
                    _semantics.Canonicalize(authorityRoot),
                    _semantics.Canonicalize(path))
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private void RemoveCreatedDirectories(IEnumerable<string> directories)
    {
        foreach (string directory in directories)
            DeleteEmptyDirectory(directory);
    }

    private void RemoveCreatedDirectories(
        IEnumerable<CreatedDirectoryAuthority> directories,
        string targetRoot,
        string stateAuthorityRoot,
        string homeBoundary)
    {
        foreach (CreatedDirectoryAuthority authority in directories)
        {
            string root = authority.Area switch
            {
                CreatedDirectoryArea.Target => targetRoot,
                CreatedDirectoryArea.Home => homeBoundary,
                _ => stateAuthorityRoot
            };
            DeleteEmptyDirectory(SquadPathPolicy.ResolveFile(root, authority.Path, _semantics));
        }
    }

    private void DeleteEmptyDirectory(string path)
    {
        if (GetEntryKind(path) != OriginalEntryKind.Directory ||
            _files.EnumerateEntries(path).Any())
            return;

        _files.DeleteEmptyDirectory(path);
    }

    private void DeleteEntry(string path, bool recursiveDirectory)
    {
        switch (GetEntryKind(path))
        {
            case OriginalEntryKind.Missing:
                return;
            case OriginalEntryKind.File:
            case OriginalEntryKind.FileSymbolicLink:
                _files.DeleteFile(path);
                return;
            case OriginalEntryKind.DirectorySymbolicLink:
                _files.DeleteTreeNoFollow(path);
                return;
            case OriginalEntryKind.Directory when recursiveDirectory:
                DeleteDirectoryTree(path);
                return;
            case OriginalEntryKind.Directory:
                throw new IOException($"Expected file path '{path}' is a directory.");
            default:
                throw new InvalidOperationException($"Unknown filesystem entry at '{path}'.");
        }
    }

    private void DeleteDirectoryTree(string path)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(path))
        {
            if (entry.IsLink)
                _files.DeleteTreeNoFollow(entry.Path);
            else if (entry.IsDirectory)
                DeleteDirectoryTree(entry.Path);
            else
                _files.DeleteFile(entry.Path);
        }

        _files.DeleteEmptyDirectory(path);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A successful TransactionLease takes ownership of the lease; a refused lease holds nothing.")]
    private TransactionLease AcquireLease(string rootKey)
    {
        IDisposable held;
        try
        {
            held = _lease.Acquire(SquadLeaseNames.ForRoot(rootKey), TimeSpan.Zero);
        }
        catch (LeaseTimeoutException exception)
        {
            throw new InvalidOperationException(LeaseBusyMessage, exception);
        }

        return new TransactionLease(held, _files);
    }

    private void CleanupOwnedArtifacts(
        string journalDirectory,
        string workDirectory,
        bool keepIntent = false)
    {
        if (!_semantics.AreSame(journalDirectory, workDirectory) &&
            _files.DirectoryExists(workDirectory))
            DeleteDirectoryTree(workDirectory);

        if (!_files.DirectoryExists(journalDirectory))
            return;

        // The journal goes last: a kill in between leaves a journal that still names the transaction, so recovery
        // finishes the cleanup instead of meeting leftovers with no journal.
        string? intentEntry = null;
        foreach (FileSystemEntry entry in _files.EnumerateEntries(journalDirectory).ToArray())
            if (!entry.IsLink && !entry.IsDirectory &&
                string.Equals(Path.GetFileName(entry.Path), IntentFileName, StringComparison.Ordinal))
                intentEntry = entry.Path;
            else if (entry.IsLink)
                _files.DeleteTreeNoFollow(entry.Path);
            else if (entry.IsDirectory)
                DeleteDirectoryTree(entry.Path);
            else
                _files.DeleteFile(entry.Path);

        if (intentEntry is not null && !keepIntent)
            _files.DeleteFile(intentEntry);
    }

    /// <summary>
    ///     Removes the journal and the folders this transaction made for it. When the journal is all that is left, the
    ///     topmost folder it made goes in one step, intent included: a kill then leaves either the whole committed
    ///     journal or nothing, never a bare folder that nothing explains.
    /// </summary>
    private void RemoveJournal(string journalDirectory, IReadOnlyList<string> journalDirectoriesCreated)
    {
        string intentPath = Path.Combine(journalDirectory, IntentFileName);
        string? top = journalDirectoriesCreated.Count > 0 ? journalDirectoriesCreated[^1] : null;
        if (top is not null &&
            (_semantics.AreSame(top, journalDirectory) || _semantics.IsWithin(top, journalDirectory)) &&
            GetEntryKind(top) == OriginalEntryKind.Directory &&
            ContainsNothingBut(top, intentPath))
        {
            _files.DeleteTreeNoFollow(top);
            return;
        }

        if (_files.FileExists(intentPath))
            _files.DeleteFile(intentPath);
        DeleteEmptyDirectory(journalDirectory);
    }

    private bool ContainsNothingBut(string directory, string intentPath)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
            if (entry.IsLink ||
                (entry.IsDirectory
                    ? !ContainsNothingBut(entry.Path, intentPath)
                    : !_semantics.AreSame(entry.Path, intentPath)))
                return false;

        return true;
    }

    /// <summary>
    ///     Records that the transaction is decided and only cleanup is left, so a kill while the backups and the
    ///     staging area are being deleted is finished forward instead of refused for the pieces already gone.
    /// </summary>
    private void MarkCleanupOnly(string intentPath, IntentDocument intent) =>
        WriteIntent(
            intentPath,
            intent with
            {
                Phase = TransactionPhase.Committed,
                Artifacts = Array.Empty<IntentArtifact>(),
                ActiveTransitions = Array.Empty<IntentTransition>()
            },
            true);

    private void WriteIntent(
        string intentPath,
        IntentDocument intent,
        bool overwrite)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(intent, IntentJsonOptions) + "\n");
        PublishFileAtomic(intentPath, bytes, overwrite);
    }

    private void PublishArtifact(
        string areaRoot,
        ArtifactArea area,
        ArtifactRole role,
        string transactionId,
        string relativePath,
        string artifactPath,
        ReadOnlySpan<byte> content,
        ICollection<IntentArtifact> artifacts)
    {
        string normalizedPath = SquadPathPolicy.NormalizeRelativePath(relativePath);
        string resolvedPath = SquadPathPolicy.ResolveFile(areaRoot, normalizedPath, _semantics);
        if (!_semantics.AreSame(resolvedPath, artifactPath))
            throw new InvalidOperationException(
                $"Squad transaction artifact '{relativePath}' resolved inconsistently.");

        byte[] bytes = content.ToArray();
        PublishFileAtomic(artifactPath, bytes, false);
        artifacts.Add(new IntentArtifact(
            transactionId,
            area,
            role,
            normalizedPath,
            OriginalEntryKind.File,
            bytes.LongLength,
            Digest(bytes),
            string.Empty,
            ArtifactLifecycleState.PreOperation));
    }

    private void DiscardUnpublishedIntentTemporaries(string journalDirectory)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(journalDirectory))
        {
            if (!entry.IsDirectory && !entry.IsLink && IsIntentTemporary(entry.Path))
                _files.DeleteFile(entry.Path);
        }
    }

    private bool HasAnyFileOtherThanIntentTemporaries(string directory)
    {
        if (!_files.DirectoryExists(directory))
            return false;

        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
            if (entry.IsLink ||
                (entry.IsDirectory ? HasAnyFile(entry.Path) : !IsIntentTemporary(entry.Path)))
                return true;

        return false;
    }

    private static bool IsIntentTemporary(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(IntentFileName + ".", StringComparison.Ordinal) &&
               name.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private bool HasAnyFile(string directory)
    {
        if (!_files.DirectoryExists(directory))
            return false;

        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
            if (entry.IsLink || !entry.IsDirectory || HasAnyFile(entry.Path))
                return true;

        return false;
    }

    private void RemoveEmptyDirectoriesUpTo(string directory, string boundary)
    {
        string? current = directory;
        while (current is not null &&
               !_semantics.AreSame(current, boundary) &&
               _semantics.IsWithin(boundary, current))
        {
            DeleteEmptyDirectory(current);
            if (_files.DirectoryExists(current))
                break;
            current = Path.GetDirectoryName(current);
        }
    }

    private void WriteNewFile(string path, ReadOnlySpan<byte> content)
    {
        using Stream stream = _files.OpenWriteNew(path);
        stream.Write(content);
        if (!SyncToDisk)
            return;

        if (stream is FileStream disk)
            disk.Flush(true);
        else
            stream.Flush();
    }

    private void CopyFileNoOverwrite(string sourcePath, string destinationPath) =>
        WriteNewFile(destinationPath, _files.ReadAllBytes(sourcePath));

    private IEnumerable<string> EnumerateAllEntries(string directory)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
        {
            yield return entry.Path;
            if (entry.IsDirectory && !entry.IsLink)
                foreach (string child in EnumerateAllEntries(entry.Path))
                    yield return child;
        }
    }

    private void PublishFileAtomic(
        string destinationPath,
        ReadOnlySpan<byte> content,
        bool overwrite)
    {
        _files.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string temporaryPath = destinationPath + $".{_ids.NewGuid():N}.tmp";
        try
        {
            WriteNewFile(temporaryPath, content);

            _files.MoveFile(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            if (_files.FileExists(temporaryPath))
                _files.DeleteFile(temporaryPath);
        }
    }

    private IntentDocument ReadIntent(
        string intentPath,
        string journalDirectory,
        string workDirectory,
        string expectedRootKey,
        string targetRoot,
        string stateAuthorityRoot,
        string homeBoundary,
        string homeStateDirectory)
    {
        try
        {
            string json = _files.ReadAllText(intentPath);
            ValidateIntentJsonShape(json);
            IntentDocument intent = JsonSerializer.Deserialize<IntentDocument>(json, IntentJsonOptions)
                                    ?? throw new InvalidDataException("Squad transaction intent is empty.");
            if (!string.Equals(intent.Schema, IntentSchema, StringComparison.Ordinal) ||
                !string.Equals(intent.RootKey, expectedRootKey, StringComparison.Ordinal) ||
                !IsDigest(intent.RootKey) ||
                !IsTransactionId(intent.TransactionId))
                throw new InvalidDataException(
                    "Squad transaction intent has an unsupported schema or physical-root binding.");

            HashSet<string> fileIdentities = new(StringComparer.OrdinalIgnoreCase);
            foreach (IntentFile file in intent.Files)
            {
                string normalized = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
                if (!string.Equals(normalized, file.RelativePath, StringComparison.Ordinal) ||
                    !fileIdentities.Add(normalized))
                    throw new InvalidDataException(
                        "Squad transaction intent contains a non-portable path.");

                foreach (string directory in file.MissingParentDirectories)
                    _ = SquadPathPolicy.NormalizeRelativePath(directory);
                ValidateAfterDigest(file.AfterKind, file.AfterSha256, "file");
            }

            ValidateAfterDigest(intent.LockAfterKind, intent.LockAfterSha256, "lock");
            ValidateAfterDigest(intent.ReceiptAfterKind, intent.ReceiptAfterSha256, "receipt");
            ValidateCreatedDirectoryAuthority(
                intent.CreatedDirectories,
                targetRoot,
                stateAuthorityRoot,
                homeBoundary,
                homeStateDirectory,
                journalDirectory,
                workDirectory);
            if (intent.Phase == TransactionPhase.Prepared)
            {
                ValidateSemanticArtifactSet(intent, journalDirectory, workDirectory);
                VerifyArtifacts(
                    intent.TransactionId,
                    intent.Artifacts,
                    intent.ActiveTransitions,
                    journalDirectory,
                    workDirectory);
            }
            else if (intent.Artifacts.Count != 0 || intent.ActiveTransitions.Count != 0)
            {
                throw new InvalidDataException(
                    "Squad preparing or committed journal cannot claim published artifacts.");
            }

            return intent;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException or
                SquadDeploymentConflictException or SquadPathContainmentException)
        {
            throw new InvalidDataException(
                $"Squad transaction intent is invalid: {exception.Message}",
                exception);
        }
    }

    private void ValidateIntentJsonShape(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        RequireExactJsonFields(
            document.RootElement,
            [
                "schema",
                "phase",
                "rootKey",
                "transactionId",
                "files",
                "createdDirectories",
                "lockKind",
                "lockAfterKind",
                "lockAfterSha256",
                "receiptKind",
                "receiptAfterKind",
                "receiptAfterSha256",
                "artifacts",
                "activeTransitions"
            ],
            "transaction intent");
        RequireCanonicalJsonEnum(
            document.RootElement,
            "phase",
            ["preparing", "prepared", "committed"],
            "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "lockKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "lockAfterKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "receiptKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "receiptAfterKind", "transaction intent");

        JsonElement files = document.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction intent files must be an array.");
        foreach (JsonElement file in files.EnumerateArray())
        {
            RequireExactJsonFields(
                file,
                [
                    "relativePath",
                    "target",
                    "physicalRoot",
                    "originalKind",
                    "missingParentDirectories",
                    "afterKind",
                    "afterSha256"
                ],
                "transaction file");
            RequireCanonicalNodeKind(file, "originalKind", "transaction file");
            RequireCanonicalNodeKind(file, "afterKind", "transaction file");
        }

        JsonElement createdDirectories = document.RootElement.GetProperty("createdDirectories");
        if (createdDirectories.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "Squad transaction created directories must be an array.");

        foreach (JsonElement directory in createdDirectories.EnumerateArray())
        {
            RequireExactJsonFields(
                directory,
                ["area", "path"],
                "transaction created directory");
            RequireCanonicalJsonEnum(
                directory,
                "area",
                ["target", "state", "home"],
                "transaction created directory");
        }

        JsonElement artifacts = document.RootElement.GetProperty("artifacts");
        if (artifacts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction artifacts must be an array.");
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            RequireExactJsonFields(
                artifact,
                [
                    "transactionId",
                    "area",
                    "role",
                    "path",
                    "nodeKind",
                    "byteLength",
                    "sha256",
                    "linkTarget",
                    "lifecycleState"
                ],
                "transaction artifact");
            RequireCanonicalJsonEnum(
                artifact,
                "area",
                ["journal", "work"],
                "transaction artifact");
            RequireCanonicalJsonEnum(
                artifact,
                "role",
                [
                    "target-stage",
                    "target-backup",
                    "target-link-metadata",
                    "state-stage",
                    "state-original",
                    "state-link-metadata",
                    "claimed-original",
                    "transition-metadata"
                ],
                "transaction artifact");
            RequireCanonicalNodeKind(artifact, "nodeKind", "transaction artifact");
            RequireCanonicalJsonEnum(
                artifact,
                "lifecycleState",
                ["pre-operation", "active-transition", "post-operation"],
                "transaction artifact");
        }

        JsonElement activeTransitions = document.RootElement.GetProperty("activeTransitions");
        if (activeTransitions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction active transitions must be an array.");
        foreach (JsonElement transition in activeTransitions.EnumerateArray())
        {
            RequireExactJsonFields(
                transition,
                ["artifactIdentity", "allowedState"],
                "transaction active transition");
            RequireCanonicalJsonEnum(
                transition,
                "allowedState",
                ["pre-operation", "active-transition", "post-operation"],
                "transaction active transition");
        }
    }

    private void RequireCanonicalNodeKind(
        JsonElement element,
        string propertyName,
        string subject)
    {
        RequireCanonicalJsonEnum(
            element,
            propertyName,
            ["missing", "file", "directory", "fileSymbolicLink", "directorySymbolicLink"],
            subject);
    }

    private void RequireCanonicalJsonEnum(
        JsonElement element,
        string propertyName,
        IReadOnlyCollection<string> allowedValues,
        string subject)
    {
        JsonElement value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } token ||
            !allowedValues.Contains(token))
            throw new InvalidDataException(
                $"Squad {subject} field '{propertyName}' must use its canonical enum token.");
    }

    private void RequireExactJsonFields(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string subject)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Squad {subject} must be an object.");

        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
                throw new InvalidDataException(
                    $"Squad {subject} contains duplicate field '{property.Name}'.");

            if (property.Value.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException(
                    $"Squad {subject} field '{property.Name}' cannot be null.");
        }

        string[] missing = expected.Where(field => !actual.Contains(field)).ToArray();
        string[] unknown = actual.Where(field => !expected.Contains(field)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
            throw new InvalidDataException(
                $"Squad {subject} has missing or unknown fields. " +
                $"Missing: {string.Join(", ", missing)}; unknown: {string.Join(", ", unknown)}.");
    }

    private void ValidateAfterDigest(
        OriginalEntryKind kind,
        string digest,
        string subject)
    {
        if (kind == OriginalEntryKind.File)
        {
            if (!IsDigest(digest))
                throw new InvalidDataException(
                    $"Squad transaction {subject} after-image digest is invalid.");
        }
        else if (digest.Length != 0)
        {
            throw new InvalidDataException(
                $"Squad transaction {subject} non-file after-image has a digest.");
        }
    }

    private void ValidateSemanticArtifactSet(
        IntentDocument intent,
        string journalDirectory,
        string workDirectory)
    {
        HashSet<string> expected = new(StringComparer.Ordinal);
        foreach (IntentFile file in intent.Files)
        {
            if (file.AfterKind == OriginalEntryKind.File)
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    TargetWorkRelative("staging", file.Target, file.RelativePath)));

            if (file.OriginalKind is OriginalEntryKind.File or
                OriginalEntryKind.Directory or
                OriginalEntryKind.FileSymbolicLink)
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetBackup,
                    TargetWorkRelative("backups", file.Target, file.RelativePath)));

            if (file.OriginalKind is OriginalEntryKind.FileSymbolicLink or
                OriginalEntryKind.DirectorySymbolicLink)
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetLinkMetadata,
                    TargetWorkRelative("links", file.Target, file.RelativePath)));
        }

        AddExpectedStateArtifacts(
            expected,
            "lock",
            intent.LockKind,
            intent.LockAfterKind,
            intent.LockAfterSha256,
            workDirectory,
            intent.Artifacts);
        AddExpectedStateArtifacts(
            expected,
            "receipt",
            intent.ReceiptKind,
            intent.ReceiptAfterKind,
            intent.ReceiptAfterSha256,
            workDirectory,
            intent.Artifacts);

        HashSet<string> optionalClaims = new(StringComparer.Ordinal);
        foreach (IntentFile file in intent.Files)
            optionalClaims.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.ClaimedOriginal,
                ClaimRelativePath(intent.TransactionId, FileKey(file.Target, file.RelativePath))));

        ArtifactArea stateClaimArea = _semantics.AreSame(
            journalDirectory,
            workDirectory)
            ? ArtifactArea.Work
            : ArtifactArea.Journal;
        optionalClaims.Add(SemanticArtifactIdentity(
            stateClaimArea,
            ArtifactRole.ClaimedOriginal,
            ClaimRelativePath(intent.TransactionId, "squad.lock.yml")));
        optionalClaims.Add(SemanticArtifactIdentity(
            stateClaimArea,
            ArtifactRole.ClaimedOriginal,
            ClaimRelativePath(intent.TransactionId, "squad.receipt.json")));

        foreach (IntentArtifact artifact in intent.Artifacts)
        {
            string identity = SemanticArtifactIdentity(
                artifact.Area,
                artifact.Role,
                artifact.Path);
            if (expected.Remove(identity))
                continue;
            if (artifact.Role == ArtifactRole.ClaimedOriginal &&
                artifact.LifecycleState != ArtifactLifecycleState.PreOperation &&
                optionalClaims.Remove(identity))
                continue;

            throw new InvalidDataException(
                $"Squad artifact '{artifact.Path}' is not required by transaction semantics.");
        }

        if (expected.Count != 0)
            throw new InvalidDataException(
                "Squad transaction is missing artifacts required by its mutation semantics.");
    }

    private void AddExpectedStateArtifacts(
        ISet<string> expected,
        string stateName,
        OriginalEntryKind originalKind,
        OriginalEntryKind afterKind,
        string afterSha256,
        string workDirectory,
        IReadOnlyList<IntentArtifact> artifacts)
    {
        string originalPath = StateOriginalPath(workDirectory, stateName);
        bool unchangedFile = originalKind == OriginalEntryKind.File &&
                             afterKind == OriginalEntryKind.File &&
                             GetEntryKind(originalPath) == OriginalEntryKind.File &&
                             string.Equals(
                                 Digest(_files.ReadAllBytes(originalPath)),
                                 afterSha256,
                                 StringComparison.Ordinal);

        bool hasStagedArtifact = artifacts.Any(a =>
            a is { Area: ArtifactArea.Journal, Role: ArtifactRole.StateStage } &&
            string.Equals(a.Path, $"state-staging/{stateName}", StringComparison.Ordinal));

        if (afterKind == OriginalEntryKind.File && (hasStagedArtifact || !unchangedFile))
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Journal,
                ArtifactRole.StateStage,
                $"state-staging/{stateName}"));

        if (originalKind is OriginalEntryKind.File or
            OriginalEntryKind.Directory or
            OriginalEntryKind.FileSymbolicLink)
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.StateOriginal,
                $"state-originals/{stateName}"));

        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.StateLinkMetadata,
                $"state-original-links/{stateName}"));
    }

    private string SemanticArtifactIdentity(
        ArtifactArea area,
        ArtifactRole role,
        string relativePath)
    {
        return $"{area}:{role}:{relativePath}";
    }

    private string ClaimRelativePath(string transactionId, string subject)
    {
        return $"claimed-{transactionId}-{Digest(Encoding.UTF8.GetBytes(subject))}";
    }

    private void VerifyArtifacts(
        string transactionId,
        IReadOnlyList<IntentArtifact> artifacts,
        IReadOnlyList<IntentTransition> activeTransitions,
        string journalDirectory,
        string workDirectory)
    {
        if (artifacts.Count == 0)
            throw new InvalidDataException("Squad prepared transaction has no verified artifacts.");

        if (ContainsTemporaryArtifact(journalDirectory) ||
            (!_semantics.AreSame(journalDirectory, workDirectory) &&
             ContainsTemporaryArtifact(workDirectory)))
            throw new InvalidDataException(
                "Squad prepared transaction contains an unpublished temporary artifact.");

        HashSet<string> identities = new(StringComparer.Ordinal);
        Dictionary<string, IntentTransition> transitions = new(StringComparer.Ordinal);
        foreach (IntentTransition transition in activeTransitions)
            if (string.IsNullOrWhiteSpace(transition.ArtifactIdentity) ||
                transition.AllowedState != ArtifactLifecycleState.PreOperation ||
                !transitions.TryAdd(transition.ArtifactIdentity, transition))
                throw new InvalidDataException(
                    "Squad prepared transaction contains an invalid active transition.");

        if (transitions.Count > 1)
            throw new InvalidDataException(
                "Squad prepared transaction contains duplicate or multiple active transitions.");

        foreach (IntentArtifact artifact in artifacts)
        {
            if (!string.Equals(artifact.TransactionId, transactionId, StringComparison.Ordinal) ||
                !ValidateArtifactFingerprint(artifact) ||
                !ArtifactRoleMatchesPath(artifact))
                throw new InvalidDataException(
                    "Squad prepared transaction contains invalid artifact metadata.");

            string relativePath = SquadPathPolicy.NormalizeRelativePath(artifact.Path);
            string identity = ArtifactIdentity(artifact.Area, relativePath);
            if (!string.Equals(relativePath, artifact.Path, StringComparison.Ordinal) ||
                !identities.Add(identity))
                throw new InvalidDataException(
                    "Squad prepared transaction contains duplicate or non-portable artifacts.");

            string areaRoot = artifact.Area == ArtifactArea.Journal
                ? journalDirectory
                : workDirectory;
            string path = ResolveArtifactNode(areaRoot, relativePath);
            OriginalEntryKind currentKind = GetEntryKind(path);
            bool mayBeAbsent = artifact.LifecycleState == ArtifactLifecycleState.ActiveTransition;
            if (artifact.LifecycleState == ArtifactLifecycleState.ActiveTransition)
            {
                if (!transitions.TryGetValue(identity, out IntentTransition? transition) ||
                    transition.AllowedState != ArtifactLifecycleState.PreOperation)
                    throw new InvalidDataException(
                        "Squad prepared transaction has an undeclared active transition.");
            }
            else if (transitions.ContainsKey(identity))
            {
                throw new InvalidDataException(
                    "Squad prepared transaction transition does not match its artifact lifecycle.");
            }

            bool? expectedPresent = ArtifactExpectedPresent(artifact);
            if (currentKind == OriginalEntryKind.Missing && mayBeAbsent)
                continue;
            if (expectedPresent == false && currentKind != OriginalEntryKind.Missing)
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' has an invalid lifecycle state.");

            if (expectedPresent == false)
                continue;
            if (currentKind != artifact.NodeKind)
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' is missing or has the wrong node kind.");

            if (!ArtifactFingerprintMatches(path, artifact))
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' failed length or digest verification.");
        }

        if (transitions.Keys.Any(identity => !identities.Contains(identity)))
            throw new InvalidDataException(
                "Squad prepared transaction transition names an unknown artifact.");

        VerifyExactTransactionTree(artifacts, journalDirectory, workDirectory);
    }

    private bool ValidateArtifactFingerprint(IntentArtifact artifact)
    {
        return artifact.NodeKind switch
        {
            OriginalEntryKind.File => artifact.ByteLength >= 0 &&
                                      IsDigest(artifact.Sha256) && artifact.LinkTarget.Length == 0,
            OriginalEntryKind.FileSymbolicLink or OriginalEntryKind.DirectorySymbolicLink =>
                artifact is { ByteLength: 0, Sha256.Length: 0, LinkTarget.Length: > 0 },
            OriginalEntryKind.Directory =>
                artifact is { ByteLength: 0, Sha256.Length: 0, LinkTarget.Length: 0 },
            _ => false
        };
    }

    private bool ArtifactFingerprintMatches(string path, IntentArtifact artifact)
    {
        return artifact.NodeKind switch
        {
            OriginalEntryKind.File =>
                _files.GetLength(path) == artifact.ByteLength &&
                string.Equals(
                    Digest(_files.ReadAllBytes(path)),
                    artifact.Sha256,
                    StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink => string.Equals(
                _files.GetLinkTarget(path),
                artifact.LinkTarget,
                StringComparison.Ordinal),
            OriginalEntryKind.DirectorySymbolicLink => string.Equals(
                _files.GetLinkTarget(path),
                artifact.LinkTarget,
                StringComparison.Ordinal),
            OriginalEntryKind.Directory => true,
            _ => false
        };
    }

    private bool? ArtifactExpectedPresent(IntentArtifact artifact)
    {
        return artifact.LifecycleState switch
        {
            ArtifactLifecycleState.PreOperation => true,
            ArtifactLifecycleState.ActiveTransition => null,
            ArtifactLifecycleState.PostOperation =>
                artifact.Role == ArtifactRole.ClaimedOriginal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifact), artifact.LifecycleState, "Unknown artifact lifecycle.")
        };
    }

    private string ArtifactIdentity(ArtifactArea area, string relativePath)
    {
        return $"{area}:{relativePath}";
    }

    private bool ArtifactRoleMatchesPath(IntentArtifact artifact)
    {
        if (artifact.Role == ArtifactRole.ClaimedOriginal)
            return artifact.Path.StartsWith("claimed-", StringComparison.Ordinal) &&
                   artifact.Path.Length > "claimed-".Length;

        (ArtifactArea, string) expected = artifact.Role switch
        {
            ArtifactRole.TargetStage => (ArtifactArea.Work, "staging/"),
            ArtifactRole.TargetBackup => (ArtifactArea.Work, "backups/"),
            ArtifactRole.TargetLinkMetadata => (ArtifactArea.Work, "links/"),
            ArtifactRole.StateStage => (ArtifactArea.Journal, "state-staging/"),
            ArtifactRole.StateOriginal => (ArtifactArea.Work, "state-originals/"),
            ArtifactRole.StateLinkMetadata => (ArtifactArea.Work, "state-original-links/"),
            ArtifactRole.TransitionMetadata => (ArtifactArea.Journal, "transitions/"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifact), artifact.Role, "Unknown Squad artifact role.")
        };
        return artifact.Area == expected.Item1 &&
               artifact.Path.StartsWith(expected.Item2, StringComparison.Ordinal) &&
               artifact.Path.Length > expected.Item2.Length;
    }

    private void VerifyExactTransactionTree(
        IReadOnlyList<IntentArtifact> artifacts,
        string journalDirectory,
        string workDirectory)
    {
        if (_semantics.AreSame(journalDirectory, workDirectory))
        {
            VerifyExactTransactionTreeArea(
                workDirectory,
                artifacts,
                true);
        }
        else
        {
            VerifyExactTransactionTreeArea(
                journalDirectory,
                artifacts.Where(artifact => artifact.Area == ArtifactArea.Journal),
                true);
            VerifyExactTransactionTreeArea(
                workDirectory,
                artifacts.Where(artifact => artifact.Area == ArtifactArea.Work),
                false);
        }
    }

    private void ValidateCreatedDirectoryAuthority(
        IReadOnlyList<CreatedDirectoryAuthority> authorities,
        string targetRoot,
        string stateAuthorityRoot,
        string homeBoundary,
        string homeStateDirectory,
        string journalDirectory,
        string workDirectory)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        int previousDepth = int.MaxValue;
        foreach (CreatedDirectoryAuthority authority in authorities)
        {
            string normalized = SquadPathPolicy.NormalizeRelativePath(authority.Path);
            int depth = normalized.Count(character => character == '/');
            if (!string.Equals(normalized, authority.Path, StringComparison.Ordinal) ||
                !identities.Add($"{authority.Area}:{normalized}") ||
                depth > previousDepth)
                throw new InvalidDataException(
                    "Squad created-directory authority is duplicate, unordered, or non-portable.");

            previousDepth = depth;
            string authorityRoot = authority.Area switch
            {
                CreatedDirectoryArea.Target => targetRoot,
                CreatedDirectoryArea.Home => homeBoundary,
                _ => stateAuthorityRoot
            };
            string directory = SquadPathPolicy.ResolveFile(authorityRoot, normalized, _semantics);
            bool anchored = authority.Area == CreatedDirectoryArea.Home
                // Home state directories are the ancestors of, or within, the home state tree. A
                // journal is never under the home state tree, so it cannot be the anchor for these.
                ? _semantics.AreSame(directory, homeStateDirectory) ||
                  _semantics.IsWithin(directory, homeStateDirectory) ||
                  _semantics.IsWithin(homeStateDirectory, directory)
                : _semantics.AreSame(
                      directory,
                      authority.Area == CreatedDirectoryArea.Target ? workDirectory : journalDirectory) ||
                  _semantics.IsWithin(
                      directory,
                      authority.Area == CreatedDirectoryArea.Target ? workDirectory : journalDirectory);
            if (!anchored)
                throw new InvalidDataException(
                    "Squad created-directory authority is not an ancestor of its transaction tree.");
        }
    }

    private void VerifyExactTransactionTreeArea(
        string root,
        IEnumerable<IntentArtifact> artifacts,
        bool excludeIntent)
    {
        IntentArtifact[] areaArtifacts = artifacts.ToArray();
        HashSet<string> actualFiles = new(StringComparer.Ordinal);
        HashSet<string> actualDirectories = new(StringComparer.Ordinal);
        EnumerateTransactionTree(root, root, actualFiles, actualDirectories);
        if (excludeIntent)
            actualFiles.Remove(IntentFileName);

        HashSet<string> expectedFiles = areaArtifacts.Where(artifact =>
                artifact.NodeKind != OriginalEntryKind.Directory &&
                (ArtifactExpectedPresent(artifact) == true ||
                 (ArtifactExpectedPresent(artifact) is null &&
                  actualFiles.Contains(artifact.Path))))
            .Select(artifact => artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (IntentArtifact artifact in areaArtifacts
                     .Where(artifact =>
                         ArtifactExpectedPresent(artifact) == true ||
                         (ArtifactExpectedPresent(artifact) is null &&
                          (actualFiles.Contains(artifact.Path) ||
                           actualDirectories.Contains(artifact.Path)))))
        {
            string path = artifact.Path;
            if (artifact.NodeKind == OriginalEntryKind.Directory)
                expectedDirectories.Add(path);
            string? parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                expectedDirectories.Add(parent.Replace(Path.DirectorySeparatorChar, '/'));
                parent = Path.GetDirectoryName(parent);
            }
        }

        // A kill between moving a staged file out and removing its now-empty folders leaves folders no artifact
        // describes. They hold nothing, so they are not evidence of anything and recovery removes them.
        actualDirectories.RemoveWhere(directory =>
            !expectedDirectories.Contains(directory) &&
            !actualFiles.Any(file => file.StartsWith(directory + "/", StringComparison.Ordinal)) &&
            !actualDirectories.Any(other => other.StartsWith(directory + "/", StringComparison.Ordinal) &&
                                            expectedDirectories.Contains(other)));
        if (!actualFiles.SetEquals(expectedFiles) ||
            !actualDirectories.SetEquals(expectedDirectories))
            throw new InvalidDataException(
                "Squad prepared artifact authority does not exactly describe its transaction tree.");
    }

    private void EnumerateTransactionTree(
        string root,
        string directory,
        ISet<string> files,
        ISet<string> directories)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
        {
            string relativePath = Path.GetRelativePath(root, entry.Path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.IsLink || !entry.IsDirectory)
            {
                files.Add(relativePath);
                continue;
            }

            directories.Add(relativePath);
            EnumerateTransactionTree(root, entry.Path, files, directories);
        }
    }

    private bool IsTransactionId(string? value)
    {
        return value is { Length: 32 } &&
               value.All(character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private string ResolveArtifactNode(string areaRoot, string relativePath)
    {
        string normalized = SquadPathPolicy.NormalizeRelativePath(relativePath);
        string fullPath = Path.GetFullPath(Path.Combine(
            areaRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        string? parentRelativePath = Path.GetDirectoryName(
            normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parentRelativePath))
            _ = SquadPathPolicy.ResolveFile(
                areaRoot,
                Path.Combine(parentRelativePath, ".authority-sentinel")
                    .Replace(Path.DirectorySeparatorChar, '/'), _semantics);

        return fullPath;
    }

    private bool ContainsTemporaryArtifact(string directory)
    {
        return _files.DirectoryExists(directory) &&
               EnumerateAllEntries(directory)
                   .Any(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsDigest(string? digest)
    {
        return digest is { Length: 64 } &&
               digest.All(character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private string StateOriginalPath(string workDirectory, string name)
    {
        return Path.Combine(workDirectory, "state-originals", name);
    }

    private string StateOriginalLinkPath(string workDirectory, string name)
    {
        return Path.Combine(workDirectory, "state-original-links", name);
    }

    private string StateStagingPath(string journalDirectory, string name)
    {
        return Path.Combine(journalDirectory, "state-staging", name);
    }

    private string TransactionFilePath(
        string workDirectory,
        string category,
        string relativePath)
    {
        string categoryRoot = Path.Combine(workDirectory, category);
        return SquadPathPolicy.ResolveFile(categoryRoot, relativePath, _semantics);
    }

    /// <summary>
    ///     Work-tree artifact path qualified by harness target so two Global-scope files that
    ///     share a relative path (Claude and Pi both emit <c>agents/architect.md</c>) do not
    ///     collide under staging, backups, or links.
    /// </summary>
    private string TargetWorkRelative(string category, string target, string relativePath)
    {
        return $"{category}/{target}/{relativePath}";
    }

    private string FileKey(string target, string relativePath)
    {
        return SquadDeploymentPlan.DeployedFileIdentity(target, relativePath);
    }

    private string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private SquadDeploymentConflictException PreconditionConflict(string relativePath)
    {
        return new SquadDeploymentConflictException(
            $"Squad deployment path '{relativePath}' changed after preflight. " +
            "Retry the operation so ownership can be checked against the current filesystem state.");
    }

    private LeafClaimConflictException ClaimConflict(string relativePath)
    {
        return new LeafClaimConflictException(
            $"Squad deployment leaf '{relativePath}' changed at the atomic claim boundary. " +
            "The raced node was preserved and the transaction journal was retained.");
    }

    private static JsonSerializerOptions CreateIntentJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NewLine = "\n",
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            false));
        return options;
    }

    /// <summary>
    ///     Keeps the home-level project registry in step with a committed project transaction. A registry
    ///     failure never fails the deployment: the transaction is already durable, and the registry can be
    ///     rebuilt from per-project state. The warning names the repair so the operator is not left guessing.
    /// </summary>
    private void RecordProjectRegistration(SquadDeploymentPlan plan, string stateDirectory)
    {
        if (plan.Scope != SquadDeploymentScope.Project)
            return;

        try
        {
            // The registry is shared by every project of this user, so the project lease the transaction holds does
            // not protect it: two projects committing at once would each read the old registry and the later write
            // would drop the earlier entry. A lease of its own makes the read-modify-write one step.
            using (_lease.Acquire(
                       SquadLeaseNames.ForRegistry(_stateStore.ResolveHomeStateDirectory()),
                       RegistryLeaseTimeout))
            {
                if (plan.ReceiptMutation == SquadStateMutation.Delete)
                {
                    _stateStore.UnregisterProject(plan.PhysicalRootPath);
                }
                else
                {
                    _stateStore.RegisterProject(
                        plan.PhysicalRootPath,
                        plan.Receipt.Files.Select(file => file.Target).Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal).ToArray(),
                        plan.Receipt.InstalledAtUtc);
                }
            }

            if (plan.ReceiptMutation == SquadStateMutation.Delete)
                DeleteEmptyDirectory(stateDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                or LeaseTimeoutException)
        {
            _warnings.Add(
                $"The Squad project registry was not updated for '{plan.PhysicalRootPath}' " +
                $"({exception.Message}). The deployment itself is committed. " +
                "Run `squad status --global` to rebuild the registry.");
        }
    }

    private void Notify(
        ref int sequence,
        SquadTransactionStepKind kind,
        string? relativePath = null)
    {
        sequence++;
        _observer?.AfterStep(new SquadTransactionStep(sequence, kind, relativePath));
    }

    private void NotifyCheckpoint(
        ref int sequence,
        ISquadTransactionObserver? observer,
        SquadTransactionCheckpointKind kind,
        string? relativePath = null)
    {
        sequence++;
        (observer as ISquadTransactionCheckpointObserver)?.AfterCheckpoint(
            new SquadTransactionCheckpoint(sequence, kind, relativePath));
    }

    private sealed record IntentDocument(
        string Schema,
        TransactionPhase Phase,
        string RootKey,
        string TransactionId,
        IReadOnlyList<IntentFile> Files,
        IReadOnlyList<CreatedDirectoryAuthority> CreatedDirectories,
        OriginalEntryKind LockKind,
        OriginalEntryKind LockAfterKind,
        string LockAfterSha256,
        OriginalEntryKind ReceiptKind,
        OriginalEntryKind ReceiptAfterKind,
        string ReceiptAfterSha256,
        IReadOnlyList<IntentArtifact> Artifacts,
        IReadOnlyList<IntentTransition> ActiveTransitions);

    /// <summary>
    ///     One file's captured before/after image, self-sufficient for recovery: <c>PhysicalRoot</c>
    ///     is this file's resolved physical root at capture time (the project root under Project
    ///     scope, or the target's resolved global root under Global scope), so restoring or
    ///     re-claiming this file never needs the originating plan or a live
    ///     <see cref="ISquadGlobalRootResolver" /> again — recovery reads it back verbatim instead of
    ///     re-deriving it from a possibly-changed environment.
    /// </summary>
    private sealed record IntentFile(
        string RelativePath,
        string Target,
        string PhysicalRoot,
        OriginalEntryKind OriginalKind,
        IReadOnlyList<string> MissingParentDirectories,
        OriginalEntryKind AfterKind,
        string AfterSha256);

    private sealed record IntentArtifact(
        string TransactionId,
        ArtifactArea Area,
        ArtifactRole Role,
        string Path,
        OriginalEntryKind NodeKind,
        long ByteLength,
        string Sha256,
        string LinkTarget,
        ArtifactLifecycleState LifecycleState);

    private sealed record CreatedDirectoryAuthority(
        CreatedDirectoryArea Area,
        string Path);

    private sealed record IntentTransition(
        string ArtifactIdentity,
        ArtifactLifecycleState AllowedState);

    private sealed record CapturedRestorePayload(
        OriginalEntryKind OriginalKind,
        ReadOnlyMemory<byte> Content,
        string ContentSha256,
        string LinkTarget);

    private enum TransactionPhase
    {
        Preparing,
        Prepared,

        // Every after-image is published and the transaction is final. Only cleanup is left, and cleanup deletes the
        // backups and the staging area piecemeal, so a kill leaves a partial tree that recovery finishes forward
        // instead of rolling back. A journal written before this phase existed never carries it and recovers as before.
        Committed
    }

    private enum OriginalEntryKind
    {
        Missing,
        File,
        Directory,
        FileSymbolicLink,
        DirectorySymbolicLink
    }

    private enum ArtifactArea
    {
        Journal,
        Work
    }

    private enum ArtifactRole
    {
        [JsonStringEnumMemberName("target-stage")]
        TargetStage,

        [JsonStringEnumMemberName("target-backup")]
        TargetBackup,

        [JsonStringEnumMemberName("target-link-metadata")]
        TargetLinkMetadata,

        [JsonStringEnumMemberName("state-stage")]
        StateStage,

        [JsonStringEnumMemberName("state-original")]
        StateOriginal,

        [JsonStringEnumMemberName("state-link-metadata")]
        StateLinkMetadata,

        [JsonStringEnumMemberName("claimed-original")]
        ClaimedOriginal,

        [JsonStringEnumMemberName("transition-metadata")]
        TransitionMetadata
    }

    private enum ArtifactLifecycleState
    {
        [JsonStringEnumMemberName("pre-operation")]
        PreOperation,

        [JsonStringEnumMemberName("active-transition")]
        ActiveTransition,

        [JsonStringEnumMemberName("post-operation")]
        PostOperation
    }

    /// <summary>
    ///     Where a created directory sits. <c>Target</c> is beneath the deployment root, <c>State</c> is
    ///     beneath the transaction's state authority, and <c>Home</c> is beneath the home's parent, so the
    ///     home's own <c>.kyber-weave/squad</c> tree is recorded and rolled back like any other state.
    /// </summary>
    private enum CreatedDirectoryArea
    {
        Target,
        State,
        Home
    }

    private sealed class SyncToDiskScope(bool? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SyncToDiskOverride.Value = prior;
        }
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors",
        Justification = "Private nested exception used internally for transaction control flow.")]
    private sealed class LeafClaimConflictException(string message) : InvalidOperationException(message);
}
