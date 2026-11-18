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
    public SquadReceipt Receipt { get; set; }

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
    public ISquadGlobalRootResolver? GlobalRoots { get; }

    /// <summary>Whether a global plan keeps every target under one root, as receipts written before the per-target layout did.</summary>
    public bool IsSingleRootLayout { get; }
}
