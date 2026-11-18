using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A preflighted set of file and state mutations for one Squad lifecycle operation.</summary>
public sealed class SquadDeploymentPlan
{

    public SquadDeploymentPlan(
        string targetRoot,
        SquadPhysicalRootIdentity physicalRootIdentity,
        SquadDeploymentScope scope,
        SquadLock? squadLock,
        SquadReceipt receipt,
        IReadOnlyList<SquadFileMutation> fileMutations,
        IReadOnlyList<SquadFilePrecondition> filePreconditions,
        SquadStateMutation lockMutation,
        SquadStateMutation receiptMutation,
        ISquadGlobalRootResolver? globalRoots = null,
        bool isSingleRootLayout = false,
        IReadOnlyList<SquadOwnedBlockDrift>? blockDrifts = null,
        IReadOnlyList<SquadDegradedFile>? degradedFiles = null)
    {
        TargetRoot = targetRoot;
        PhysicalRootPath = physicalRootIdentity.PhysicalPath;
        PhysicalRootKey = physicalRootIdentity.Key;
        Scope = scope;
        Lock = squadLock;
        Receipt = receipt;
        FileMutations = fileMutations;
        PlannedFileChanges =
        [
            .. fileMutations.Select(mutation =>
                new SquadPlannedFileChange(mutation.RelativePath, mutation.Target, mutation.Kind))
        ];
        FilePreconditions = filePreconditions;
        LockMutation = lockMutation;
        ReceiptMutation = receiptMutation;
        GlobalRoots = globalRoots;
        IsSingleRootLayout = isSingleRootLayout;
        BlockDrifts = blockDrifts ?? [];
        DegradedFiles = degradedFiles ?? [];
    }

    /// <summary>The absolute root into which harness-native files are deployed.</summary>
    public string TargetRoot { get; }

    public string PhysicalRootPath { get; }

    public string PhysicalRootKey { get; }

    /// <summary>The state scope for this deployment.</summary>
    public SquadDeploymentScope Scope { get; }

    /// <summary>The desired lock, or <see langword="null" /> for an uninstall.</summary>
    public SquadLock? Lock { get; }

    /// <summary>
    ///     The desired or retained ownership receipt. A transaction anchors it to its canonical root before
    ///     the first write, so after execution it names exactly what was written.
    /// </summary>
    public SquadReceipt Receipt { get; internal set; }

    /// <summary>
    ///     The pending file mutations, in apply order. Internal payload: hosts report
    ///     <see cref="PlannedFileChanges" /> instead.
    /// </summary>
    public IReadOnlyList<SquadFileMutation> FileMutations { get; }

    /// <summary>
    ///     The pending file changes, in apply order, without payloads. Public so command hosts
    ///     can report what a plan will change — an uninstall plan's <see cref="Receipt" /> holds
    ///     only the files it retains, so the planned removals are readable from this list alone.
    /// </summary>
    public IReadOnlyList<SquadPlannedFileChange> PlannedFileChanges { get; }

    /// <summary>
    ///     Deployed files this plan could not read and therefore kept on trust rather than verifying, each
    ///     with the raw target token that produced it. Empty for a plan whose every file was read and checked.
    /// </summary>
    public IReadOnlyList<SquadDegradedFile> DegradedFiles { get; }

    public IReadOnlyList<SquadFilePrecondition> FilePreconditions { get; }

    /// <summary>
    /// The owned block entries whose current file content no longer matches the receipt at
    /// plan time. Update preserves these entries — rewriting only the blocks without drift —
    /// unless the caller passes <c>replaceManaged</c>, so the operator can see what was
    /// kept and why.
    /// </summary>
    public IReadOnlyList<SquadOwnedBlockDrift> BlockDrifts { get; }

    public SquadStateMutation LockMutation { get; }

    public SquadStateMutation ReceiptMutation { get; }

    /// <summary>The resolver of each target's global root, or null when the plan has none.</summary>
    internal ISquadGlobalRootResolver? GlobalRoots { get; }

    /// <summary>Whether a global plan keeps every target under one root, as receipts written before the per-target layout did.</summary>
    internal bool IsSingleRootLayout { get; }
}

/// <summary>
///     One unmanaged file whose name matches a canonical Squad identity and whose
///     bytes do not match the current render. Doctor lists these; install refuses them.
/// </summary>
public sealed record SquadUnmanagedPathCollision(
    string RelativePath,
    string FullPath,
    string Target,
    string Identity);

/// <summary>
///     Whether a planned file change writes new bytes or removes a deployed file. Public so
///     command hosts can report what a plan will do without seeing the internal payload record.
/// </summary>
public enum SquadFileMutationKind
{
    Write,
    Delete
}

/// <summary>A deployed file a plan kept on trust, with the raw target token that produced it.</summary>
public sealed record SquadDegradedFile(string RelativePath, string Target);

/// <summary>
///     One planned file change without its payload, for command hosts that report what a plan
///     will do. An uninstall plan's receipt holds only the files it retains, so the removals it
///     plans are readable from these changes alone.
/// </summary>
public sealed record SquadPlannedFileChange(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind);

/// <summary>
///     A pending write or delete for one deployed file, identified by <c>(Target, RelativePath)</c>
///     so that Global scope's per-target physical roots (see <c>SquadDeploymentPlanRules.ResolvePhysicalRoot</c>)
///     can be resolved without cross-referencing the receipt by relative path alone.
/// </summary>
public sealed record SquadFileMutation(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind,
    ReadOnlyMemory<byte>? Content)
{
    public static SquadFileMutation Write(
        string relativePath,
        string target,
        ReadOnlyMemory<byte> content)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Write, content);
    }

    public static SquadFileMutation Delete(string relativePath, string target)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Delete, null);
    }
}

public enum SquadStateMutation
{
    Keep,
    Write,
    Delete
}

public enum SquadFilePreconditionKind
{
    Missing,
    ExactFile
}

public sealed record SquadFilePrecondition(
    string RelativePath,
    string Target,
    SquadFilePreconditionKind Kind,
    string? Sha256)
{
    public static SquadFilePrecondition Missing(string relativePath, string target)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.Missing, null);
    }

    public static SquadFilePrecondition Exact(string relativePath, string target, string sha256)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.ExactFile, sha256);
    }
}
