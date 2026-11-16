namespace KyberWeave.Domain.Doctor;

/// <summary>The two repository settings the doctor acts on.</summary>
/// <param name="StopsOnToolFailure">Whether a run stops when a required tool cannot be repaired. Absent means true.</param>
/// <param name="WorktreeIndexHook">Whether the git post-checkout hook indexes a new worktree. Absent means true.</param>
public sealed record RepositoryToolSettings(bool StopsOnToolFailure = true, bool WorktreeIndexHook = true);
