namespace KyberWeave.Core.RunStart;

/// <summary>One run <c>run start</c> recorded: the repository, its branch, and where it works.</summary>
/// <param name="Repository">The repository's common git directory, absolute.</param>
/// <param name="Branch">The run's branch.</param>
/// <param name="Worktree">The directory the run works in.</param>
/// <param name="StartedAt">When the run started; null on a record written before <c>run start</c> recorded it.</param>
public sealed record RunRecord(string Repository, string Branch, string Worktree, DateTimeOffset? StartedAt = null);

/// <summary>Where a run started: the facts <c>run start</c> records beside the base, read back by the dispatch guard.</summary>
/// <param name="Repository">The repository's common git directory, absolute.</param>
/// <param name="Worktree">The directory the run works in: its worktree, or the checkout it branched in place.</param>
/// <param name="StartedAt">When the run started.</param>
public sealed record RunStartPlace(string Repository, string Worktree, DateTimeOffset StartedAt);
