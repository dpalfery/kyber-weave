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

    /// <summary>
    /// How many waits a path that cannot be created may last before it is reported as unwritable rather than as
    /// another worker's lock being taken over at that moment.
    /// </summary>
    private const int UncreatableLockAttempts = 3;

    private readonly IGitCommandRunner _git;
    private readonly IRunCheckout _checkouts;
    private readonly IWritableFileSystem _files;
    private readonly IExclusiveFileWriter _exclusive;
    private readonly IProcessEnvironment _environment;
    private readonly IPathResolver _paths;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;

    /// <summary>Creates the integration over the git, file-system and run-record seams.</summary>
    /// <param name="git">Runs every git child, bounded by its own timeout.</param>
    /// <param name="checkouts">Finds the live run record for the task worktree's repository.</param>
    /// <param name="files">Reads and writes the evidence folder and the lock file.</param>
    /// <param name="exclusive">Takes the lock with an atomic create, so two workers cannot both win it.</param>
    /// <param name="environment">The operator's home and the current directory.</param>
    /// <param name="paths">Turns the paths the caller gave into absolute ones.</param>
    /// <param name="ids">Names the file a stale lock is moved to when this process takes it over.</param>
    /// <param name="time">The clock the stale-lock age and the wait use.</param>
    public RunIntegrateService(
        IGitCommandRunner git,
        IRunCheckout checkouts,
        IWritableFileSystem files,
        IExclusiveFileWriter exclusive,
        IProcessEnvironment environment,
        IPathResolver paths,
        IIdGenerator ids,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(checkouts);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(exclusive);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(time);

        _git = git;
        _checkouts = checkouts;
        _files = files;
        _exclusive = exclusive;
        _environment = environment;
        _paths = paths;
        _ids = ids;
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

        // The canonical checks come first, before any other git call in either worktree: the answer decides whether
        // this worktree may be touched at all, and a repository's own checkout is where a run never works.
        WorktreeFacts? taskFacts = ReadFacts(taskWorktree);
        if (taskFacts is null)
        {
            return Refuse($"the task worktree '{taskWorktree}' is not inside a git repository.");
        }

        if (taskFacts.IsCanonicalCheckout)
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' is the repository's canonical checkout; a task commits in a " +
                "worktree of its own, never in the checkout the operator works in.");
        }

        string? taskTop = GitText(taskWorktree, "rev-parse", "--show-toplevel");
        if (taskTop is null)
        {
            return Refuse($"the task worktree '{taskWorktree}' is not inside a git repository.");
        }

        taskWorktree = taskTop;
        string? taskBranch = GitText(taskWorktree, "symbolic-ref", "--short", "HEAD");
        if (taskBranch is null)
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has no branch checked out; a task commit must be on a branch.");
        }

        if (!TryRunWorktree(request, taskWorktree, taskBranch, taskFacts.CommonDirectory, out string runWorktree, out refused))
        {
            return refused!;
        }

        WorktreeFacts? runFacts = ReadFacts(runWorktree);
        if (runFacts is null)
        {
            return Refuse($"the run worktree '{runWorktree}' is not a git worktree.");
        }

        if (runFacts.IsCanonicalCheckout)
        {
            return Refuse(
                $"the run worktree '{runWorktree}' is the repository's canonical checkout; pass the run's own worktree " +
                "with --run-worktree.");
        }

        string? runTop = GitText(runWorktree, "rev-parse", "--show-toplevel");
        if (runTop is null)
        {
            return Refuse($"the run worktree '{runWorktree}' is not a git worktree.");
        }

        runWorktree = runTop;

        if (!SamePath(taskFacts.CommonDirectory, runFacts.CommonDirectory))
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' and the run worktree '{runWorktree}' are in different repositories.");
        }

        string runGitDirectory = runFacts.GitDirectory;
        if (!_files.DirectoryExists(runGitDirectory))
        {
            return Refuse($"the run worktree '{runWorktree}' has no git directory to lock.");
        }

        if (SamePath(taskWorktree, runWorktree))
        {
            return _git.Run(runWorktree, ["-C", runWorktree, "rev-parse", "HEAD"]) is { Succeeded: true } head
                ? new RunIntegrateOutcome(RunIntegrateStatus.Integrated, head.StandardOutput.Trim(), Notes: notes)
                : Refuse($"the run worktree '{runWorktree}' has no commit to report.");
        }

        string? runBranch = GitText(runWorktree, "symbolic-ref", "--short", "HEAD");
        if (runBranch is null)
        {
            return Refuse($"the run worktree '{runWorktree}' has no branch checked out.");
        }

        if (string.Equals(taskBranch, runBranch, StringComparison.Ordinal))
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has the run branch '{runBranch}' checked out; a task commits on a " +
                "branch of its own.");
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

        string lockPath = Path.Combine(runGitDirectory, LockFileName);
        try
        {
            using IntegrationLock held = TakeLock(lockPath, taskId, notes);
            return IntegrateUnderLock(taskWorktree, runWorktree, taskId, taskBranch, runBranch, count, notes);
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
    /// <param name="commitsAhead">How many commits the task branch has that the run branch does not.</param>
    /// <param name="notes">Receives lines to print before the outcome.</param>
    private RunIntegrateOutcome IntegrateUnderLock(
        string taskWorktree,
        string runWorktree,
        string taskId,
        string taskBranch,
        string runBranch,
        int commitsAhead,
        List<string> notes)
    {
        RunIntegrateStatus status = RunIntegrateStatus.NothingToIntegrate;
        string? head;
        if (commitsAhead == 0)
        {
            // A retry with nothing left to integrate still finishes the cleanup the first attempt began: the
            // scratchpad is copied and the task worktree goes, so a conductor's retry never leaves a stray worktree.
            head = GitText(runWorktree, "rev-parse", "HEAD");
            if (head is null)
            {
                return Failed($"git could not read the head of '{runBranch}'.");
            }
        }
        else
        {
            status = RunIntegrateStatus.Integrated;
            RunIntegrateOutcome? stopped = Apply(runWorktree, taskBranch, runBranch, notes, out head);
            if (stopped is not null)
            {
                return stopped;
            }
        }

        string? evidenceFailure = CopyEvidence(taskWorktree, runWorktree, taskId);
        if (evidenceFailure is not null)
        {
            return new RunIntegrateOutcome(
                RunIntegrateStatus.IntegratedWithoutEvidence,
                head,
                Message: $"the task's commits are integrated at {head}, but copying '{ScratchpadFolder}/{taskId}/' " +
                    $"into '{runWorktree}' failed: {evidenceFailure}. The task worktree '{taskWorktree}' is left " +
                    "in place; remove it once its scratchpad has been saved.",
                Notes: notes);
        }

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

        return new RunIntegrateOutcome(status, head, Notes: notes);
    }

    /// <summary>Applies the task's commits to the run branch, refusing a dirty run worktree first.</summary>
    /// <param name="runWorktree">The run's worktree.</param>
    /// <param name="taskBranch">The branch the task committed on.</param>
    /// <param name="runBranch">The run's branch.</param>
    /// <param name="notes">Receives lines to print before the outcome.</param>
    /// <param name="head">Receives the run branch's head once the commits are applied.</param>
    /// <returns>Null when the commits are applied, else the outcome to report.</returns>
    private RunIntegrateOutcome? Apply(
        string runWorktree,
        string taskBranch,
        string runBranch,
        List<string> notes,
        out string? head)
    {
        head = null;

        // An uncommitted tracked change would make git either refuse the cherry-pick halfway or fold the operator's
        // work into the task's commit, so the run worktree must be clean before anything is applied.
        GitOutcome status = _git.Run(runWorktree, ["-C", runWorktree, "status", "--porcelain", "--untracked-files=no"]);
        if (!status.Succeeded)
        {
            return Failed($"git could not read the run worktree's status: {status.FirstErrorLine}");
        }

        string[] dirty = PathsOfStatus(status.StandardOutput);
        if (dirty.Length > 0)
        {
            return Refuse(
                $"the run worktree '{runWorktree}' has uncommitted changes: {string.Join(", ", dirty)}. " +
                "Commit or stash them before integrating.");
        }

        bool fastForward = _git.Run(
            runWorktree, ["-C", runWorktree, "merge-base", "--is-ancestor", runBranch, taskBranch]).Succeeded;

        GitOutcome applied = fastForward
            ? _git.Run(runWorktree, ["-C", runWorktree, "merge", "--ff-only", taskBranch])
            : _git.Run(
                runWorktree,
                ["-C", runWorktree, "cherry-pick", "--empty=drop", runBranch + ".." + taskBranch]);

        if (!applied.Succeeded)
        {
            string[] conflicts = UnmergedPaths(runWorktree);
            if (!fastForward)
            {
                AbandonCherryPick(runWorktree);
            }

            if (conflicts.Length > 0)
            {
                return new RunIntegrateOutcome(
                    RunIntegrateStatus.Conflict,
                    Message: $"applying '{taskBranch}' onto '{runBranch}' stopped at a conflict.",
                    Files: conflicts,
                    Notes: notes);
            }

            return Failed(
                $"git could not apply '{taskBranch}' onto '{runBranch}': {applied.FirstErrorLine}");
        }

        head = GitText(runWorktree, "rev-parse", "HEAD");
        return head is null
            ? Failed($"git applied '{taskBranch}' but could not read the new head of '{runBranch}'.")
            : null;
    }

    /// <summary>
    /// Ends a cherry-pick that did not complete. A failed pick leaves <c>CHERRY_PICK_HEAD</c> or a sequencer
    /// directory behind even when no file conflicted — an untracked file the commit would overwrite is enough —
    /// and the next integration is then refused as "already in progress". <c>--abort</c> restores the branch;
    /// <c>--quit</c> ends the sequence when the abort itself cannot.
    /// </summary>
    private void AbandonCherryPick(string runWorktree)
    {
        GitOutcome aborted = _git.Run(runWorktree, ["-C", runWorktree, "cherry-pick", "--abort"]);
        if (!aborted.Succeeded)
        {
            _ = _git.Run(runWorktree, ["-C", runWorktree, "cherry-pick", "--quit"]);
        }
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
    /// repository whose run branch the task branch was cut from.
    /// </summary>
    private bool TryRunWorktree(
        RunIntegrateRequest request,
        string taskWorktree,
        string taskBranch,
        string taskCommon,
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

        if (runs.Count == 1)
        {
            runWorktree = runs[0].Worktree;
            return true;
        }

        // The branch a task was cut from is an ancestor of the branch the task committed on. That is the only
        // record a task can belong to, so several live runs are narrowed to it, never guessed by recency.
        List<RunRecord> candidates = [.. runs.Where(run => IsAncestor(taskWorktree, run.Branch, taskBranch))];
        if (candidates.Count == 1)
        {
            runWorktree = candidates[0].Worktree;
            return true;
        }

        string named = string.Join(", ", runs.Select(run => $"'{run.Branch}' at '{run.Worktree}'"));
        refused = Refuse(
            candidates.Count == 0
                ? $"several live runs work in their own worktrees and none has a run branch the task branch " +
                  $"'{taskBranch}' was cut from: {named}; pass the run's worktree with --run-worktree."
                : $"several live runs work in their own worktrees and more than one has a run branch the task branch " +
                  $"'{taskBranch}' was cut from: {named}; pass the run's worktree with --run-worktree.");
        return false;
    }

    /// <summary>Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> in one repository.</summary>
    private bool IsAncestor(string worktree, string ancestor, string descendant) =>
        _git.Run(worktree, ["-C", worktree, "merge-base", "--is-ancestor", ancestor, descendant]).Succeeded;

    /// <summary>Takes the lock, waiting for a parallel worker and taking over a stale file.</summary>
    private IntegrationLock TakeLock(string path, string taskId, List<string> notes)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + LockWait;
        string holder = $"{taskId}\n{_time.GetUtcNow():O}\n";
        bool staleLogged = false;
        bool yieldTakeover = false;
        int uncreatable = 0;
        while (true)
        {
            if (!yieldTakeover && _exclusive.TryWriteNew(path, holder))
            {
                return new IntegrationLock(_files, path, holder);
            }

            bool yielded = yieldTakeover;
            yieldTakeover = false;

            if (_files.FileExists(path))
            {
                uncreatable = 0;
                if (IsStale(path))
                {
                    bool moved = TryMoveStaleAside(path, out string? aside);
                    TryDelete(aside);
                    if (moved)
                    {
                        if (!staleLogged)
                        {
                            notes.Add($"the integration lock '{path}' was stale; took it over.");
                            staleLogged = true;
                        }

                        // Only the process whose rename won may retry the create at once; it is the one that cleared
                        // the stale file. Losing the create means another waiter was already there, so this one waits.
                        if (_exclusive.TryWriteNew(path, holder))
                        {
                            return new IntegrationLock(_files, path, holder);
                        }
                    }

                    // The stale file is briefly gone while its taker-over creates the lock. A failed rename means
                    // another process won the takeover, so this one keeps waiting rather than racing for the file.
                    yielded = true;
                }
            }
            else if (!yielded && ++uncreatable >= UncreatableLockAttempts)
            {
                throw new IOException(
                    $"the integration lock '{path}' could not be created; check that the run worktree's git " +
                    "directory is writable.");
            }
            else if (yielded)
            {
                uncreatable = 0;
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

    /// <summary>
    /// Moves a stale lock to a name of its own, as one rename. Two waiters that both saw the same stale file race
    /// here, and the file system lets exactly one of them win: the other finds nothing left to move.
    /// </summary>
    private bool TryMoveStaleAside(string path, out string? moved)
    {
        moved = path + "." + _ids.NewRandomToken() + ".stale";
        try
        {
            _files.MoveFile(path, moved);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            moved = null;
            return false;
        }
    }

    private bool IsStale(string path)
    {
        DateTime lastWrite = _files.GetLastWriteTimeUtc(path);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        return now - lastWrite >= StaleLockAge;
    }

    private void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            _files.DeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lock file that cannot be removed is retried on the next pass and reported as held at the deadline.
        }
    }

    /// <summary>Copies the task's scratchpad into the run worktree before the task worktree is removed.</summary>
    /// <returns>Why the copy failed, or null when it succeeded or there was nothing to copy.</returns>
    private string? CopyEvidence(string taskWorktree, string runWorktree, string taskId)
    {
        try
        {
            string source = Path.Combine(taskWorktree, ScratchpadFolder, taskId);
            if (!_files.DirectoryExists(source))
            {
                return null;
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

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
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

    /// <summary>
    /// Reads a worktree's git directory and common git directory, and decides whether the worktree is the
    /// repository's canonical checkout: the one place where the two are the same directory. A linked worktree's
    /// git directory is a private folder under the common one, so the two differ there, whatever the folders are
    /// named.
    /// </summary>
    /// <returns>The facts, or null when git cannot name the worktree's repository.</returns>
    private WorktreeFacts? ReadFacts(string worktree)
    {
        string? gitDirectory = GitText(worktree, "rev-parse", GitDirectoryOption);
        if (gitDirectory is null)
        {
            return null;
        }

        string? commonText = GitText(worktree, "rev-parse", "--path-format=absolute", CommonDirectoryOption);
        if (commonText is null)
        {
            return null;
        }

        string commonDirectory = _paths.GetFullPath(commonText, worktree);
        return new WorktreeFacts(
            Path.TrimEndingDirectorySeparator(gitDirectory),
            commonDirectory,
            SamePath(gitDirectory, commonDirectory));
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

    /// <summary>One worktree's git facts.</summary>
    /// <param name="GitDirectory">The worktree's own git directory, absolute.</param>
    /// <param name="CommonDirectory">The repository's common git directory, absolute.</param>
    /// <param name="IsCanonicalCheckout">True when the two are the same directory, which only the canonical checkout is.</param>
    private sealed record WorktreeFacts(string GitDirectory, string CommonDirectory, bool IsCanonicalCheckout);

    /// <summary>The held lock, removed on every path that leaves the integration.</summary>
    /// <param name="files">The file system holding the lock.</param>
    /// <param name="path">The lock file.</param>
    /// <param name="holder">This process's own holder text, which the file must still hold for the removal to happen.</param>
    internal sealed class IntegrationLock(IWritableFileSystem files, string path, string holder) : IDisposable
    {
        /// <inheritdoc />
        /// <remarks>
        /// A lock taken over as stale belongs to another process now, and deleting it would let two integrations run
        /// together. Only a file that still holds this process's own text is this process's to remove.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                if (files.FileExists(path) && string.Equals(files.ReadAllText(path), holder, StringComparison.Ordinal))
                {
                    files.DeleteFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A lock left behind is taken over as stale by the next integration after ten minutes.
            }
        }
    }
}
