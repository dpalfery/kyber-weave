using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Application.RunIntegrate;

/// <summary>
/// Brings one developer task's commits from its own worktree onto the run's branch, under a lock, then keeps the
/// task's scratchpad and removes the worktree it came from.
/// </summary>
/// <remarks>
/// <para>
/// A task worktree is cut from the run worktree, so the run branch has moved on by the time the task finishes.
/// The task's own commits are the work; everything else here is about making the move deterministic: the run
/// worktree is resolved from the run record <c>run start</c> wrote rather than guessed, the integration holds one
/// exclusive lock per run worktree so parallel tasks arrive one at a time, and no git command ever runs in the
/// repository's canonical checkout.
/// </para>
/// <para>
/// Every git call goes through <see cref="IGitCommandRunner" /> with an explicit <c>-C</c> naming the worktree it
/// targets, so a caller standing anywhere integrates the same two checkouts.
/// </para>
/// </remarks>
public sealed class RunIntegrateService : IRunIntegrateService
{
    /// <summary>The lock file every integration of one run worktree shares, inside that worktree's git directory.</summary>
    public const string LockFileName = "kyber-weave-run-integrate.lock";

    private const string ScratchpadFolder = ".agent_scratchpad";
    private const string GitDirectoryOption = "--absolute-git-dir";
    private const string CommonDirectoryOption = "--git-common-dir";
    private const string UnmergedFiles = "--diff-filter=U";

    /// <summary>How long an integration waits for a parallel worker's lock before failing.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(120);

    /// <summary>How often a waiting integration retries the lock.</summary>
    public static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How old a lock must be before a waiting integration takes it over.</summary>
    public static readonly TimeSpan StaleLockAge = TimeSpan.FromMinutes(10);

    private readonly IGitCommandRunner _git;
    private readonly IRunCheckout _checkouts;
    private readonly IWritableFileSystem _files;
    private readonly IExclusiveFileWriter _exclusive;
    private readonly IProcessEnvironment _environment;
    private readonly IPathResolver _paths;
    private readonly TimeProvider _time;

    /// <summary>Creates the integration over the git, file-system and run-record seams.</summary>
    /// <param name="git">Runs every git child, bounded by its own timeout.</param>
    /// <param name="checkouts">Finds the live run record for the task worktree's repository.</param>
    /// <param name="files">Reads and writes the evidence folder and the lock file.</param>
    /// <param name="exclusive">Takes the lock with an atomic create, so two workers cannot both win it.</param>
    /// <param name="environment">The operator's home and the current directory.</param>
    /// <param name="paths">Turns the paths the caller gave into absolute ones.</param>
    /// <param name="time">The clock the stale-lock age and the wait use.</param>
    public RunIntegrateService(
        IGitCommandRunner git,
        IRunCheckout checkouts,
        IWritableFileSystem files,
        IExclusiveFileWriter exclusive,
        IProcessEnvironment environment,
        IPathResolver paths,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(checkouts);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(exclusive);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);

