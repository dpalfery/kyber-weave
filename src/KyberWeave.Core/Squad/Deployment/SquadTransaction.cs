using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A durable point in the ordered Squad deployment transaction.</summary>
public enum SquadTransactionStepKind
{
    IntentWritten,
    FileStaged,
    FileBackedUp,
    FileApplied,
    LockApplied,
    ReceiptApplied
}

/// <summary>An observable transaction step, numbered from one in filesystem order.</summary>
public sealed record SquadTransactionStep(
    int Sequence,
    SquadTransactionStepKind Kind,
    string? RelativePath = null);

/// <summary>Receives transaction checkpoints after their corresponding filesystem mutation.</summary>
public interface ISquadTransactionObserver
{
    void AfterStep(SquadTransactionStep step);
}

internal enum SquadTransactionCheckpointKind
{
    Prepared,
    ActiveTransitionWritten,
    OriginalClaimed,
    AfterImagePublished
}

internal sealed record SquadTransactionCheckpoint(
    int Sequence,
    SquadTransactionCheckpointKind Kind,
    string? RelativePath = null);

internal interface ISquadTransactionCheckpointObserver : ISquadTransactionObserver
{
    void AfterCheckpoint(SquadTransactionCheckpoint checkpoint);
}

/// <summary>
/// Applies a preflighted Squad plan through a recoverable intent, staging, and backup protocol.
/// </summary>
public sealed class SquadTransaction
{
    private const string IntentFileName = "intent.json";
    private const string IntentSchema = "kyber-squad.transaction/v1";
    private static readonly JsonSerializerOptions IntentJsonOptions = CreateIntentJsonOptions();

    private static readonly ConcurrentDictionary<string, byte> ActiveLeases =
        new(StringComparer.Ordinal);

    private readonly SquadStateStore _stateStore;
    private readonly ISquadTransactionObserver? _observer;

    public SquadTransaction(
        SquadStateStore stateStore,
        ISquadTransactionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        _stateStore = stateStore;
        _observer = observer;
    }

    /// <summary>Executes a plan and rolls it back before propagating any caught failure.</summary>
    public void Execute(SquadDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var currentIdentity = SquadPhysicalRootIdentity.Resolve(plan.TargetRoot);
        if (!string.Equals(currentIdentity.Key, plan.PhysicalRootKey, StringComparison.Ordinal))
        {
            throw new SquadDeploymentConflictException(
                "Squad target root changed physical identity after preflight. Recreate the plan before writing.");
        }

        using var lease = AcquireLease(plan.PhysicalRootKey);
        var root = plan.PhysicalRootPath;
        var journalDirectory = _stateStore.ResolveTransactionDirectory(
            root,
            plan.Scope);
        var workDirectory = _stateStore.ResolveTransactionWorkDirectory(
            root,
            plan.Scope);
        var sameTransactionDirectory = SquadFileSystemPathSemantics.AreSame(
            journalDirectory,
            workDirectory);
        var intentPath = Path.Combine(journalDirectory, IntentFileName);
        var workExisted = Directory.Exists(workDirectory);
        var stateAuthorityRoot = _stateStore.ResolveStateAuthorityRoot(root, plan.Scope);
        var journalDirectoriesCreated = CaptureMissingDirectories(
            journalDirectory,
            stateAuthorityRoot);
        var workDirectoriesCreated = sameTransactionDirectory
            ? Array.Empty<string>()
            : CaptureMissingDirectories(workDirectory, root);

        Directory.CreateDirectory(journalDirectory);
        var ownsNewTransaction = false;
        var retainTransaction = false;
        try
        {
            if (File.Exists(intentPath) || (!sameTransactionDirectory && workExisted))
            {
                throw new InvalidOperationException(
                    "An unfinished Squad transaction exists. Recover it before starting another deployment.");
            }

            ownsNewTransaction = true;
            ValidatePreconditions(plan);
            Directory.CreateDirectory(workDirectory);

            var lockPath = _stateStore.ResolveLockPath(root, plan.Scope);
            var receiptPath = _stateStore.ResolveReceiptPath(root, plan.Scope);
            var intent = CaptureIntent(
                plan,
                lockPath,
                receiptPath,
                sameTransactionDirectory
                    ? journalDirectoriesCreated
                    : workDirectoriesCreated,
                journalDirectoriesCreated,
                stateAuthorityRoot);
            var sequence = 0;
            var checkpointSequence = 0;
            try
            {
                WriteIntent(intentPath, intent, overwrite: false);
                lease.HoldJournal(intentPath);
                Notify(ref sequence, SquadTransactionStepKind.IntentWritten);

                var artifacts = new List<IntentArtifact>();
                var stagedPaths = StageFiles(
                    plan,
                    intent.TransactionId,
                    workDirectory,
                    artifacts);
                var backedUpPaths = BackupFiles(plan, intent, workDirectory, artifacts);
                BackupStateFiles(
                    intent,
                    workDirectory,
                    lockPath,
                    receiptPath,
                    artifacts);
                PrepareStateFiles(
                    plan,
                    intent.TransactionId,
                    journalDirectory,
                    artifacts);

                intent = intent with
                {
                    Phase = TransactionPhase.Prepared,
                    Artifacts = artifacts.ToArray()
                };
                lease.ReleaseJournal();
                WriteIntent(intentPath, intent, overwrite: true);
                lease.HoldJournal(intentPath);

                foreach (var relativePath in stagedPaths)
                {
                    Notify(
                        ref sequence,
                        SquadTransactionStepKind.FileStaged,
                        relativePath);
                }

                if (backedUpPaths.Count == 0)
                {
                    Notify(ref sequence, SquadTransactionStepKind.FileBackedUp);
                }
                else
                {
                    foreach (var relativePath in backedUpPaths)
                    {
                        Notify(
                            ref sequence,
                            SquadTransactionStepKind.FileBackedUp,
                        relativePath);
                    }
                }

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
                        root,
                        intent,
                        journalDirectory,
                        workDirectory,
                        lockPath,
                        receiptPath);
                    conflicts = RestoreIntent(
                        root,
                        intent,
                        workDirectory,
                        lockPath,
                        receiptPath);
                }
                catch (InvalidDataException)
                {
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
            CleanupOwnedArtifacts(journalDirectory, workDirectory);
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
            if (ownsNewTransaction && !retainTransaction)
            {
                RemoveCreatedDirectories(workDirectoriesCreated);
                RemoveCreatedDirectories(journalDirectoriesCreated);
            }
        }
    }

