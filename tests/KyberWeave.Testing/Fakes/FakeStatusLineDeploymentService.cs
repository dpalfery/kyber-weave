using KyberWeave.Contracts.Services;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Tests.Fakes;

/// <summary>The status-line deployment service in a unit test: the receipts and plans a test states, and no disk.</summary>
/// <remarks>
/// Plans are built here rather than scripted whole, so a case states only what the command reports: the files a remove
/// deletes, the files a removal keeps, and the artifacts a deploy stages. Every call the command makes is recorded, so a
/// dry run can assert that nothing was applied.
/// </remarks>
public sealed class FakeStatusLineDeploymentService : IStatusLineDeploymentService
{
    private readonly Dictionary<StatusLineTarget, StatusLineReceipt> _receipts = [];
    private readonly Dictionary<StatusLineTarget, Exception> _readFailures = [];
    private readonly Dictionary<StatusLineTarget, IReadOnlyList<string>> _removals = [];
    private readonly Dictionary<StatusLineTarget, StatusLineReceipt> _retained = [];
    private readonly List<StatusLineDeploymentPlan> _applied = [];
    private readonly List<(StatusLineTarget Target, IReadOnlyList<StatusLineDeploymentFile> Files)> _deployRequests = [];

    /// <summary>The failure <see cref="Apply"/> throws, or null when it records the plan and returns.</summary>
    public Exception? ApplyFailure { get; set; }

    /// <summary>The plans <see cref="Apply"/> was given, in call order.</summary>
    public IReadOnlyList<StatusLineDeploymentPlan> Applied => _applied;

    /// <summary>Each <see cref="CreateDeploy"/> call, as the target and the artifacts it was given.</summary>
    public IReadOnlyList<(StatusLineTarget Target, IReadOnlyList<StatusLineDeploymentFile> Files)> DeployRequests =>
        _deployRequests;

    /// <summary>States the receipt <see cref="ReadReceipt"/> answers with for <paramref name="target"/>.</summary>
    /// <param name="target">The harness the receipt belongs to.</param>
    /// <param name="files">The owned files the receipt records.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeStatusLineDeploymentService WithReceipt(StatusLineTarget target, params StatusLineOwnedFile[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _receipts[target] = new StatusLineReceipt(files);
        return this;
    }

    /// <summary>Makes <see cref="ReadReceipt"/> throw, as a malformed receipt on disk does.</summary>
    /// <param name="target">The harness whose receipt is unreadable.</param>
    /// <param name="failure">The exception to throw.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeStatusLineDeploymentService WithReadFailure(StatusLineTarget target, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _readFailures[target] = failure;
        return this;
    }

    /// <summary>States what a removal deletes and what it keeps because the file was edited locally.</summary>
    /// <param name="target">The harness being removed.</param>
    /// <param name="deletedRelativePaths">The portable relative paths the plan deletes.</param>
    /// <param name="retained">The owned files the plan keeps.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeStatusLineDeploymentService WithRemoval(
        StatusLineTarget target,
        IReadOnlyList<string> deletedRelativePaths,
        params StatusLineOwnedFile[] retained)
    {
        ArgumentNullException.ThrowIfNull(deletedRelativePaths);
        ArgumentNullException.ThrowIfNull(retained);
        _removals[target] = deletedRelativePaths;
        _retained[target] = new StatusLineReceipt(retained);
        return this;
    }

    /// <inheritdoc />
    public StatusLineDeploymentPlan CreateDeploy(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        IReadOnlyList<StatusLineDeploymentFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _deployRequests.Add((target, files));
        return Plan(
            target,
            roots.ResolveStagingRoot(target),
            new StatusLineReceipt([.. files.Select(file => new StatusLineOwnedFile(file.RelativePath, string.Empty, target))]),
            [.. files.Select(file => (file.RelativePath, StatusLineFileChangeKind.Write))]);
    }

    /// <inheritdoc />
    public StatusLineDeploymentPlan CreateRemove(StatusLineTarget target, StatusLineTargetRoots roots)
    {
        IReadOnlyList<string> deleted = _removals.TryGetValue(target, out IReadOnlyList<string>? paths) ? paths : [];
        StatusLineReceipt retained = _retained.TryGetValue(target, out StatusLineReceipt? receipt)
            ? receipt
            : new StatusLineReceipt([]);
        return Plan(
            target,
            roots.ResolveStagingRoot(target),
            retained,
            [.. deleted.Select(relativePath => (relativePath, StatusLineFileChangeKind.Delete))]);
    }

    /// <inheritdoc />
    public StatusLineReceipt? ReadReceipt(StatusLineTarget target, StatusLineTargetRoots roots)
    {
        if (_readFailures.TryGetValue(target, out Exception? failure)) throw failure;
        return _receipts.TryGetValue(target, out StatusLineReceipt? receipt) ? receipt : null;
    }

    /// <inheritdoc />
    public void Apply(StatusLineDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (ApplyFailure is not null) throw ApplyFailure;
        _applied.Add(plan);
    }

    private static StatusLineDeploymentPlan Plan(
        StatusLineTarget target,
        string stagingRoot,
        StatusLineReceipt receipt,
        IReadOnlyList<(string RelativePath, StatusLineFileChangeKind Kind)> changes)
    {
        return new StatusLineDeploymentPlan(
            target,
            stagingRoot,
            stagingRoot + "/.statusline-receipt.json",
            receipt,
            [.. changes.Select(change => new StatusLinePendingChange(
                change.RelativePath,
                stagingRoot + "/" + change.RelativePath,
                change.Kind,
                ReadOnlyMemory<byte>.Empty))]);
    }
}
