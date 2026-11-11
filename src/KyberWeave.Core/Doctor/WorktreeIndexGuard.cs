using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

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
/// <param name="Created">True when this call created the index with <c>codegraph init</c> and it is ready now.</param>
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
    /// <param name="restore">
    ///     Which tracked files the index run restores. The default restores every written file; the post-checkout hook
    ///     passes <see cref="CodeGraphRestore.InitConfigFilesOnly"/>. Passed through to the provisioner.
    /// </param>
    public static WorktreeIndexOutcome Ensure(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        bool governed;
        try
        {
            governed = DocsEnabled.IsEnabled(root, ReadText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present but unreadable is not absent: report it, so the caller neither crashes nor skips the index.
            DoctorCheck unreadable = new(
                "codegraph.index",
                false,
                $"the repository configuration could not be read: {ex.Message}",
                $"make .kyber-weave/kyber-weave.yml readable, then run: kyber-weave doctor --ensure {ShellQuote.Quote(root)}");
            return new WorktreeIndexOutcome(
                WorktreeIndexStatus.Failed,
                Created: false,
                StopsRun: RepositoryDoctor.StopsOnToolFailure(root),
                [unreadable]);
        }

        if (!governed)
            return new WorktreeIndexOutcome(WorktreeIndexStatus.NotGoverned, Created: false, StopsRun: false, []);

        CodeGraphOutcome outcome = CodeGraphProvisioner.Run(root, DoctorMode.Ensure, options, executor, lockDirectory, restore);

        WorktreeIndexStatus status = outcome.BinaryMissing
            ? WorktreeIndexStatus.BinaryMissing
            : outcome.Checks.All(check => check.Passed) ? WorktreeIndexStatus.Ready : WorktreeIndexStatus.Failed;

        bool stops = (status is WorktreeIndexStatus.Failed or WorktreeIndexStatus.BinaryMissing)
                     && RepositoryDoctor.StopsOnToolFailure(root);

        return new WorktreeIndexOutcome(
            status,
            Created: outcome.Initialised && status is WorktreeIndexStatus.Ready,
            StopsRun: stops,
            outcome.Checks);
    }

    /// <summary>Reads a file, treating one that is not there as absent and letting an unreadable one throw.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
