namespace KyberWeave.Domain.Doctor;

/// <summary>The state a repository tree's CodeGraph index is in after a worktree index guard run.</summary>
public enum WorktreeIndexStatus
{
    /// <summary>The index exists and answers.</summary>
    Ready,

    /// <summary>The repository has no governed docs, so it has no index to keep.</summary>
    NotGoverned,

    /// <summary>The <c>codegraph</c> executable is not on PATH.</summary>
    BinaryMissing,

    /// <summary>The index could not be created, synced or proved.</summary>
    Failed,
}
