using KyberWeave.Domain.Doctor;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>Makes sure a repository tree has a usable CodeGraph index before a run or a review works in it.</summary>
public interface IWorktreeIndexGuard
{
    /// <summary>Creates, syncs or proves the index of a tree, unless the repository is not governed.</summary>
    /// <param name="root">The tree whose index is wanted: a worktree, or the checkout itself.</param>
    /// <param name="options">The index budget and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child, bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the index lock file lives.</param>
    /// <param name="restore">Which tracked files the index run restores.</param>
    /// <returns>The index state and the checks to report.</returns>
    WorktreeIndexOutcome Ensure(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles);
}
