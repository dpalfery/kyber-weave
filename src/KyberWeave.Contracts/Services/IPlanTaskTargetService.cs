using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Finds a task by the id a worker typed, and what its tests are: the one lookup <c>docs packet</c> prints the test
/// command from and <c>review gates --task</c> runs it from, so the two never disagree.
/// </summary>
public interface IPlanTaskTargetService
{
    /// <summary>
    /// The task in this checkout's plans — the one in flight first, then the ones still in review — or in another
    /// worktree's active plan, or null with the reason when there is not exactly one match.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="typedTask">The task id as the worker typed it, for example <c>T1</c>.</param>
    /// <param name="reason">Why nothing was found; null when a task was found.</param>
    PlanTaskTargetMatch? Find(string root, KyberWeaveConfig config, string typedTask, out string? reason);

    /// <summary>
    /// The task in this checkout's plans — the one in flight first, then the ones still in review — or in another
    /// worktree's active plan, or null with the reason when there is not exactly one match, without blocking a thread.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="typedTask">The task id as the worker typed it, for example <c>T1</c>.</param>
    /// <param name="cancellationToken">Cancels the task-file reads.</param>
    Task<PlanTaskTargetLookup> FindAsync(
        string root,
        KyberWeaveConfig config,
        string typedTask,
        CancellationToken cancellationToken = default);

    /// <summary>The task's Test contract row in <paramref name="plan"/>, or null when it has none or the plan cannot be read.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="plan">The plan, repository-relative.</param>
    /// <param name="taskId">The task's id, or null.</param>
    PlanContractRow? ContractRow(string root, string plan, string? taskId);

    /// <summary>
    /// The task's own files as the gate takes them: literal paths only (a glob names no file) that pass the gate's path
    /// check. Files the task has not created yet are kept, because a test-first task's failing run comes before them.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="entry">The task.</param>
    /// <param name="row">
    /// The task's Test contract row. A test file it names is the task's own too, even when the task's file list leaves
    /// it out, so a compile error in it reads as this task's failing run.
    /// </param>
    IReadOnlyList<string> Files(string root, PlanTaskFileEntry entry, PlanContractRow? row = null);

    /// <summary>The test classes the task's Test contract row names in a <c>FullyQualifiedName</c> filter, in order.</summary>
    /// <param name="row">The task's Test contract row, or null when the plan has none for it.</param>
    IReadOnlyList<string> DeclaredClasses(PlanContractRow? row);
}

/// <summary>The task a lookup found, or null with the reason there was not exactly one match.</summary>
/// <param name="Match">The task and the plan it belongs to, or null when nothing matched.</param>
/// <param name="Reason">Why nothing was found; null when <paramref name="Match"/> is not null.</param>
public sealed record PlanTaskTargetLookup(PlanTaskTargetMatch? Match, string? Reason);