    /// <summary>Rolls back an interrupted transaction. Repeated recovery is a no-op.</summary>
    public void Recover(string targetRoot, SquadDeploymentScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

        var identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        using var lease = AcquireLease(identity.Key);
        var root = identity.PhysicalPath;
        var journalDirectory = _stateStore.ResolveTransactionDirectory(root, scope);
        if (!Directory.Exists(journalDirectory))
            return;

        var workDirectory = _stateStore.ResolveTransactionWorkDirectory(root, scope);
        var recoveryCompleted = false;
        IReadOnlyList<CreatedDirectoryAuthority> createdDirectoriesToRemove =
            Array.Empty<CreatedDirectoryAuthority>();
        try
        {
            var intentPath = Path.Combine(journalDirectory, IntentFileName);
            if (!File.Exists(intentPath))
            {
                throw new InvalidDataException(
                    "Squad transaction has preparation artifacts but no authoritative intent journal.");
            }

            var intent = ReadIntent(
                intentPath,
                journalDirectory,
                workDirectory,
                identity.Key,
                root,
                _stateStore.ResolveStateAuthorityRoot(root, scope));
            var lockPath = _stateStore.ResolveLockPath(root, scope);
            var receiptPath = _stateStore.ResolveReceiptPath(root, scope);
            ResolveActiveClaims(
                root,
                intent,
                journalDirectory,
                workDirectory,
                lockPath,
                receiptPath);
            var conflicts = RestoreIntent(root, intent, workDirectory, lockPath, receiptPath);
            if (conflicts.Count > 0)
            {
                throw new SquadDeploymentConflictException(
                    "Squad deployment paths changed after their transaction after-images " +
                    $"were published: {string.Join(", ", conflicts)}. " +
                    "The verified journal was retained for recovery.");
            }
            createdDirectoriesToRemove = intent.CreatedDirectories;

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
                    _stateStore.ResolveStateAuthorityRoot(root, scope));
            }
        }
    }

    private IntentDocument CaptureIntent(
        SquadDeploymentPlan plan,
        string lockPath,
        string receiptPath,
        IReadOnlyList<string> transactionDirectoriesCreated,
        IReadOnlyList<string> journalDirectoriesCreated,
        string stateAuthorityRoot)
    {
        var files = plan.FileMutations
            .Select(mutation =>
            {
                var fullPath = SquadPathPolicy.ResolveFile(
                    plan.PhysicalRootPath,
                    mutation.RelativePath);
                return new IntentFile(
                    mutation.RelativePath,
                    GetEntryKind(fullPath),
                    CaptureMissingDirectories(
                        Path.GetDirectoryName(fullPath)!,
                        plan.PhysicalRootPath)
                        .Select(path => Path.GetRelativePath(plan.PhysicalRootPath, path)
                            .Replace(Path.DirectorySeparatorChar, '/'))
                        .ToArray(),
                    mutation.Kind == SquadFileMutationKind.Write
                        ? OriginalEntryKind.File
                        : OriginalEntryKind.Missing,
                    mutation.Kind == SquadFileMutationKind.Write
                        ? Digest(mutation.Content
                            ?? throw new InvalidOperationException(
                                $"Write mutation '{mutation.RelativePath}' has no content."))
                        : string.Empty);
            })
            .ToArray();
        var lockKind = GetEntryKind(lockPath);
        var receiptKind = GetEntryKind(receiptPath);
        var lockAfter = StateAfterImage(
            plan.LockMutation,
            lockKind,
            lockPath,
            plan.LockMutation == SquadStateMutation.Write
                ? Encoding.UTF8.GetBytes(_stateStore.SerializeLock(
                    plan.Lock ?? throw new InvalidOperationException(
                        "A lock write requires lock content.")))
                : null);
        var receiptAfter = StateAfterImage(
            plan.ReceiptMutation,
            receiptKind,
            receiptPath,
            plan.ReceiptMutation == SquadStateMutation.Write
                ? Encoding.UTF8.GetBytes(_stateStore.SerializeReceipt(plan.Receipt))
                : null);
        return new IntentDocument(
            IntentSchema,
            TransactionPhase.Preparing,
            plan.PhysicalRootKey,
            Guid.NewGuid().ToString("N"),
            files,
            CaptureCreatedDirectoryAuthority(
                transactionDirectoriesCreated,
                journalDirectoriesCreated,
                plan.PhysicalRootPath,
                stateAuthorityRoot),
            lockKind,
            lockAfter.Kind,
            lockAfter.Sha256,
            receiptKind,
            receiptAfter.Kind,
            receiptAfter.Sha256,
            Array.Empty<IntentArtifact>(),
            Array.Empty<IntentTransition>());
    }

    private static (OriginalEntryKind Kind, string Sha256) StateAfterImage(
        SquadStateMutation mutation,
        OriginalEntryKind originalKind,
        string originalPath,
        byte[]? content) =>
        mutation switch
        {
            SquadStateMutation.Keep => (
                originalKind,
                originalKind == OriginalEntryKind.File
                    ? Digest(File.ReadAllBytes(originalPath))
                    : string.Empty),
            SquadStateMutation.Write => (
                OriginalEntryKind.File,
                Digest(content ?? throw new InvalidOperationException(
                    "A state write requires prepared content."))),
            SquadStateMutation.Delete => (OriginalEntryKind.Missing, string.Empty),
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation), mutation, "Unknown state mutation.")
        };

    private static void ValidatePreconditions(SquadDeploymentPlan plan)
    {
        foreach (var precondition in plan.FilePreconditions)
            ValidatePrecondition(plan.PhysicalRootPath, precondition);
    }

    private static void ValidatePrecondition(
        string targetRoot,
        SquadFilePrecondition precondition)
    {
        var fullPath = SquadPathPolicy.ResolveFile(targetRoot, precondition.RelativePath);
        var kind = GetEntryKind(fullPath);
        if (precondition.Kind == SquadFilePreconditionKind.Missing)
        {
            if (kind != OriginalEntryKind.Missing)
                throw PreconditionConflict(precondition.RelativePath);
            return;
        }

        if (kind is not (OriginalEntryKind.File or OriginalEntryKind.FileSymbolicLink) ||
            !string.Equals(
                Digest(File.ReadAllBytes(fullPath)),
                precondition.Sha256,
                StringComparison.Ordinal))
        {
            throw PreconditionConflict(precondition.RelativePath);
        }
    }

    private static IReadOnlyList<string> StageFiles(
        SquadDeploymentPlan plan,
        string transactionId,
        string workDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        var staged = new List<string>();
        foreach (var mutation in plan.FileMutations)
        {
            if (mutation.Kind != SquadFileMutationKind.Write)
                continue;

            var stagingPath = TransactionFilePath(
                workDirectory,
                "staging",
                mutation.RelativePath);
            var content = mutation.Content
                ?? throw new InvalidOperationException(
                    $"Write mutation '{mutation.RelativePath}' has no content.");
            PublishArtifact(
                workDirectory,
                ArtifactArea.Work,
                ArtifactRole.TargetStage,
                transactionId,
                $"staging/{mutation.RelativePath}",
                stagingPath,
                content,
                artifacts);
            staged.Add(mutation.RelativePath);
        }

        return staged;
    }

    private static IReadOnlyList<string> BackupFiles(
        SquadDeploymentPlan plan,
        IntentDocument intent,
        string workDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        var backedUp = new List<string>();
        var intentByPath = intent.Files.ToDictionary(
            file => file.RelativePath,
            StringComparer.Ordinal);
        foreach (var mutation in plan.FileMutations)
        {
            var original = intentByPath[mutation.RelativePath];
            if (original.OriginalKind == OriginalEntryKind.Missing)
                continue;

            var targetPath = SquadPathPolicy.ResolveFile(plan.PhysicalRootPath, mutation.RelativePath);
            var backupPath = TransactionFilePath(
                workDirectory,
                "backups",
                mutation.RelativePath);
            BackupEntry(
                targetPath,
                backupPath,
                original.OriginalKind,
                TransactionFilePath(workDirectory, "links", mutation.RelativePath),
                workDirectory,
                mutation.RelativePath,
                intent.TransactionId,
                artifacts);
            backedUp.Add(mutation.RelativePath);
        }

        return backedUp;
    }

    private static void BackupStateFiles(
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
        string transactionId,
        string journalDirectory,
        ICollection<IntentArtifact> artifacts)
    {
        if (plan.LockMutation == SquadStateMutation.Write)
        {
            var squadLock = plan.Lock
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
        {
            PublishArtifact(
                journalDirectory,
                ArtifactArea.Journal,
                ArtifactRole.StateStage,
                transactionId,
                "state-staging/receipt",
                StateStagingPath(journalDirectory, "receipt"),
                Encoding.UTF8.GetBytes(_stateStore.SerializeReceipt(plan.Receipt)),
                artifacts);
        }
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
        var preconditions = plan.FilePreconditions.ToDictionary(
            precondition => precondition.RelativePath,
            StringComparer.Ordinal);
        foreach (var mutation in plan.FileMutations)
        {
            var fileIntent = intent.Files.Single(file =>
                string.Equals(file.RelativePath, mutation.RelativePath, StringComparison.Ordinal));
            var precondition = preconditions[mutation.RelativePath];
            if (mutation.Kind == SquadFileMutationKind.Write)
            {
                var stagingPath = TransactionFilePath(
                    workDirectory,
                    "staging",
                    mutation.RelativePath);
                VerifyPreparedStage(
                    intent,
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    $"staging/{mutation.RelativePath}",
                    stagingPath,
                    mutation.RelativePath);
            }

            ClaimExistingNoOverwrite(
                plan.PhysicalRootPath,
                mutation.RelativePath,
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
                var stageRelativePath = $"staging/{mutation.RelativePath}";
                var stagingPath = TransactionFilePath(
                    workDirectory,
                    "staging",
                    mutation.RelativePath);
                BeginArtifactTransition(
                    ArtifactArea.Work,
                    stageRelativePath,
                    intentPath,
                    lease,
                    ref intent);
                var targetPath = SquadPathPolicy.ResolveFile(
                    plan.PhysicalRootPath,
                    mutation.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                PublishStageNoOverwrite(
                    intent,
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    stageRelativePath,
                    plan.PhysicalRootPath,
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
                    stageRelativePath,
                    intentPath,
                    lease,
                    ref intent);
            }
            else
            {
                VerifyAfterImage(
                    SquadPathPolicy.ResolveFile(plan.PhysicalRootPath, mutation.RelativePath),
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

    private static void ApplyStateFileNoOverwrite(
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
        var stagingPath = StateStagingPath(journalDirectory, stageName);
        var stageRelativePath = $"state-staging/{stageName}";
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

    private static void ClaimStateNoOverwrite(
        string workDirectory,
        string stateName,
        string targetPath,
        OriginalEntryKind originalKind,
        string intentPath,
        TransactionLease lease,
        ISquadTransactionObserver? observer,
        ref int checkpointSequence,
        ref IntentDocument intent) =>
        ClaimExistingNoOverwrite(
            Path.GetDirectoryName(targetPath)!,
            Path.GetFileName(targetPath),
            SquadFileSystemPathSemantics.AreSame(
                workDirectory,
                Path.GetDirectoryName(intentPath)!)
                ? workDirectory
                : Path.GetDirectoryName(intentPath)!,
            SquadFileSystemPathSemantics.AreSame(
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

    private static void ClaimExistingNoOverwrite(
        string boundRoot,
        string relativePath,
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
        var targetPath = SquadPathPolicy.ResolveFile(boundRoot, relativePath);
        var currentKind = GetEntryKind(targetPath);
        if (currentKind == OriginalEntryKind.Missing)
        {
            if (expectedKind != OriginalEntryKind.Missing)
                throw ClaimConflict(relativePath);
            return;
        }

        var claimRelativePath = ClaimRelativePath(intent.TransactionId, relativePath);
        var claimPath = SquadPathPolicy.ResolveFile(claimRoot, claimRelativePath);
        var artifact = CaptureNodeArtifact(
            intent.TransactionId,
            claimArea,
            ArtifactRole.ClaimedOriginal,
            claimRelativePath,
            targetPath,
            ArtifactLifecycleState.ActiveTransition);
        var resolvedDirectoryLinkTarget = currentKind == OriginalEntryKind.DirectorySymbolicLink &&
            !Path.IsPathRooted(artifact.LinkTarget)
                ? new DirectoryInfo(targetPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                : null;
        intent = intent with
        {
            Artifacts = intent.Artifacts.Append(artifact).ToArray(),
            ActiveTransitions =
            [new IntentTransition(ArtifactIdentity(claimArea, claimRelativePath), ArtifactLifecycleState.PreOperation)]
        };
        PublishIntentGeneration(intentPath, intent, lease);
        NotifyCheckpoint(
            ref checkpointSequence,
            observer,
            SquadTransactionCheckpointKind.ActiveTransitionWritten,
            subject);

        var checkedTarget = SquadPathPolicy.ResolveFile(boundRoot, relativePath);
        if (!SquadFileSystemPathSemantics.AreSame(checkedTarget, targetPath))
            throw ClaimConflict(relativePath);
        MoveEntryNoOverwrite(targetPath, claimPath, currentKind);
        if (resolvedDirectoryLinkTarget is not null)
        {
            File.Delete(claimPath);
            Directory.CreateSymbolicLink(
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
        {
            throw new InvalidDataException(
                $"Squad claimed original '{relativePath}' changed during its atomic claim.");
        }
        NotifyCheckpoint(
            ref checkpointSequence,
            observer,
            SquadTransactionCheckpointKind.OriginalClaimed,
            subject);

        var matchesExpected = expectedKind != OriginalEntryKind.Missing &&
            artifact.NodeKind == currentKind &&
            ArtifactFingerprintMatches(claimPath, artifact) &&
            (expectedKind != OriginalEntryKind.File || expectedSha256.Length == 0 ||
                string.Equals(
                    Digest(File.ReadAllBytes(claimPath)),
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

    private static void PublishStageNoOverwrite(
        IntentDocument intent,
        ArtifactArea area,
        ArtifactRole role,
        string artifactRelativePath,
        string boundRoot,
        string relativePath,
        string stagingPath,
        string targetPath)
    {
        var checkedTarget = SquadPathPolicy.ResolveFile(boundRoot, relativePath);
        if (!SquadFileSystemPathSemantics.AreSame(checkedTarget, targetPath))
            throw PreconditionConflict(relativePath);

        var artifacts = intent.Artifacts.Where(artifact =>
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
        {
            throw new InvalidDataException(
                $"Squad prepared artifact '{relativePath}' changed before publication.");
        }

        MoveEntryNoOverwrite(stagingPath, targetPath, artifacts[0].NodeKind);
    }

    private static void VerifyAfterImage(
        string path,
        OriginalEntryKind expectedKind,
        string expectedSha256,
        string displayPath)
    {
        if (GetEntryKind(path) != expectedKind ||
            (expectedKind == OriginalEntryKind.File &&
                !string.Equals(
                    Digest(File.ReadAllBytes(path)),
                    expectedSha256,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Squad after-image '{displayPath}' failed verification.");
        }
    }

    private static IReadOnlyList<string> RestoreIntent(
        string targetRoot,
        IntentDocument intent,
        string workDirectory,
        string lockPath,
        string receiptPath)
    {
        if (intent.Phase == TransactionPhase.Preparing)
            return Array.Empty<string>();

        VerifyRestoreArtifacts(intent);
        var conflicts = new List<string>();
        foreach (var file in intent.Files.Reverse())
        {
            var targetPath = SquadPathPolicy.ResolveFile(targetRoot, file.RelativePath);
            var backupPath = TransactionFilePath(
                workDirectory,
                "backups",
                file.RelativePath);
            var linkMetadataPath = TransactionFilePath(
                workDirectory,
                "links",
                file.RelativePath);
            CompareAndRestoreEntry(
                intent,
                targetPath,
                backupPath,
                file.OriginalKind,
                linkMetadataPath,
                ArtifactRole.TargetBackup,
                $"backups/{file.RelativePath}",
                ArtifactRole.TargetLinkMetadata,
                $"links/{file.RelativePath}",
                file.AfterKind,
                file.AfterSha256,
                file.RelativePath,
                conflicts);
            foreach (var relativeDirectory in file.MissingParentDirectories)
            {
                var directory = SquadPathPolicy.ResolveFile(targetRoot, relativeDirectory);
                DeleteEmptyDirectory(directory);
                if (GetEntryKind(directory) != OriginalEntryKind.Missing)
                    conflicts.Add(relativeDirectory);
            }
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

    private static void ResolveActiveClaims(
        string targetRoot,
        IntentDocument intent,
        string journalDirectory,
        string workDirectory,
        string lockPath,
        string receiptPath)
    {
        foreach (var artifact in intent.Artifacts.Where(artifact =>
                     artifact.Role == ArtifactRole.ClaimedOriginal &&
                     artifact.LifecycleState is ArtifactLifecycleState.ActiveTransition or
                         ArtifactLifecycleState.PostOperation))
        {
            string? targetPath = null;
            foreach (var file in intent.Files)
            {
                if (string.Equals(
                        artifact.Path,
                        ClaimRelativePath(intent.TransactionId, file.RelativePath),
                        StringComparison.Ordinal))
                {
                    targetPath = SquadPathPolicy.ResolveFile(targetRoot, file.RelativePath);
                    break;
                }
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
            {
                throw new InvalidDataException(
                    $"Squad active claim '{artifact.Path}' has no transaction subject.");
            }

            var artifactRoot = artifact.Area == ArtifactArea.Journal
                ? journalDirectory
                : workDirectory;
            var claimPath = ResolveArtifactNode(artifactRoot, artifact.Path);
            var claimKind = GetEntryKind(claimPath);
            var targetKind = GetEntryKind(targetPath);
            if (claimKind == OriginalEntryKind.Missing)
            {
                if (targetKind == OriginalEntryKind.Missing)
                {
                    throw new InvalidDataException(
                        $"Squad active claim '{artifact.Path}' lost both transition alternatives.");
                }

                continue;
            }

            if (targetKind == OriginalEntryKind.Missing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                VerifyAndMoveArtifactNoOverwrite(
                    artifact,
                    claimPath,
                    targetPath,
                    artifact.Path);
            }
        }
    }

    private static void VerifyAndMoveArtifactNoOverwrite(
        IntentArtifact artifact,
        string sourcePath,
        string destinationPath,
        string displayPath)
    {
        if (GetEntryKind(sourcePath) != artifact.NodeKind ||
            !ArtifactFingerprintMatches(sourcePath, artifact))
        {
            throw new InvalidDataException(
                $"Squad claim artifact '{displayPath}' changed before restoration.");
        }

        MoveEntryNoOverwrite(sourcePath, destinationPath, artifact.NodeKind);
        if (GetEntryKind(destinationPath) != artifact.NodeKind ||
            !ArtifactFingerprintMatches(destinationPath, artifact))
        {
            throw new InvalidDataException(
                $"Squad claim artifact '{displayPath}' changed during restoration.");
        }
    }

    private static bool EntryMatchesOriginal(
        string targetPath,
        CapturedRestorePayload payload)
    {
        var currentKind = GetEntryKind(targetPath);
        if (currentKind != payload.OriginalKind)
            return false;

        return payload.OriginalKind switch
        {
            OriginalEntryKind.Missing or OriginalEntryKind.Directory => true,
            OriginalEntryKind.File => string.Equals(
                Digest(File.ReadAllBytes(targetPath)),
                payload.ContentSha256,
                StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink =>
                string.Equals(
                    new FileInfo(targetPath).LinkTarget,
                    payload.LinkTarget,
                    StringComparison.Ordinal),
            OriginalEntryKind.DirectorySymbolicLink =>
                string.Equals(
                    new DirectoryInfo(targetPath).LinkTarget,
                    payload.LinkTarget,
                    StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(
                nameof(payload), payload.OriginalKind, "Unknown original entry kind.")
        };
    }

    private static bool EntryMatchesAfter(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload)
    {
        var currentKind = GetEntryKind(targetPath);
        if (currentKind != afterKind)
            return false;

        return afterKind switch
        {
            OriginalEntryKind.Missing or OriginalEntryKind.Directory => true,
            OriginalEntryKind.File => string.Equals(
                Digest(File.ReadAllBytes(targetPath)),
                afterSha256,
                StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink or OriginalEntryKind.DirectorySymbolicLink =>
                EntryMatchesOriginal(targetPath, payload),
            _ => throw new ArgumentOutOfRangeException(
                nameof(afterKind), afterKind, "Unknown after-image entry kind.")
        };
    }

    private static void VerifyRestoreArtifacts(IntentDocument intent)
    {
        foreach (var file in intent.Files)
        {
            VerifyRestoreArtifacts(
                intent,
                file.OriginalKind,
                ArtifactRole.TargetBackup,
                $"backups/{file.RelativePath}",
                ArtifactRole.TargetLinkMetadata,
                $"links/{file.RelativePath}");
        }

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

    private static void VerifyRestoreArtifacts(
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
        {
            _ = RequireRestoreArtifact(
                intent,
                backupRole,
                backupRelativePath);
        }

        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
        {
            _ = RequireRestoreArtifact(
                intent,
                linkRole,
                linkRelativePath);
        }
    }

    private static IntentArtifact RequireRestoreArtifact(
        IntentDocument intent,
        ArtifactRole role,
        string relativePath)
    {
        var matches = intent.Artifacts.Where(artifact =>
                artifact.Area == ArtifactArea.Work &&
                artifact.Role == role &&
                string.Equals(artifact.Path, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].LifecycleState != ArtifactLifecycleState.PreOperation)
        {
            throw new InvalidDataException(
                $"Squad restore artifact '{relativePath}' has invalid authority.");
        }

        return matches[0];
    }

    private static CapturedRestorePayload CaptureVerifiedRestorePayload(
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
            var authority = RequireRestoreArtifact(intent, backupRole, backupRelativePath);
            var beforeKind = GetEntryKind(backupPath);
            backupBytes = File.ReadAllBytes(backupPath);
            var afterKind = GetEntryKind(backupPath);
            if (beforeKind != authority.NodeKind ||
                afterKind != authority.NodeKind ||
                backupBytes.LongLength != authority.ByteLength ||
                !string.Equals(Digest(backupBytes), authority.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Squad restore artifact '{backupRelativePath}' changed during capture.");
            }
        }
        else if (originalKind == OriginalEntryKind.Directory)
        {
            var authority = RequireRestoreArtifact(intent, backupRole, backupRelativePath);
            var beforeKind = GetEntryKind(backupPath);
            var afterKind = GetEntryKind(backupPath);
            if (beforeKind != authority.NodeKind || afterKind != authority.NodeKind)
            {
                throw new InvalidDataException(
                    $"Squad restore artifact '{backupRelativePath}' changed during capture.");
            }
        }

        var linkTarget = string.Empty;
        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
        {
            var authority = RequireRestoreArtifact(intent, linkRole, linkRelativePath);
            var beforeKind = GetEntryKind(linkMetadataPath);
            var linkBytes = File.ReadAllBytes(linkMetadataPath);
            var afterKind = GetEntryKind(linkMetadataPath);
            if (beforeKind != authority.NodeKind ||
                afterKind != authority.NodeKind ||
                linkBytes.LongLength != authority.ByteLength ||
                !string.Equals(Digest(linkBytes), authority.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Squad restore artifact '{linkRelativePath}' changed during capture.");
            }

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

    private static void RestoreEntry(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload,
        string displayPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (!VerifyRestoreAfterImage(targetPath, afterKind, afterSha256, payload) ||
            GetEntryKind(targetPath) != afterKind)
        {
            throw new InvalidDataException(
                $"Squad live after-image '{displayPath}' changed before restoration.");
        }

        DeleteEntry(targetPath, recursiveDirectory: true);
        switch (payload.OriginalKind)
        {
            case OriginalEntryKind.Missing:
                break;
            case OriginalEntryKind.File:
                using (var stream = new FileStream(
                           targetPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(payload.Content.Span);
                    stream.Flush(flushToDisk: true);
                }
                break;
            case OriginalEntryKind.Directory:
                Directory.CreateDirectory(targetPath);
                break;
            case OriginalEntryKind.FileSymbolicLink:
                File.CreateSymbolicLink(targetPath, payload.LinkTarget);
                break;
            case OriginalEntryKind.DirectorySymbolicLink:
                Directory.CreateSymbolicLink(targetPath, payload.LinkTarget);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(payload), payload.OriginalKind, "Unknown original entry kind.");
        }

        if (!EntryMatchesOriginal(targetPath, payload))
        {
            throw new InvalidDataException(
                $"Squad restored entry '{displayPath}' failed verification.");
        }
    }

    private static bool VerifyRestoreAfterImage(
        string targetPath,
        OriginalEntryKind afterKind,
        string afterSha256,
        CapturedRestorePayload payload) =>
        EntryMatchesAfter(targetPath, afterKind, afterSha256, payload);

    private static void CompareAndRestoreEntry(
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
        var payload = CaptureVerifiedRestorePayload(
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

    private static void VerifyPreparedStage(
        IntentDocument intent,
        ArtifactArea area,
        ArtifactRole role,
        string relativePath,
        string stagingPath,
        string displayPath)
    {
        var matches = intent.Artifacts.Where(artifact =>
                artifact.Area == area &&
                artifact.Role == role &&
                string.Equals(artifact.Path, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].LifecycleState != ArtifactLifecycleState.PreOperation ||
            GetEntryKind(stagingPath) != matches[0].NodeKind ||
            !ArtifactFingerprintMatches(stagingPath, matches[0]))
        {
            throw new InvalidDataException(
                $"Squad prepared artifact '{displayPath}' changed before it was consumed.");
        }
    }

    private static void BeginArtifactTransition(
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

    private static void CompleteArtifactTransition(
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

    private static IntentDocument SetArtifactLifecycle(
        IntentDocument intent,
        ArtifactArea area,
        string relativePath,
        ArtifactLifecycleState lifecycleState)
    {
        var identity = ArtifactIdentity(area, relativePath);
        var changed = 0;
        var artifacts = intent.Artifacts.Select(artifact =>
        {
            if (!string.Equals(
                    ArtifactIdentity(artifact.Area, artifact.Path),
                    identity,
                    StringComparison.Ordinal))
            {
                return artifact;
            }

            changed++;
            return artifact with { LifecycleState = lifecycleState };
        }).ToArray();
        if (changed != 1)
        {
            throw new InvalidDataException(
                $"Squad transition artifact '{relativePath}' is missing or duplicated.");
        }

        return intent with
        {
            Artifacts = artifacts,
            ActiveTransitions = lifecycleState == ArtifactLifecycleState.ActiveTransition
                ? intent.ActiveTransitions
                : Array.Empty<IntentTransition>()
        };
    }

    private static IntentArtifact CaptureNodeArtifact(
        string transactionId,
        ArtifactArea area,
        ArtifactRole role,
        string relativePath,
        string sourcePath,
        ArtifactLifecycleState lifecycleState)
    {
        var kind = GetEntryKind(sourcePath);
        return kind switch
        {
            OriginalEntryKind.File => new IntentArtifact(
                transactionId,
                area,
                role,
                relativePath,
                kind,
                new FileInfo(sourcePath).Length,
                Digest(File.ReadAllBytes(sourcePath)),
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
                new FileInfo(sourcePath).LinkTarget
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
                new DirectoryInfo(sourcePath).LinkTarget
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

    private static void MoveEntryNoOverwrite(
        string sourcePath,
        string destinationPath,
        OriginalEntryKind kind)
    {
        if (kind is OriginalEntryKind.Directory or OriginalEntryKind.DirectorySymbolicLink)
            Directory.Move(sourcePath, destinationPath);
        else
            File.Move(sourcePath, destinationPath);
    }

    private static void RemoveEmptyArtifactParents(string artifactPath, string areaRoot)
    {
        var current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !SquadFileSystemPathSemantics.AreSame(current, areaRoot) &&
               SquadFileSystemPathSemantics.IsWithin(areaRoot, current))
        {
            DeleteEmptyDirectory(current);
            if (Directory.Exists(current))
                break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static void PublishIntentGeneration(
        string intentPath,
        IntentDocument intent,
        TransactionLease lease)
    {
        lease.ReleaseJournal();
        WriteIntent(intentPath, intent, overwrite: true);
        lease.HoldJournal(intentPath);
    }

    private static void CopyStateOriginal(
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
        {
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
    }

    private static void BackupEntry(
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
            var linkTarget = kind == OriginalEntryKind.FileSymbolicLink
                ? new FileInfo(sourcePath).LinkTarget
                : new DirectoryInfo(sourcePath).LinkTarget;
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
                    File.ReadAllBytes(sourcePath),
                    artifacts);
            }

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
                File.ReadAllBytes(sourcePath),
                artifacts);
            return;
        }

        CopyEntry(sourcePath, destinationPath, kind);
        if (kind == OriginalEntryKind.Directory)
        {
            var relativePath = sourceRelativePath.StartsWith(
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

    private static void CopyEntry(
        string sourcePath,
        string destinationPath,
        OriginalEntryKind kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        switch (kind)
        {
            case OriginalEntryKind.File:
                File.Copy(sourcePath, destinationPath, overwrite: false);
                break;
            case OriginalEntryKind.Directory:
                Directory.CreateDirectory(destinationPath);
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

    private static OriginalEntryKind GetEntryKind(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null && directory.Exists)
            return OriginalEntryKind.DirectorySymbolicLink;

        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
            return OriginalEntryKind.FileSymbolicLink;
        if (file.Exists)
            return OriginalEntryKind.File;

        if (directory.LinkTarget is not null)
            return OriginalEntryKind.DirectorySymbolicLink;
        return directory.Exists
            ? OriginalEntryKind.Directory
            : OriginalEntryKind.Missing;
    }

    private static IReadOnlyList<string> CaptureMissingDirectories(
        string directoryPath,
        string boundaryPath)
    {
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundaryPath));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var missing = new List<string>();
        while (!SquadFileSystemPathSemantics.AreSame(current, boundary) &&
               SquadFileSystemPathSemantics.IsWithin(boundary, current))
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

    private static IReadOnlyList<CreatedDirectoryAuthority> CaptureCreatedDirectoryAuthority(
        IReadOnlyList<string> transactionDirectories,
        IReadOnlyList<string> journalDirectories,
        string targetRoot,
        string stateAuthorityRoot)
    {
        var authorities = transactionDirectories.Select(path =>
                new CreatedDirectoryAuthority(
                    CreatedDirectoryArea.Target,
                    RelativeAuthorityPath(targetRoot, path)))
            .Concat(journalDirectories.Select(path =>
                new CreatedDirectoryAuthority(
                    CreatedDirectoryArea.State,
                    RelativeAuthorityPath(stateAuthorityRoot, path))))
            .Distinct()
            .OrderByDescending(authority => authority.Path.Count(character => character == '/'))
            .ThenBy(authority => authority.Area)
            .ThenBy(authority => authority.Path, StringComparer.Ordinal)
            .ToArray();
        return authorities;
    }

    private static string RelativeAuthorityPath(string authorityRoot, string path)
    {
        if (!SquadFileSystemPathSemantics.IsWithin(authorityRoot, path))
        {
            throw new InvalidOperationException(
                "Squad created-directory authority escaped its bound root.");
        }

        return SquadPathPolicy.NormalizeRelativePath(
            Path.GetRelativePath(authorityRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static void RemoveCreatedDirectories(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
            DeleteEmptyDirectory(directory);
    }

    private static void RemoveCreatedDirectories(
        IEnumerable<CreatedDirectoryAuthority> directories,
        string targetRoot,
        string stateAuthorityRoot)
    {
        foreach (var authority in directories)
        {
            var root = authority.Area == CreatedDirectoryArea.Target
                ? targetRoot
                : stateAuthorityRoot;
            DeleteEmptyDirectory(SquadPathPolicy.ResolveFile(root, authority.Path));
        }
    }

    private static void DeleteEmptyDirectory(string path)
    {
        if (GetEntryKind(path) != OriginalEntryKind.Directory ||
            Directory.EnumerateFileSystemEntries(path).Any())
        {
            return;
        }

        Directory.Delete(path);
    }

    private static void DeleteEntry(string path, bool recursiveDirectory)
    {
        switch (GetEntryKind(path))
        {
            case OriginalEntryKind.Missing:
                return;
            case OriginalEntryKind.File:
            case OriginalEntryKind.FileSymbolicLink:
                File.Delete(path);
                return;
            case OriginalEntryKind.DirectorySymbolicLink:
                Directory.Delete(path);
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

    private static void DeleteDirectoryTree(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    File.Delete(entry.FullName);
                continue;
            }

            if (entry is DirectoryInfo directory)
                DeleteDirectoryTree(directory.FullName);
            else
                entry.Delete();
        }

        Directory.Delete(path);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A successful TransactionLease takes ownership of the mutex; the catch path disposes it.")]
    private static TransactionLease AcquireLease(string rootKey)
    {
        if (!ActiveLeases.TryAdd(rootKey, 0))
        {
            throw new InvalidOperationException(
                "A Squad transaction is active for this deployment root. Wait for it to finish before retrying.");
        }

        Mutex? mutex = null;
        var mutexAcquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, $"kyber-weave-squad-{rootKey}");
            try
            {
                mutexAcquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new InvalidOperationException(
                    "A Squad transaction is active for this deployment root. Wait for it to finish before retrying.");
            }

            return new TransactionLease(rootKey, mutex);
        }
        catch (Exception exception)
        {
            if (mutex is not null)
            {
                if (mutexAcquired)
                {
                    mutex.ReleaseMutex();
                }

                mutex.Dispose();
            }

            ActiveLeases.TryRemove(rootKey, out _);
            if (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "A Squad transaction is active for this deployment root. Wait for it to finish before retrying.",
                    exception);
            }

            throw;
        }
    }

    private static void CleanupOwnedArtifacts(
        string journalDirectory,
        string workDirectory)
    {
        if (!SquadFileSystemPathSemantics.AreSame(journalDirectory, workDirectory) &&
            Directory.Exists(workDirectory))
            DeleteDirectoryTree(workDirectory);

        if (!Directory.Exists(journalDirectory))
            return;

        foreach (var entry in new DirectoryInfo(journalDirectory).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo directory)
            {
                DeleteDirectoryTree(directory.FullName);
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private string JournalBoundary(SquadDeploymentPlan plan) =>
        plan.Scope == SquadDeploymentScope.Project
            ? plan.PhysicalRootPath
            : Path.GetPathRoot(_stateStore.ResolveStateDirectory(
                plan.PhysicalRootPath,
                plan.Scope))
                ?? throw new InvalidOperationException(
                    "Could not resolve the global Squad state filesystem root.");

    private static void WriteIntent(
        string intentPath,
        IntentDocument intent,
        bool overwrite)
    {
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(intent, IntentJsonOptions) + "\n");
        PublishFileAtomic(intentPath, bytes, overwrite);
    }

    private static void PublishArtifact(
        string areaRoot,
        ArtifactArea area,
        ArtifactRole role,
        string transactionId,
        string relativePath,
        string artifactPath,
        ReadOnlySpan<byte> content,
        ICollection<IntentArtifact> artifacts)
    {
        var normalizedPath = SquadPathPolicy.NormalizeRelativePath(relativePath);
        var resolvedPath = SquadPathPolicy.ResolveFile(areaRoot, normalizedPath);
        if (!SquadFileSystemPathSemantics.AreSame(resolvedPath, artifactPath))
        {
            throw new InvalidOperationException(
                $"Squad transaction artifact '{relativePath}' resolved inconsistently.");
        }

        var bytes = content.ToArray();
        PublishFileAtomic(artifactPath, bytes, overwrite: false);
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

    private static void PublishFileAtomic(
        string destinationPath,
        ReadOnlySpan<byte> content,
        bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static IntentDocument ReadIntent(
        string intentPath,
        string journalDirectory,
        string workDirectory,
        string expectedRootKey,
        string targetRoot,
        string stateAuthorityRoot)
    {
        try
        {
            var json = File.ReadAllText(intentPath, Encoding.UTF8);
            ValidateIntentJsonShape(json);
            var intent = JsonSerializer.Deserialize<IntentDocument>(json, IntentJsonOptions)
                ?? throw new InvalidDataException("Squad transaction intent is empty.");
            if (!string.Equals(intent.Schema, IntentSchema, StringComparison.Ordinal) ||
                !string.Equals(intent.RootKey, expectedRootKey, StringComparison.Ordinal) ||
                !IsDigest(intent.RootKey) ||
                !IsTransactionId(intent.TransactionId) ||
                intent.Files is null ||
                intent.CreatedDirectories is null ||
                intent.Artifacts is null ||
                intent.ActiveTransitions is null)
            {
                throw new InvalidDataException(
                    "Squad transaction intent has an unsupported schema or physical-root binding.");
            }

            var fileIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in intent.Files)
            {
                var normalized = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
                if (!string.Equals(normalized, file.RelativePath, StringComparison.Ordinal) ||
                    !fileIdentities.Add(normalized) ||
                    file.MissingParentDirectories is null)
                {
                    throw new InvalidDataException(
                        "Squad transaction intent contains a non-portable path.");
                }

                foreach (var directory in file.MissingParentDirectories)
                    _ = SquadPathPolicy.NormalizeRelativePath(directory);
                ValidateAfterDigest(file.AfterKind, file.AfterSha256, "file");
            }

            ValidateAfterDigest(intent.LockAfterKind, intent.LockAfterSha256, "lock");
            ValidateAfterDigest(intent.ReceiptAfterKind, intent.ReceiptAfterSha256, "receipt");
            ValidateCreatedDirectoryAuthority(
                intent.CreatedDirectories,
                targetRoot,
                stateAuthorityRoot,
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
                    "Squad preparing journal cannot claim published artifacts.");
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

    private static void ValidateIntentJsonShape(string json)
    {
        using var document = JsonDocument.Parse(json);
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
            ["preparing", "prepared"],
            "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "lockKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "lockAfterKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "receiptKind", "transaction intent");
        RequireCanonicalNodeKind(document.RootElement, "receiptAfterKind", "transaction intent");

        var files = document.RootElement.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction intent files must be an array.");
        foreach (var file in files.EnumerateArray())
        {
            RequireExactJsonFields(
                file,
                [
                    "relativePath",
                    "originalKind",
                    "missingParentDirectories",
                    "afterKind",
                    "afterSha256"
                ],
                "transaction file");
            RequireCanonicalNodeKind(file, "originalKind", "transaction file");
            RequireCanonicalNodeKind(file, "afterKind", "transaction file");
        }

        var createdDirectories = document.RootElement.GetProperty("createdDirectories");
        if (createdDirectories.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Squad transaction created directories must be an array.");
        }

        foreach (var directory in createdDirectories.EnumerateArray())
        {
            RequireExactJsonFields(
                directory,
                ["area", "path"],
                "transaction created directory");
            RequireCanonicalJsonEnum(
                directory,
                "area",
                ["target", "state"],
                "transaction created directory");
        }

        var artifacts = document.RootElement.GetProperty("artifacts");
        if (artifacts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction artifacts must be an array.");
        foreach (var artifact in artifacts.EnumerateArray())
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

        var activeTransitions = document.RootElement.GetProperty("activeTransitions");
        if (activeTransitions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Squad transaction active transitions must be an array.");
        foreach (var transition in activeTransitions.EnumerateArray())
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

    private static void RequireCanonicalNodeKind(
        JsonElement element,
        string propertyName,
        string subject) =>
        RequireCanonicalJsonEnum(
            element,
            propertyName,
            ["missing", "file", "directory", "fileSymbolicLink", "directorySymbolicLink"],
            subject);

    private static void RequireCanonicalJsonEnum(
        JsonElement element,
        string propertyName,
        IReadOnlyCollection<string> allowedValues,
        string subject)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } token ||
            !allowedValues.Contains(token))
        {
            throw new InvalidDataException(
                $"Squad {subject} field '{propertyName}' must use its canonical enum token.");
        }
    }

    private static void RequireExactJsonFields(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string subject)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Squad {subject} must be an object.");

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Squad {subject} contains duplicate field '{property.Name}'.");
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                throw new InvalidDataException(
                    $"Squad {subject} field '{property.Name}' cannot be null.");
            }
        }

        var missing = expected.Where(field => !actual.Contains(field)).ToArray();
        var unknown = actual.Where(field => !expected.Contains(field)).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            throw new InvalidDataException(
                $"Squad {subject} has missing or unknown fields. " +
                $"Missing: {string.Join(", ", missing)}; unknown: {string.Join(", ", unknown)}.");
        }
    }

    private static void ValidateAfterDigest(
        OriginalEntryKind kind,
        string digest,
        string subject)
    {
        if (kind == OriginalEntryKind.File)
        {
            if (!IsDigest(digest))
            {
                throw new InvalidDataException(
                    $"Squad transaction {subject} after-image digest is invalid.");
            }
        }
        else if (digest.Length != 0)
        {
            throw new InvalidDataException(
                $"Squad transaction {subject} non-file after-image has a digest.");
        }
    }

    private static void ValidateSemanticArtifactSet(
        IntentDocument intent,
        string journalDirectory,
        string workDirectory)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in intent.Files)
        {
            if (file.AfterKind == OriginalEntryKind.File)
            {
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetStage,
                    $"staging/{file.RelativePath}"));
            }

            if (file.OriginalKind is OriginalEntryKind.File or
                OriginalEntryKind.Directory or
                OriginalEntryKind.FileSymbolicLink)
            {
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetBackup,
                    $"backups/{file.RelativePath}"));
            }

            if (file.OriginalKind is OriginalEntryKind.FileSymbolicLink or
                OriginalEntryKind.DirectorySymbolicLink)
            {
                expected.Add(SemanticArtifactIdentity(
                    ArtifactArea.Work,
                    ArtifactRole.TargetLinkMetadata,
                    $"links/{file.RelativePath}"));
            }
        }

        AddExpectedStateArtifacts(
            expected,
            "lock",
            intent.LockKind,
            intent.LockAfterKind,
            intent.LockAfterSha256,
            workDirectory);
        AddExpectedStateArtifacts(
            expected,
            "receipt",
            intent.ReceiptKind,
            intent.ReceiptAfterKind,
            intent.ReceiptAfterSha256,
            workDirectory);

        var optionalClaims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in intent.Files)
        {
            optionalClaims.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.ClaimedOriginal,
                ClaimRelativePath(intent.TransactionId, file.RelativePath)));
        }

        var stateClaimArea = SquadFileSystemPathSemantics.AreSame(
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

        foreach (var artifact in intent.Artifacts)
        {
            var identity = SemanticArtifactIdentity(
                artifact.Area,
                artifact.Role,
                artifact.Path);
            if (expected.Remove(identity))
                continue;
            if (artifact.Role == ArtifactRole.ClaimedOriginal &&
                artifact.LifecycleState != ArtifactLifecycleState.PreOperation &&
                optionalClaims.Remove(identity))
            {
                continue;
            }

            throw new InvalidDataException(
                $"Squad artifact '{artifact.Path}' is not required by transaction semantics.");
        }

        if (expected.Count != 0)
        {
            throw new InvalidDataException(
                "Squad transaction is missing artifacts required by its mutation semantics.");
        }
    }

    private static void AddExpectedStateArtifacts(
        ISet<string> expected,
        string stateName,
        OriginalEntryKind originalKind,
        OriginalEntryKind afterKind,
        string afterSha256,
        string workDirectory)
    {
        var originalPath = StateOriginalPath(workDirectory, stateName);
        var unchangedFile = originalKind == OriginalEntryKind.File &&
            afterKind == OriginalEntryKind.File &&
            GetEntryKind(originalPath) == OriginalEntryKind.File &&
            string.Equals(
                Digest(File.ReadAllBytes(originalPath)),
                afterSha256,
                StringComparison.Ordinal);
        if (afterKind == OriginalEntryKind.File && !unchangedFile)
        {
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Journal,
                ArtifactRole.StateStage,
                $"state-staging/{stateName}"));
        }

        if (originalKind is OriginalEntryKind.File or
            OriginalEntryKind.Directory or
            OriginalEntryKind.FileSymbolicLink)
        {
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.StateOriginal,
                $"state-originals/{stateName}"));
        }

        if (originalKind is OriginalEntryKind.FileSymbolicLink or
            OriginalEntryKind.DirectorySymbolicLink)
        {
            expected.Add(SemanticArtifactIdentity(
                ArtifactArea.Work,
                ArtifactRole.StateLinkMetadata,
                $"state-original-links/{stateName}"));
        }
    }

    private static string SemanticArtifactIdentity(
        ArtifactArea area,
        ArtifactRole role,
        string relativePath) =>
        $"{area}:{role}:{relativePath}";

    private static string ClaimRelativePath(string transactionId, string subject) =>
        $"claimed-{transactionId}-{Digest(Encoding.UTF8.GetBytes(subject))}";

    private static void VerifyArtifacts(
        string transactionId,
        IReadOnlyList<IntentArtifact> artifacts,
        IReadOnlyList<IntentTransition> activeTransitions,
        string journalDirectory,
        string workDirectory)
    {
        if (artifacts.Count == 0)
            throw new InvalidDataException("Squad prepared transaction has no verified artifacts.");

        if (ContainsTemporaryArtifact(journalDirectory) ||
            (!SquadFileSystemPathSemantics.AreSame(journalDirectory, workDirectory) &&
                ContainsTemporaryArtifact(workDirectory)))
        {
            throw new InvalidDataException(
                "Squad prepared transaction contains an unpublished temporary artifact.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var transitions = new Dictionary<string, IntentTransition>(StringComparer.Ordinal);
        foreach (var transition in activeTransitions)
        {
            if (string.IsNullOrWhiteSpace(transition.ArtifactIdentity) ||
                transition.AllowedState != ArtifactLifecycleState.PreOperation ||
                !transitions.TryAdd(transition.ArtifactIdentity, transition))
            {
                throw new InvalidDataException(
                    "Squad prepared transaction contains an invalid active transition.");
            }
        }

        if (transitions.Count > 1)
        {
            throw new InvalidDataException(
                "Squad prepared transaction contains duplicate or multiple active transitions.");
        }

        foreach (var artifact in artifacts)
        {
            if (!string.Equals(artifact.TransactionId, transactionId, StringComparison.Ordinal) ||
                !ValidateArtifactFingerprint(artifact) ||
                !ArtifactRoleMatchesPath(artifact))
            {
                throw new InvalidDataException(
                    "Squad prepared transaction contains invalid artifact metadata.");
            }

            var relativePath = SquadPathPolicy.NormalizeRelativePath(artifact.Path);
            var identity = ArtifactIdentity(artifact.Area, relativePath);
            if (!string.Equals(relativePath, artifact.Path, StringComparison.Ordinal) ||
                !identities.Add(identity))
            {
                throw new InvalidDataException(
                    "Squad prepared transaction contains duplicate or non-portable artifacts.");
            }

            var areaRoot = artifact.Area == ArtifactArea.Journal
                ? journalDirectory
                : workDirectory;
            var path = ResolveArtifactNode(areaRoot, relativePath);
            var currentKind = GetEntryKind(path);
            var mayBeAbsent = artifact.LifecycleState == ArtifactLifecycleState.ActiveTransition;
            if (artifact.LifecycleState == ArtifactLifecycleState.ActiveTransition)
            {
                if (!transitions.TryGetValue(identity, out var transition) ||
                    transition.AllowedState != ArtifactLifecycleState.PreOperation)
                {
                    throw new InvalidDataException(
                        "Squad prepared transaction has an undeclared active transition.");
                }
            }
            else if (transitions.ContainsKey(identity))
            {
                throw new InvalidDataException(
                    "Squad prepared transaction transition does not match its artifact lifecycle.");
            }

            var expectedPresent = ArtifactExpectedPresent(artifact);
            if (currentKind == OriginalEntryKind.Missing && mayBeAbsent)
                continue;
            if (expectedPresent == false && currentKind != OriginalEntryKind.Missing)
            {
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' has an invalid lifecycle state.");
            }

            if (expectedPresent == false)
                continue;
            if (currentKind != artifact.NodeKind)
            {
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' is missing or has the wrong node kind.");
            }

            if (!ArtifactFingerprintMatches(path, artifact))
            {
                throw new InvalidDataException(
                    $"Squad prepared artifact '{relativePath}' failed length or digest verification.");
            }
        }

        if (transitions.Keys.Any(identity => !identities.Contains(identity)))
        {
            throw new InvalidDataException(
                "Squad prepared transaction transition names an unknown artifact.");
        }

        VerifyExactTransactionTree(artifacts, journalDirectory, workDirectory);
    }

    private static bool ValidateArtifactFingerprint(IntentArtifact artifact) =>
        artifact.NodeKind switch
        {
            OriginalEntryKind.File => artifact.ByteLength >= 0 &&
                IsDigest(artifact.Sha256) && artifact.LinkTarget.Length == 0,
            OriginalEntryKind.FileSymbolicLink or OriginalEntryKind.DirectorySymbolicLink =>
                artifact.ByteLength == 0 && artifact.Sha256.Length == 0 &&
                artifact.LinkTarget.Length > 0,
            OriginalEntryKind.Directory => artifact.ByteLength == 0 &&
                artifact.Sha256.Length == 0 && artifact.LinkTarget.Length == 0,
            OriginalEntryKind.Missing => false,
            _ => false
        };

    private static bool ArtifactFingerprintMatches(string path, IntentArtifact artifact) =>
        artifact.NodeKind switch
        {
            OriginalEntryKind.File =>
                new FileInfo(path).Length == artifact.ByteLength &&
                string.Equals(
                    Digest(File.ReadAllBytes(path)),
                    artifact.Sha256,
                    StringComparison.Ordinal),
            OriginalEntryKind.FileSymbolicLink => string.Equals(
                new FileInfo(path).LinkTarget,
                artifact.LinkTarget,
                StringComparison.Ordinal),
            OriginalEntryKind.DirectorySymbolicLink => string.Equals(
                new DirectoryInfo(path).LinkTarget,
                artifact.LinkTarget,
                StringComparison.Ordinal),
            OriginalEntryKind.Directory => true,
            _ => false
        };

    private static bool? ArtifactExpectedPresent(IntentArtifact artifact) =>
        artifact.LifecycleState switch
        {
            ArtifactLifecycleState.PreOperation => true,
            ArtifactLifecycleState.ActiveTransition => null,
            ArtifactLifecycleState.PostOperation =>
                artifact.Role == ArtifactRole.ClaimedOriginal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifact), artifact.LifecycleState, "Unknown artifact lifecycle.")
        };

    private static string ArtifactIdentity(ArtifactArea area, string relativePath) =>
        $"{area}:{relativePath}";

    private static bool ArtifactRoleMatchesPath(IntentArtifact artifact)
    {
        if (artifact.Role == ArtifactRole.ClaimedOriginal)
        {
            return artifact.Path.StartsWith("claimed-", StringComparison.Ordinal) &&
                artifact.Path.Length > "claimed-".Length;
        }

        var expected = artifact.Role switch
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

    private static void VerifyExactTransactionTree(
        IReadOnlyList<IntentArtifact> artifacts,
        string journalDirectory,
        string workDirectory)
    {
        if (SquadFileSystemPathSemantics.AreSame(journalDirectory, workDirectory))
        {
            VerifyExactTransactionTreeArea(
                workDirectory,
                artifacts,
                excludeIntent: true);
        }
        else
        {
            VerifyExactTransactionTreeArea(
                journalDirectory,
                artifacts.Where(artifact => artifact.Area == ArtifactArea.Journal),
                excludeIntent: true);
            VerifyExactTransactionTreeArea(
                workDirectory,
                artifacts.Where(artifact => artifact.Area == ArtifactArea.Work),
                excludeIntent: false);
        }
    }

    private static void ValidateCreatedDirectoryAuthority(
        IReadOnlyList<CreatedDirectoryAuthority> authorities,
        string targetRoot,
        string stateAuthorityRoot,
        string journalDirectory,
        string workDirectory)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var previousDepth = int.MaxValue;
        foreach (var authority in authorities)
        {
            var normalized = SquadPathPolicy.NormalizeRelativePath(authority.Path);
            var depth = normalized.Count(character => character == '/');
            if (!string.Equals(normalized, authority.Path, StringComparison.Ordinal) ||
                !identities.Add($"{authority.Area}:{normalized}") ||
                depth > previousDepth)
            {
                throw new InvalidDataException(
                    "Squad created-directory authority is duplicate, unordered, or non-portable.");
            }

            previousDepth = depth;
            var authorityRoot = authority.Area == CreatedDirectoryArea.Target
                ? targetRoot
                : stateAuthorityRoot;
            var transactionRoot = authority.Area == CreatedDirectoryArea.Target
                ? workDirectory
                : journalDirectory;
            var directory = SquadPathPolicy.ResolveFile(authorityRoot, normalized);
            if (!SquadFileSystemPathSemantics.AreSame(directory, transactionRoot) &&
                !SquadFileSystemPathSemantics.IsWithin(directory, transactionRoot))
            {
                throw new InvalidDataException(
                    "Squad created-directory authority is not an ancestor of its transaction tree.");
            }
        }
    }

    private static void VerifyExactTransactionTreeArea(
        string root,
        IEnumerable<IntentArtifact> artifacts,
        bool excludeIntent)
    {
        var areaArtifacts = artifacts.ToArray();
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        EnumerateTransactionTree(root, root, actualFiles, actualDirectories);
        if (excludeIntent)
            actualFiles.Remove(IntentFileName);

        var expectedFiles = areaArtifacts.Where(artifact =>
                artifact.NodeKind != OriginalEntryKind.Directory &&
                (ArtifactExpectedPresent(artifact) == true ||
                    (ArtifactExpectedPresent(artifact) is null &&
                        actualFiles.Contains(artifact.Path))))
            .Select(artifact => artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in areaArtifacts
                     .Where(artifact =>
                         ArtifactExpectedPresent(artifact) == true ||
                         (ArtifactExpectedPresent(artifact) is null &&
                             (actualFiles.Contains(artifact.Path) ||
                                 actualDirectories.Contains(artifact.Path)))))
        {
            var path = artifact.Path;
            if (artifact.NodeKind == OriginalEntryKind.Directory)
                expectedDirectories.Add(path);
            var parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                expectedDirectories.Add(parent.Replace(Path.DirectorySeparatorChar, '/'));
                parent = Path.GetDirectoryName(parent);
            }
        }

        if (!actualFiles.SetEquals(expectedFiles) ||
            !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InvalidDataException(
                "Squad prepared artifact authority does not exactly describe its transaction tree.");
        }
    }

    private static void EnumerateTransactionTree(
        string root,
        string directory,
        ISet<string> files,
        ISet<string> directories)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var relativePath = Path.GetRelativePath(root, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.LinkTarget is not null || entry is not DirectoryInfo child)
            {
                files.Add(relativePath);
                continue;
            }

            directories.Add(relativePath);
            EnumerateTransactionTree(root, child.FullName, files, directories);
        }
    }

    private static bool IsTransactionId(string? value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ResolveArtifactNode(string areaRoot, string relativePath)
    {
        var normalized = SquadPathPolicy.NormalizeRelativePath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(
            areaRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var parentRelativePath = Path.GetDirectoryName(
            normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parentRelativePath))
        {
            _ = SquadPathPolicy.ResolveFile(
                areaRoot,
                Path.Combine(parentRelativePath, ".authority-sentinel")
                    .Replace(Path.DirectorySeparatorChar, '/'));
        }

        return fullPath;
    }

    private static bool ContainsTemporaryArtifact(string directory) =>
        Directory.Exists(directory) &&
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase));

    private static bool IsDigest(string? digest) =>
        digest is { Length: 64 } &&
        digest.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string StateOriginalPath(string workDirectory, string name) =>
        Path.Combine(workDirectory, "state-originals", name);

    private static string StateOriginalLinkPath(string workDirectory, string name) =>
        Path.Combine(workDirectory, "state-original-links", name);

    private static string StateStagingPath(string journalDirectory, string name) =>
        Path.Combine(journalDirectory, "state-staging", name);

    private static string TransactionFilePath(
        string workDirectory,
        string category,
        string relativePath)
    {
        var categoryRoot = Path.Combine(workDirectory, category);
        return SquadPathPolicy.ResolveFile(categoryRoot, relativePath);
    }

    private static string Digest(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static SquadDeploymentConflictException PreconditionConflict(string relativePath) =>
        new(
            $"Squad deployment path '{relativePath}' changed after preflight. " +
            "Retry the operation so ownership can be checked against the current filesystem state.");

    private static LeafClaimConflictException ClaimConflict(string relativePath) =>
        new(
            $"Squad deployment leaf '{relativePath}' changed at the atomic claim boundary. " +
            "The raced node was preserved and the transaction journal was retained.");

    private static JsonSerializerOptions CreateIntentJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private void Notify(
        ref int sequence,
        SquadTransactionStepKind kind,
        string? relativePath = null)
    {
        sequence++;
        _observer?.AfterStep(new SquadTransactionStep(sequence, kind, relativePath));
    }

    private static void NotifyCheckpoint(
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

    private sealed record IntentFile(
        string RelativePath,
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
        Prepared
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

    private enum CreatedDirectoryArea
    {
        Target,
        State
    }

    private sealed class TransactionLease(string identity, Mutex mutex) : IDisposable
    {
        private bool _disposed;
        private FileStream? _journal;

        public void HoldJournal(string journalPath)
        {
            _journal = new FileStream(
                journalPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read);
        }

        public void ReleaseJournal()
        {
            _journal?.Dispose();
            _journal = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            ReleaseJournal();
            mutex.ReleaseMutex();
            mutex.Dispose();
            ActiveLeases.TryRemove(identity, out _);
            _disposed = true;
        }
    }

    private sealed class LeafClaimConflictException : InvalidOperationException
    {
        public LeafClaimConflictException()
        {
        }

        public LeafClaimConflictException(string message)
            : base(message)
        {
        }

        public LeafClaimConflictException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
