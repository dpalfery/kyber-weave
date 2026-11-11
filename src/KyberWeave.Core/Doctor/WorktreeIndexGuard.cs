using KyberWeave.Cli.Commands.Squad.Infrastructure;

namespace KyberWeave.Core.Doctor;

/// <summary>The state a repository tree's CodeGraph index is in after <see cref="WorktreeIndexGuard.Ensure"/>.</summary>
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

/// <summary>The result of ensuring one tree's CodeGraph index.</summary>
/// <param name="Status">The index state.</param>
/// <param name="Created">True when this call created the index: it was absent before and is ready now.</param>
/// <param name="StopsRun">True when the caller must stop: the index is unusable and the repository stops on tool failure.</param>
/// <param name="Checks">The <c>codegraph.*</c> checks, in order, for the caller to report.</param>
public sealed record WorktreeIndexOutcome(
    WorktreeIndexStatus Status,
    bool Created,
    bool StopsRun,
    IReadOnlyList<DoctorCheck> Checks);

/// <summary>
///     Makes sure a repository tree has a usable CodeGraph index before a run or a review works in it.
/// </summary>
/// <remarks>
///     A git worktree starts with no index, because <c>.codegraph/</c> is untracked, and nothing else builds one. This
///     guard reuses <see cref="CodeGraphProvisioner"/> in ensure mode, so the verbs, the lock and the repair are the
///     ones <c>doctor --ensure</c> uses. It checks the index only; the MCP docs server is not probed here.
/// </remarks>
public static class WorktreeIndexGuard
{
    /// <summary>Creates, syncs or proves the index of <paramref name="root"/>, unless the repository is not governed.</summary>
    /// <param name="root">The tree whose index is wanted: a worktree, or the checkout itself.</param>
    /// <param name="options">The index budget and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child. Production passes an executor bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the index lock file lives.</param>
    public static WorktreeIndexOutcome Ensure(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        if (!DocsEnabled.IsEnabled(root, ReadText))
            return new WorktreeIndexOutcome(WorktreeIndexStatus.NotGoverned, Created: false, StopsRun: false, []);

        bool existed = Directory.Exists(Path.Combine(root, CodeGraphProvisioner.IndexDirectoryName));
        CodeGraphOutcome outcome = CodeGraphProvisioner.Run(root, DoctorMode.Ensure, options, executor, lockDirectory);

        WorktreeIndexStatus status = outcome.BinaryMissing
            ? WorktreeIndexStatus.BinaryMissing
            : outcome.Checks.All(check => check.Passed) ? WorktreeIndexStatus.Ready : WorktreeIndexStatus.Failed;

        bool stops = (status is WorktreeIndexStatus.Failed or WorktreeIndexStatus.BinaryMissing)
                     && RepositoryDoctor.StopsOnToolFailure(root);

        return new WorktreeIndexOutcome(
            status,
            Created: !existed && status is WorktreeIndexStatus.Ready,
            StopsRun: stops,
            outcome.Checks);
    }

    private static string? ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
