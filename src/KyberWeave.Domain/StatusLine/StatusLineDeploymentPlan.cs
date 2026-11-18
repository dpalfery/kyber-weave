namespace KyberWeave.Domain.StatusLine;

/// <summary>A preflighted set of status-line file mutations for one harness.</summary>
/// <remarks>
/// Mirrors <c>SquadDeploymentPlan</c>'s shape: build a plan, inspect it, then apply it. This type is the plan as data;
/// building it and applying it are <c>IStatusLineDeploymentService</c>, which owns the file system. A plan is built
/// from injected roots (<c>StatusLineTargetRoots</c>) rather than from process state, so a host and a test
/// drive the whole deploy/remove surface the same way. Every mutation is confined to the harness staging root and
/// cross-checked against that harness's settings paths and auto-load directories, because Kyber Utilities never
/// writes a harness settings file (A4, C2) and never activates a status line on the user's behalf (A3, C1).
///
/// <para>
/// Building a plan is the dry run: it reads the receipt and the target paths and writes nothing, not even a
/// directory. Only applying it changes the filesystem.
/// </para>
/// </remarks>
public sealed class StatusLineDeploymentPlan
{
    /// <summary>Creates a plan. Only the service that preflights a deployment builds one.</summary>
    internal StatusLineDeploymentPlan(
        StatusLineTarget target,
        string stagingRoot,
        string receiptPath,
        StatusLineReceipt receipt,
        IReadOnlyList<StatusLinePendingChange> pendingChanges)
    {
        Target = target;
        StagingRoot = stagingRoot;
        ReceiptPath = receiptPath;
        Receipt = receipt;
        PendingChanges = pendingChanges;
        PlannedFileChanges =
        [
            .. pendingChanges.Select(change =>
                new StatusLinePlannedFileChange(change.RelativePath, change.Kind))
        ];
        PlannedPhysicalPaths = [.. pendingChanges.Select(change => change.PhysicalPath)];
    }

    /// <summary>The harness this plan deploys or removes.</summary>
    public StatusLineTarget Target { get; }

    /// <summary>The Kyber-owned staging root every planned path lives beneath.</summary>
    public string StagingRoot { get; }

    /// <summary>
    /// The receipt this plan will persist: the deployed files for a deploy, and, for a removal, the
    /// files it keeps because they were edited locally.
    /// </summary>
    public StatusLineReceipt Receipt { get; }

    /// <summary>The pending file changes, in apply order, without payloads.</summary>
    public IReadOnlyList<StatusLinePlannedFileChange> PlannedFileChanges { get; }

    /// <summary>
    /// The absolute paths this plan will touch. Public so a caller, and a test, can prove that no
    /// harness settings path is ever among them.
    /// </summary>
    public IReadOnlyList<string> PlannedPhysicalPaths { get; }

    /// <summary>The absolute path of the receipt file this plan persists, whether or not it exists.</summary>
    internal string ReceiptPath { get; }

    /// <summary>The pending changes with their payloads, in apply order.</summary>
    internal IReadOnlyList<StatusLinePendingChange> PendingChanges { get; }
}

/// <summary>
/// A pending write or delete, with the payload a write needs. Internal because a host reports
/// <see cref="StatusLineDeploymentPlan.PlannedFileChanges"/> rather than the bytes.
/// </summary>
/// <param name="RelativePath">The path relative to the staging root.</param>
/// <param name="PhysicalPath">The absolute path the change touches.</param>
/// <param name="Kind">Whether the change writes or deletes.</param>
/// <param name="Content">The bytes a write puts there; empty for a delete.</param>
internal sealed record StatusLinePendingChange(
    string RelativePath,
    string PhysicalPath,
    StatusLineFileChangeKind Kind,
    ReadOnlyMemory<byte> Content);
