using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Application.Doctor;

/// <summary>
///     Makes sure a repository tree has a usable CodeGraph index before a run or a review works in it.
/// </summary>
/// <remarks>
///     A git worktree starts with no index, because <c>.codegraph/</c> is untracked, and nothing else builds one. This
///     guard reuses <see cref="ICodeGraphProvisioner"/> in ensure mode, so the verbs, the lock and the repair are the
///     ones <c>doctor --ensure</c> uses. It checks the index only; the MCP docs server is not probed here.
/// </remarks>
public sealed class WorktreeIndexEnsurer(
    ICodeGraphProvisioner provisioner,
    IDoctorFileSystem files,
    IRepositoryToolSettings settings) : IWorktreeIndexGuard
{
    /// <summary>Creates, syncs or proves the index without blocking a thread, unless the repository is not governed.</summary>
    /// <param name="root">The tree whose index is wanted: a worktree, or the checkout itself.</param>
    /// <param name="options">The index budget and the CodeGraph executable name.</param>
    /// <param name="executor">Runs the CodeGraph child, bounded by the index timeout.</param>
    /// <param name="lockDirectory">Where the index lock file lives.</param>
    /// <param name="restore">Which tracked files the index run restores.</param>
    /// <param name="cancellationToken">Cancels the index and git children.</param>
    public async Task<WorktreeIndexOutcome> EnsureAsync(
        string root,
        DoctorOptions options,
        IProcessRunner executor,
        string lockDirectory,
        CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);

        bool governed;
        try
        {
            governed = DocsEnabled.IsEnabled(root, ReadText, files.DirectoryExists);
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
                StopsRun: settings.Read(root).StopsOnToolFailure,
                [unreadable]);
        }

        if (!governed)
            return new WorktreeIndexOutcome(WorktreeIndexStatus.NotGoverned, Created: false, StopsRun: false, []);

        CodeGraphOutcome outcome = await provisioner.RunAsync(
            root,
            DoctorMode.Ensure,
            options,
            executor,
            lockDirectory,
            restore,
            cancellationToken).ConfigureAwait(false);

        WorktreeIndexStatus status = outcome.BinaryMissing
            ? WorktreeIndexStatus.BinaryMissing
            : outcome.Checks.All(check => check.Passed) ? WorktreeIndexStatus.Ready : WorktreeIndexStatus.Failed;

        bool stops = (status is WorktreeIndexStatus.Failed or WorktreeIndexStatus.BinaryMissing)
                     && settings.Read(root).StopsOnToolFailure;

        return new WorktreeIndexOutcome(
            status,
            Created: outcome.Initialised && status is WorktreeIndexStatus.Ready,
            StopsRun: stops,
            outcome.Checks);
    }

    /// <summary>Reads a file, treating one that is not there as absent and letting an unreadable one throw.</summary>
    private string? ReadText(string path)
    {
        try
        {
            return files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
