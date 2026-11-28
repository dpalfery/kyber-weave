

namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the working tree a dispatch's plan lives in.</summary>
public interface IPlanRootResolver
{
    /// <summary>
    /// The root to evaluate <paramref name="prompt"/> against: <paramref name="hookRoot"/> when the plan exists
    /// there or nothing better resolves, otherwise the one worktree that holds the plan.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="prompt">The dispatch prompt, headers included when it still has them.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees; <c>GitFacts.ListWorktrees</c> in production.</param>
    string Resolve(string hookRoot, string prompt, Func<string, IReadOnlyList<string>>? listWorktrees = null);

    /// <summary>
    /// The root to read a dispatch's plan, task file and prompt against when its headers are already parsed:
    /// <paramref name="hookRoot"/> when the plan exists there or nothing better resolves, otherwise the one
    /// worktree that holds the plan. The same choice <see cref="Resolve"/> makes, without re-parsing a prompt.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="planFile">The dispatch's <c>PLAN_FILE</c> value, or null when it names no plan.</param>
    /// <param name="text">
    /// The dispatch text, for the worktree it names when the dispatch carries no plan header; null when there is
    /// none to read.
    /// </param>
    /// <param name="listWorktrees">Lists the repository's worktrees; <c>GitFacts.ListWorktrees</c> in production.</param>
    string ResolvePlanRoot(
        string hookRoot,
        string? planFile,
        string? text,
        Func<string, IReadOnlyList<string>>? listWorktrees = null);

    /// <summary>
    /// The root to evaluate <paramref name="prompt"/> against, without blocking a thread while the worktree list is read.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="prompt">The dispatch prompt, headers included when it still has them.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees without blocking.</param>
    /// <param name="cancellationToken">Cancels the worktree-list read.</param>
    Task<string> ResolveAsync(
        string hookRoot,
        string prompt,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? listWorktrees = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The root to read a dispatch's plan, task file and prompt against when its headers are already parsed,
    /// without blocking a thread while the worktree list is read.
    /// </summary>
    /// <param name="hookRoot">The repository root the hook resolved from its working directory.</param>
    /// <param name="planFile">The dispatch's <c>PLAN_FILE</c> value, or null when it names no plan.</param>
    /// <param name="text">The dispatch text; null when there is none to read.</param>
    /// <param name="listWorktrees">Lists the repository's worktrees without blocking.</param>
    /// <param name="cancellationToken">Cancels the worktree-list read.</param>
    Task<string> ResolvePlanRootAsync(
        string hookRoot,
        string? planFile,
        string? text,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? listWorktrees = null,
        CancellationToken cancellationToken = default);
}