        _git = git;
        _checkouts = checkouts;
        _files = files;
        _exclusive = exclusive;
        _environment = environment;
        _paths = paths;
        _time = time;
    }

    /// <inheritdoc />
    public RunIntegrateOutcome Integrate(RunIntegrateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryTaskId(request.TaskId, out string taskId, out RunIntegrateOutcome? refused))
        {
            return refused!;
        }

        List<string> notes = [];
        string taskWorktree = _paths.GetFullPath(
            string.IsNullOrWhiteSpace(request.TaskWorktree) ? request.WorkingDirectory : request.TaskWorktree,
            request.WorkingDirectory);
        if (!_files.DirectoryExists(taskWorktree))
        {
            return Refuse($"the task worktree '{taskWorktree}' does not exist.");
        }

        string? taskTop = GitText(taskWorktree, "rev-parse", "--show-toplevel");
        if (taskTop is null)
        {
            return Refuse($"the task worktree '{taskWorktree}' is not inside a git repository.");
        }

        taskWorktree = taskTop;
        string? taskCommon = CommonDirectoryOf(taskWorktree);
        if (taskCommon is null)
        {
            return Refuse($"the task worktree '{taskWorktree}' is not inside a git repository.");
        }

        if (!TryRunWorktree(request, taskWorktree, taskCommon, notes, out string runWorktree, out refused))
        {
            return refused!;
        }

        string? runTop = GitText(runWorktree, "rev-parse", "--show-toplevel");
        if (runTop is null)
        {
            return Refuse($"the run worktree '{runWorktree}' is not a git worktree.");
        }

        runWorktree = runTop;

        string? runCommon = CommonDirectoryOf(runWorktree);
        if (runCommon is null)
        {
            return Refuse($"the run worktree '{runWorktree}' is not a git worktree.");
        }

        if (!SamePath(taskCommon, runCommon))
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' and the run worktree '{runWorktree}' are in different repositories.");
        }

        string? runGitDirectory = GitText(runWorktree, "rev-parse", GitDirectoryOption);
        if (runGitDirectory is null || !_files.DirectoryExists(runGitDirectory))
        {
            return Refuse($"the run worktree '{runWorktree}' has no git directory to lock.");
        }

        if (_checkouts.CanonicalCheckoutOf(runGitDirectory) is not null)
        {
            return Refuse(
                $"the run worktree '{runWorktree}' is the repository's canonical checkout; pass the run's own worktree " +
                "with --run-worktree.");
        }

        if (SamePath(taskWorktree, runWorktree))
        {
            return _git.Run(runWorktree, ["-C", runWorktree, "rev-parse", "HEAD"]) is { Succeeded: true } head
                ? new RunIntegrateOutcome(RunIntegrateStatus.Integrated, head.StandardOutput.Trim(), Notes: notes)
                : Refuse($"the run worktree '{runWorktree}' has no commit to report.");
        }

        string? taskBranch = GitText(taskWorktree, "symbolic-ref", "--short", "HEAD");
        if (taskBranch is null)
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has no branch checked out; a task commit must be on a branch.");
        }

        string? runBranch = GitText(runWorktree, "symbolic-ref", "--short", "HEAD");
        if (runBranch is null)
        {
            return Refuse($"the run worktree '{runWorktree}' has no branch checked out.");
        }

        GitOutcome status = _git.Run(taskWorktree, ["-C", taskWorktree, "status", "--porcelain", "--untracked-files=no"]);
        if (!status.Succeeded)
        {
            return Failed($"git could not read the task worktree's status: {status.FirstErrorLine}");
        }

        string[] dirty = PathsOfStatus(status.StandardOutput);
        if (dirty.Length > 0)
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has uncommitted changes: {string.Join(", ", dirty)}. " +
                "Commit them before integrating.");
        }

        GitOutcome ahead = _git.Run(
            runWorktree, ["-C", runWorktree, "rev-list", "--count", runBranch + ".." + taskBranch]);
        if (!ahead.Succeeded || !int.TryParse(ahead.StandardOutput.Trim(), out int count))
        {
            return Failed($"git could not compare '{taskBranch}' with '{runBranch}': {ahead.FirstErrorLine}");
        }

        if (count == 0)
        {
            string? head = GitText(runWorktree, "rev-parse", "HEAD");
            return head is null
                ? Failed($"git could not read the head of '{runBranch}'.")
                : new RunIntegrateOutcome(RunIntegrateStatus.NothingToIntegrate, head, Notes: notes);
        }

        string lockPath = Path.Combine(runGitDirectory, LockFileName);
        try
        {
            using IntegrationLock held = TakeLock(lockPath, taskId, notes);
            return IntegrateUnderLock(taskWorktree, runWorktree, taskId, taskBranch, runBranch, runBranch, notes);
        }
        catch (TimeoutException timeout)
        {
            return Failed(timeout.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed($"the task could not be integrated: {exception.Message}");
        }
    }

    /// <summary>Moves the commits and cleans up while the run worktree's integration lock is held.</summary>
    /// <param name="taskWorktree">The task's worktree.</param>
    /// <param name="runWorktree">The run's worktree.</param>
    /// <param name="taskId">The task whose scratchpad is preserved.</param>
    /// <param name="taskBranch">The branch the task committed on.</param>
    /// <param name="runBranch">The run's branch.</param>
    /// <param name="rangeBase">The run branch whose commits are already on the run.</param>
    /// <param name="notes">Receives lines to print before the outcome.</param>
    private RunIntegrateOutcome IntegrateUnderLock(
        string taskWorktree,
        string runWorktree,
        string taskId,
        string taskBranch,
        string runBranch,
        string rangeBase,
        List<string> notes)
    {
        bool fastForward = _git.Run(
            runWorktree, ["-C", runWorktree, "merge-base", "--is-ancestor", runBranch, taskBranch]).Succeeded;

        GitOutcome applied = fastForward
            ? _git.Run(runWorktree, ["-C", runWorktree, "merge", "--ff-only", taskBranch])
            : _git.Run(runWorktree, ["-C", runWorktree, "cherry-pick", rangeBase + ".." + taskBranch]);

        if (!applied.Succeeded)
        {
            string[] conflicts = UnmergedPaths(runWorktree);
            if (conflicts.Length > 0)
            {
                _ = _git.Run(runWorktree, ["-C", runWorktree, "cherry-pick", "--abort"]);
                return new RunIntegrateOutcome(
                    RunIntegrateStatus.Conflict,
                    Message: $"applying '{taskBranch}' onto '{runBranch}' stopped at a conflict.",
                    Files: conflicts,
                    Notes: notes);
            }

            return Failed(
                $"git could not apply '{taskBranch}' onto '{runBranch}': {applied.FirstErrorLine}");
        }

        string? head = GitText(runWorktree, "rev-parse", "HEAD");
        if (head is null)
        {
            return Failed($"git applied '{taskBranch}' but could not read the new head of '{runBranch}'.");
        }

        CopyEvidence(taskWorktree, runWorktree, taskId);

        GitOutcome removed = _git.Run(
            runWorktree, ["-C", runWorktree, "worktree", "remove", "--force", taskWorktree]);
        if (!removed.Succeeded)
        {
            return Failed(
                $"the task is integrated at {head}, but removing the task worktree '{taskWorktree}' failed: " +
                removed.FirstErrorLine);
        }

        GitOutcome deleted = _git.Run(runWorktree, ["-C", runWorktree, "branch", "-D", taskBranch]);
        if (!deleted.Succeeded)
        {
            return Failed(
                $"the task is integrated at {head}, but deleting the task branch '{taskBranch}' failed: " +
                deleted.FirstErrorLine);
        }

        return new RunIntegrateOutcome(RunIntegrateStatus.Integrated, head, Notes: notes);
    }

    /// <summary>Checks the task id before it is used as a folder name.</summary>
    private static bool TryTaskId(string? value, out string taskId, out RunIntegrateOutcome? refused)
    {
        taskId = value?.Trim() ?? string.Empty;
        refused = null;
        if (taskId.Length == 0)
        {
            refused = Refuse("--task is required: it names the task whose .agent_scratchpad folder is kept.");
            return false;
        }

        if (taskId is "." or ".." || taskId.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            refused = Refuse($"--task '{taskId}' is not a task id: it must not contain a path separator.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the run worktree: the one the caller named, or the live run record <c>run start</c> wrote for this
    /// repository.
    /// </summary>
    private bool TryRunWorktree(
        RunIntegrateRequest request,
        string taskWorktree,
        string taskCommon,
        List<string> notes,
        out string runWorktree,
        out RunIntegrateOutcome? refused)
    {
        runWorktree = string.Empty;
        refused = null;
        if (!string.IsNullOrWhiteSpace(request.RunWorktree))
        {
            runWorktree = _paths.GetFullPath(request.RunWorktree, request.WorkingDirectory);
            if (_files.DirectoryExists(runWorktree))
            {
                return true;
            }

            refused = Refuse($"the run worktree '{runWorktree}' does not exist.");
            return false;
        }

        string? canonical = _checkouts.CanonicalCheckoutOf(taskCommon);
        if (canonical is null || string.IsNullOrWhiteSpace(request.HomeDirectory))
        {
            refused = Refuse(
                $"no live run record of this repository names a run worktree; pass the run's worktree with " +
                "--run-worktree.");
            return false;
        }

        IReadOnlyList<RunRecord>? runs = _checkouts.LiveSeparateRuns(request.HomeDirectory, canonical);
        if (runs is null)
        {
            refused = Refuse(
                "the live run records could not be read; pass the run's worktree with --run-worktree.");
            return false;
        }

        if (runs.Count == 0)
        {
            refused = Refuse(
                $"no live run of '{canonical}' works in a worktree of its own; pass the run's worktree with " +
                "--run-worktree.");
            return false;
        }

        RunRecord chosen = runs
            .OrderByDescending(run => run.StartedAt ?? DateTimeOffset.MinValue)
            .First();
        runWorktree = chosen.Worktree;
        if (runs.Count > 1)
        {
            notes.Add(
                $"several live runs work in their own worktrees; using the most recent, '{chosen.Branch}' at " +
                $"'{chosen.Worktree}'.");
        }

        return true;
    }

    /// <summary>Takes the lock, waiting for a parallel worker and taking over a stale file.</summary>
    private IntegrationLock TakeLock(string path, string taskId, List<string> notes)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + LockWait;
        string holder = $"{taskId}\n{_time.GetUtcNow():O}\n";
        bool takeoverLogged = false;
        while (true)
        {
            if (_exclusive.TryWriteNew(path, holder))
            {
                return new IntegrationLock(_files, path);
            }

            if (!_files.FileExists(path))
            {
                throw new IOException(
                    $"the integration lock '{path}' could not be created; check that the run worktree's git " +
                    "directory is writable.");
            }

            if (IsStale(path))
            {
                TryDelete(path);
                if (!takeoverLogged)
                {
                    notes.Add($"the integration lock '{path}' was stale; took it over.");
                    takeoverLogged = true;
                }

                if (_exclusive.TryWriteNew(path, holder))
                {
                    return new IntegrationLock(_files, path);
                }
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new TimeoutException(
                    $"the run worktree is locked by '{path}'; another task is still integrating after " +
                    $"{LockWait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds.");
            }

            Task.Delay(LockRetryInterval, _time).GetAwaiter().GetResult();
        }
    }

    private bool IsStale(string path)
    {
        DateTime lastWrite = _files.GetLastWriteTimeUtc(path);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        return now - lastWrite >= StaleLockAge;
    }

    private void TryDelete(string path)
    {
        try
        {
            _files.DeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lock that cannot be removed is retried on the next pass and reported as held at the deadline.
        }
    }

    /// <summary>Copies the task's scratchpad into the run worktree before the task worktree is removed.</summary>
    private void CopyEvidence(string taskWorktree, string runWorktree, string taskId)
    {
        string source = Path.Combine(taskWorktree, ScratchpadFolder, taskId);
        if (!_files.DirectoryExists(source))
        {
            return;
        }

        string destination = Path.Combine(runWorktree, ScratchpadFolder, taskId);
        _files.CreateDirectory(destination);
        foreach (string file in _files.EnumerateFiles(source, "*", recursive: true, followLinks: false))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                _files.CreateDirectory(parent);
            }

            _files.WriteAllBytes(target, _files.ReadAllBytes(file));
        }
    }

    private string[] UnmergedPaths(string worktree)
    {
        GitOutcome outcome = _git.Run(
            worktree, ["-C", worktree, "diff", "--name-only", UnmergedFiles]);
        return outcome.Succeeded ? PathsOfOutput(outcome.StandardOutput) : [];
    }

    private static string[] PathsOfStatus(string porcelain) =>
        porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private static string[] PathsOfOutput(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private string? CommonDirectoryOf(string worktree)
    {
        GitOutcome outcome = _git.Run(worktree, ["-C", worktree, "rev-parse", CommonDirectoryOption]);
        if (!outcome.Succeeded || outcome.StandardOutput.Trim().Length == 0)
        {
            return null;
        }

        return _paths.GetFullPath(outcome.StandardOutput.Trim(), worktree);
    }

    private string? GitText(string worktree, params string[] arguments)
    {
        List<string> argv = ["-C", worktree];
        argv.AddRange(arguments);
        GitOutcome outcome = _git.Run(worktree, argv);
        return outcome.Succeeded && outcome.StandardOutput.Trim().Length > 0
            ? outcome.StandardOutput.Trim()
            : null;
    }

    private bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            _environment.IsFileSystemCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static RunIntegrateOutcome Refuse(string message) =>
        new(RunIntegrateStatus.RefusedInput, Message: message);

    private static RunIntegrateOutcome Failed(string message) =>
        new(RunIntegrateStatus.Failed, Message: message);

    /// <summary>The held lock, removed on every path that leaves the integration.</summary>
    private sealed class IntegrationLock(IWritableFileSystem files, string path) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                files.DeleteFile(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A lock left behind is taken over as stale by the next integration after ten minutes.
            }
        }
    }
}
