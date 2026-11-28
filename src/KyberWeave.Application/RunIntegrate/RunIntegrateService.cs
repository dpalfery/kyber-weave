using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core;
using KyberWeave.Core.Processes;
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
    /// <summary>
    /// The lock file every integration of one run worktree shares, inside that worktree's git directory. It is
    /// opened exclusively for the whole integration and never deleted, because the operating system, not the file,
    /// is what holds the lock.
    /// </summary>
    public const string LockFileName = "kyber-weave-run-integrate.lock";

    private const string GitDirectoryOption = "--absolute-git-dir";
    private const string CommonDirectoryOption = "--git-common-dir";
    private const string UnmergedFiles = "--diff-filter=U";

    /// <summary>How long an integration waits for a parallel worker's lock before failing.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(120);

    /// <summary>How often a waiting integration retries the lock.</summary>
    public static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(500);

    private readonly IGitCommandRunner _git;
    private readonly IRunCheckout _checkouts;
    private readonly IWritableFileSystem _files;
    private readonly IProcessEnvironment _environment;
    private readonly IPathResolver _paths;
    private readonly TimeProvider _time;

    /// <summary>Creates the integration over the git, file-system and run-record seams.</summary>
    /// <param name="git">Runs every git child, bounded by its own timeout.</param>
    /// <param name="checkouts">Finds the live run record for the task worktree's repository.</param>
    /// <param name="files">Reads and writes the evidence folder, and opens the integration lock.</param>
    /// <param name="environment">The operator's home, the current directory and this process's id.</param>
    /// <param name="paths">Turns the paths the caller gave into absolute ones.</param>
    /// <param name="time">The clock the lock wait uses.</param>
    public RunIntegrateService(
        IGitCommandRunner git,
        IRunCheckout checkouts,
        IWritableFileSystem files,
        IProcessEnvironment environment,
        IPathResolver paths,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(checkouts);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(time);

        _git = git;
        _checkouts = checkouts;
        _files = files;
        _environment = environment;
        _paths = paths;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<RunIntegrateOutcome> IntegrateAsync(
        RunIntegrateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryTaskId(request.TaskId, out string taskId, out RunIntegrateOutcome? refused))
        {
            return refused;
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
        WorktreeFacts? taskFacts = await ReadFactsAsync(taskWorktree, cancellationToken).ConfigureAwait(false);
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

        string? taskTop = await GitTextAsync(
            taskWorktree, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (taskTop is null)
        {
            return Refuse($"the task worktree '{taskWorktree}' is not inside a git repository.");
        }

        taskWorktree = taskTop;
        string? taskBranch = await GitTextAsync(
            taskWorktree, cancellationToken, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
        if (taskBranch is null)
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has no branch checked out; a task commit must be on a branch.");
        }

        RunIntegrateOutcome? taskBranchFailed = await TryBranchNameAsync(
            taskWorktree, taskBranch, "task", cancellationToken).ConfigureAwait(false);
        if (taskBranchFailed is not null)
        {
            return taskBranchFailed;
        }

        RunWorktreeResolution runResolution = await TryRunWorktreeAsync(
            request, taskWorktree, taskBranch, taskFacts.CommonDirectory, cancellationToken).ConfigureAwait(false);
        if (runResolution.Outcome is { } resolutionFailure)
        {
            return resolutionFailure;
        }

        string runWorktree = runResolution.RunWorktree;
        WorktreeFacts? runFacts = await ReadFactsAsync(runWorktree, cancellationToken).ConfigureAwait(false);
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

        string? runTop = await GitTextAsync(
            runWorktree, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
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
            GitOutcome sameHead = await _git.RunAsync(
                runWorktree, ["-C", runWorktree, "rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            return sameHead is { Succeeded: true }
                ? new RunIntegrateOutcome(RunIntegrateStatus.Integrated, sameHead.StandardOutput.Trim(), Notes: notes)
                : Refuse($"the run worktree '{runWorktree}' has no commit to report.");
        }

        string? runBranch = await GitTextAsync(
            runWorktree, cancellationToken, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
        if (runBranch is null)
        {
            return Refuse($"the run worktree '{runWorktree}' has no branch checked out.");
        }

        RunIntegrateOutcome? runBranchFailed = await TryBranchNameAsync(
            runWorktree, runBranch, "run", cancellationToken).ConfigureAwait(false);
        if (runBranchFailed is not null)
        {
            return runBranchFailed;
        }

        if (string.Equals(taskBranch, runBranch, StringComparison.Ordinal))
        {
            return Refuse(
                $"the task worktree '{taskWorktree}' has the run branch '{runBranch}' checked out; a task commits on a " +
                "branch of its own.");
        }

        GitOutcome status = await _git.RunAsync(
            taskWorktree,
            ["-C", taskWorktree, "status", "--porcelain", "--untracked-files=no"],
            cancellationToken).ConfigureAwait(false);
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

        GitOutcome ahead = await _git.RunAsync(
            runWorktree,
            ["-C", runWorktree, "rev-list", "--count", "--end-of-options", runBranch + ".." + taskBranch],
            cancellationToken).ConfigureAwait(false);
        if (!ahead.Succeeded || !int.TryParse(ahead.StandardOutput.Trim(), out int count))
        {
            return Failed($"git could not compare '{taskBranch}' with '{runBranch}': {ahead.FirstErrorLine}");
        }

        string lockPath = Path.Combine(runGitDirectory, LockFileName);
        try
        {
            // The stream is the lock: the operating system releases it when this process exits or crashes, so there
            // is never a stale file to detect or take over, and the file itself is reused rather than deleted.
            using Stream held = await TakeLockAsync(lockPath, taskId, cancellationToken).ConfigureAwait(false);
            return await IntegrateUnderLockAsync(
                taskWorktree, runWorktree, taskId, taskBranch, runBranch, count, notes, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            return Failed(timeout.Message);
        }
        // This catch answers for the work before the commits land. Once IntegrateUnderLock has applied them, its
        // head reads and cleanup report the landed state there ("integrated at (sha), but …", or "the task commits
        // are on the run branch, but …" when the sha could not be read) and never reach this message.
        catch (Exception exception) when (IsGitProcessFailure(exception))
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
    /// <param name="cancellationToken">Cancels the git children the cleanup runs.</param>
    private async Task<RunIntegrateOutcome> IntegrateUnderLockAsync(
        string taskWorktree,
        string runWorktree,
        string taskId,
        string taskBranch,
        string runBranch,
        int commitsAhead,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        RunIntegrateStatus status = RunIntegrateStatus.NothingToIntegrate;
        string? head;
        if (commitsAhead == 0)
        {
            // A retry with nothing left to integrate still finishes the cleanup the first attempt began: the
            // scratchpad is copied and the task worktree goes, so a conductor's retry never leaves a stray worktree.
            LandedHead landed = await ReadLandedHeadAsync(runWorktree, cancellationToken).ConfigureAwait(false);
            head = landed.Head;
            string? retryHeadFailure = landed.Failure;
            if (head is null)
            {
                // The first attempt landed the commits, so a failed head read reports them as landed; a second git
                // call to recover the sha could fail the same way this one did.
                return Failed(retryHeadFailure is null
                    ? $"git could not read the head of '{runBranch}'."
                    : $"the task commits are on the run branch, but reading its head failed: {retryHeadFailure}");
            }
        }
        else
        {
            status = RunIntegrateStatus.Integrated;
            AppliedCommits applied = await ApplyAsync(
                runWorktree, taskBranch, runBranch, notes, cancellationToken).ConfigureAwait(false);
            if (applied.Stopped is { } stopped)
            {
                return stopped;
            }

            head = applied.Head;
        }

        string? evidenceFailure = CopyEvidence(taskWorktree, runWorktree, taskId);
        if (evidenceFailure is not null)
        {
            return new RunIntegrateOutcome(
                RunIntegrateStatus.IntegratedWithoutEvidence,
                head,
                Message: $"the task's commits are integrated at {head}, but copying '{ScratchLayout.Root}/{taskId}/' " +
                    $"into '{runWorktree}' failed: {evidenceFailure}. The task worktree '{taskWorktree}' is left " +
                    "in place; remove it once its scratchpad has been saved.",
                Notes: notes);
        }

        // The commits are on the run branch before cleanup starts, so a git child that fails to start, times out,
        // is cancelled, or exceeds its output limit is a cleanup failure: catching it here keeps it from reaching
        // IntegrateAsync's catch, which would report work that landed as "could not be integrated" or a bare timeout.
        GitOutcome removed;
        try
        {
            removed = await _git.RunAsync(
                runWorktree,
                ["-C", runWorktree, "worktree", "remove", "--force", taskWorktree],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsGitProcessFailure(exception))
        {
            return Failed(
                $"the task is integrated at {head}, but removing the task worktree '{taskWorktree}' failed: " +
                exception.Message);
        }

        if (!removed.Succeeded)
        {
            return Failed(
                $"the task is integrated at {head}, but removing the task worktree '{taskWorktree}' failed: " +
                removed.FirstErrorLine);
        }

        GitOutcome deleted;
        try
        {
            deleted = await _git.RunAsync(
                runWorktree,
                ["-C", runWorktree, "branch", "-D", "--end-of-options", taskBranch],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsGitProcessFailure(exception))
        {
            return Failed(
                $"the task is integrated at {head}, but deleting the task branch '{taskBranch}' failed: " +
                exception.Message);
        }

        if (!deleted.Succeeded)
        {
            return Failed(
                $"the task is integrated at {head}, but deleting the task branch '{taskBranch}' failed: " +
                deleted.FirstErrorLine);
        }

        return new RunIntegrateOutcome(status, head, Notes: notes);
    }

    /// <summary>
    /// Applies the task's commits to the run branch, refusing first only when an uncommitted run-worktree path is one
    /// those commits change.
    /// </summary>
    /// <param name="runWorktree">The run's worktree.</param>
    /// <param name="taskBranch">The branch the task committed on.</param>
    /// <param name="runBranch">The run's branch.</param>
    /// <param name="notes">Receives lines to print before the outcome.</param>
    /// <param name="cancellationToken">Cancels the git children the apply runs.</param>
    /// <returns>The run branch's head when the commits are applied, else the outcome to report.</returns>
    private async Task<AppliedCommits> ApplyAsync(
        string runWorktree,
        string taskBranch,
        string runBranch,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        // Only an uncommitted path the task's own commits change is dangerous: git would then refuse the apply
        // halfway, or fold the operator's work into the task's commit. A path the commits do not touch is left
        // alone by both a fast-forward and a cherry-pick, so it is reported and kept rather than blocking the task.
        GitOutcome status = await _git.RunAsync(
            runWorktree,
            ["-C", runWorktree, "status", "--porcelain", "--untracked-files=no"],
            cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded)
        {
            return new AppliedCommits(
                Failed($"git could not read the run worktree's status: {status.FirstErrorLine}"), null);
        }

        string[] dirty = PathsOfStatus(status.StandardOutput);

        GitOutcome changed = await _git.RunAsync(
            runWorktree,
            ["-C", runWorktree, "diff", "--name-only", runBranch + "..." + taskBranch],
            cancellationToken).ConfigureAwait(false);
        if (!changed.Succeeded)
        {
            return new AppliedCommits(
                Failed($"git could not list the files '{taskBranch}' changes: {changed.FirstErrorLine}"), null);
        }

        HashSet<string> taskPaths = new(PathsOfOutput(changed.StandardOutput), StringComparer.Ordinal);
        string[] clashing = [.. dirty.Where(path => Touches(path, taskPaths))];
        if (clashing.Length > 0)
        {
            return new AppliedCommits(
                Refuse(
                    $"the run worktree '{runWorktree}' has uncommitted changes to files this task changes: " +
                    $"{string.Join(", ", clashing)}. Commit or stash them before integrating."),
                null);
        }

        if (dirty.Length > 0)
        {
            notes.Add(
                "leaving these uncommitted changes in the run worktree in place: " + string.Join(", ", dirty));
        }

        Ancestry ancestry = await IsAncestorAsync(
            runWorktree, runBranch, taskBranch, cancellationToken).ConfigureAwait(false);
        bool? fastForward = ancestry.IsAncestor;
        if (fastForward is null)
        {
            return new AppliedCommits(
                Failed(
                    $"git could not tell whether '{runBranch}' is an ancestor of '{taskBranch}': " +
                    $"{ancestry.Outcome.FirstErrorLine}"),
                null);
        }

        GitOutcome applied;
        if (fastForward.Value)
        {
            applied = await _git.RunAsync(
                runWorktree,
                ["-C", runWorktree, "merge", "--ff-only", "--end-of-options", taskBranch],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            applied = await _git.RunAsync(
                runWorktree,
                ["-C", runWorktree, "cherry-pick", "--empty=drop", "--end-of-options", runBranch + ".." + taskBranch],
                cancellationToken).ConfigureAwait(false);
        }

        if (!applied.Succeeded)
        {
            string[] conflicts = await UnmergedPathsAsync(runWorktree, cancellationToken).ConfigureAwait(false);
            if (!fastForward.Value)
            {
                await AbandonCherryPickAsync(runWorktree, cancellationToken).ConfigureAwait(false);
            }

            if (conflicts.Length > 0)
            {
                return new AppliedCommits(
                    new RunIntegrateOutcome(
                        RunIntegrateStatus.Conflict,
                        Message: $"applying '{taskBranch}' onto '{runBranch}' stopped at a conflict.",
                        Files: conflicts,
                        Notes: notes),
                    null);
            }

            return new AppliedCommits(
                Failed($"git could not apply '{taskBranch}' onto '{runBranch}': {applied.FirstErrorLine}"), null);
        }

        LandedHead landed = await ReadLandedHeadAsync(runWorktree, cancellationToken).ConfigureAwait(false);
        if (landed.Head is not null)
        {
            return new AppliedCommits(null, landed.Head);
        }

        // The commits are on the run branch, so only the sha is missing; a second git call to recover it could
        // fail the same way this one did.
        return new AppliedCommits(
            landed.Failure is null
                ? Failed($"git applied '{taskBranch}' but could not read the new head of '{runBranch}'.")
                : Failed($"the task commits are on the run branch, but reading its head failed: {landed.Failure}"),
            null);
    }

    /// <summary>
    /// Ends a cherry-pick that did not complete. A failed pick leaves <c>CHERRY_PICK_HEAD</c> or a sequencer
    /// directory behind even when no file conflicted — an untracked file the commit would overwrite is enough —
    /// and the next integration is then refused as "already in progress". <c>--abort</c> restores the branch;
    /// <c>--quit</c> ends the sequence when the abort itself cannot.
    /// </summary>
    private async Task AbandonCherryPickAsync(string runWorktree, CancellationToken cancellationToken)
    {
        GitOutcome aborted = await _git.RunAsync(
            runWorktree,
            ["-C", runWorktree, "cherry-pick", "--abort"],
            cancellationToken).ConfigureAwait(false);
        if (!aborted.Succeeded)
        {
            _ = await _git.RunAsync(
                runWorktree,
                ["-C", runWorktree, "cherry-pick", "--quit"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks the task id before it is used as a folder name.</summary>
    private static bool TryTaskId(string? value, out string taskId, [NotNullWhen(false)] out RunIntegrateOutcome? refused)
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
    private async Task<RunWorktreeResolution> TryRunWorktreeAsync(
        RunIntegrateRequest request,
        string taskWorktree,
        string taskBranch,
        string taskCommon,
        CancellationToken cancellationToken)
    {
        string runWorktree;
        if (!string.IsNullOrWhiteSpace(request.RunWorktree))
        {
            runWorktree = _paths.GetFullPath(request.RunWorktree, request.WorkingDirectory);
            return _files.DirectoryExists(runWorktree)
                ? new RunWorktreeResolution(runWorktree, null)
                : new RunWorktreeResolution(string.Empty, Refuse($"the run worktree '{runWorktree}' does not exist."));
        }

        string? canonical = _checkouts.CanonicalCheckoutOf(taskCommon);
        if (canonical is null || string.IsNullOrWhiteSpace(request.HomeDirectory))
        {
            return new RunWorktreeResolution(
                string.Empty,
                Refuse(
                    $"no live run record of this repository names a run worktree; pass the run's worktree with " +
                    "--run-worktree."));
        }

        IReadOnlyList<RunRecord>? runs = _checkouts.LiveSeparateRuns(request.HomeDirectory, canonical);
        if (runs is null)
        {
            return new RunWorktreeResolution(
                string.Empty,
                Refuse("the live run records could not be read; pass the run's worktree with --run-worktree."));
        }

        if (runs.Count == 0)
        {
            return new RunWorktreeResolution(
                string.Empty,
                Refuse(
                    $"no live run of '{canonical}' works in a worktree of its own; pass the run's worktree with " +
                    "--run-worktree."));
        }

        if (runs.Count == 1)
        {
            return new RunWorktreeResolution(runs[0].Worktree, null);
        }

        // The branch a task was cut from is an ancestor of the branch the task committed on. That is the only
        // record a task can belong to, so several live runs are narrowed to it, never guessed by recency.
        List<RunRecord> candidates = [];
        foreach (RunRecord run in runs)
        {
            Ancestry ancestry = await IsAncestorAsync(
                taskWorktree, run.Branch, taskBranch, cancellationToken).ConfigureAwait(false);
            bool? ancestor = ancestry.IsAncestor;
            if (ancestor is null)
            {
                return new RunWorktreeResolution(
                    string.Empty,
                    Failed(
                        $"git could not tell whether '{run.Branch}' is an ancestor of '{taskBranch}': " +
                        ancestry.Outcome.FirstErrorLine));
            }

            if (ancestor.Value)
            {
                candidates.Add(run);
            }
        }

        if (candidates.Count == 1)
        {
            return new RunWorktreeResolution(candidates[0].Worktree, null);
        }

        string named = string.Join(", ", runs.Select(run => $"'{run.Branch}' at '{run.Worktree}'"));
        return new RunWorktreeResolution(
            string.Empty,
            Refuse(
                candidates.Count == 0
                    ? $"several live runs work in their own worktrees and none has a run branch the task branch " +
                      $"'{taskBranch}' was cut from: {named}; pass the run's worktree with --run-worktree."
                    : $"several live runs work in their own worktrees and more than one has a run branch the task branch " +
                      $"'{taskBranch}' was cut from: {named}; pass the run's worktree with --run-worktree."));
    }

    /// <summary>
    /// Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> in one repository.
    /// </summary>
    /// <returns>
    /// True or false from git's exit code — 0 means an ancestor, 1 means not — or null with the git outcome when git
    /// itself failed, so an error is never read as "not an ancestor".
    /// </returns>
    private async Task<Ancestry> IsAncestorAsync(
        string worktree,
        string ancestor,
        string descendant,
        CancellationToken cancellationToken)
    {
        GitOutcome outcome = await _git.RunAsync(
            worktree,
            ["-C", worktree, "merge-base", "--is-ancestor", "--end-of-options", ancestor, descendant],
            cancellationToken).ConfigureAwait(false);
        bool? isAncestor = outcome.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
        return new Ancestry(isAncestor, outcome);
    }

    /// <summary>
    /// Rejects a branch name git would parse as an option before it reaches any git command.
    /// </summary>
    /// <param name="worktree">The worktree git checks the name in.</param>
    /// <param name="branch">The branch name <c>symbolic-ref</c> reported.</param>
    /// <param name="role">Whether the branch belongs to the task or the run, for the message.</param>
    /// <param name="cancellationToken">Cancels the git child the check runs.</param>
    /// <returns>The failure to report when the name is not a branch name, or null when it is.</returns>
    private async Task<RunIntegrateOutcome?> TryBranchNameAsync(
        string worktree,
        string branch,
        string role,
        CancellationToken cancellationToken)
    {
        GitOutcome outcome = await _git.RunAsync(
            worktree,
            ["-C", worktree, "check-ref-format", "--branch", branch],
            cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded
            ? null
            : Failed($"the {role} branch '{branch}' is not a valid branch name.");
    }

    /// <summary>
    /// Opens the run worktree's lock for exclusive use, waiting for a parallel worker. The open handle is the lock:
    /// the operating system releases it when the process exits or crashes, so a waiter can never take over a lock
    /// another live worker still holds.
    /// </summary>
    /// <param name="path">The lock file, inside the run worktree's git directory.</param>
    /// <param name="taskId">The task waiting for the lock, written into the file for diagnostics only.</param>
    /// <param name="cancellationToken">Cancels the wait between retries.</param>
    /// <returns>The open stream, which the caller disposes to release the lock.</returns>
    /// <exception cref="TimeoutException">Another worker held the lock for the whole wait.</exception>
    private async Task<Stream> TakeLockAsync(string path, string taskId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + LockWait;
        while (true)
        {
            Stream? held;
            try
            {
                held = _files.OpenExclusive(path);
            }
            catch (IOException)
            {
                if (_time.GetUtcNow() >= deadline)
                {
                    throw new TimeoutException(
                        $"the run worktree is locked by '{path}'; another task is still integrating after " +
                        $"{LockWait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds.");
                }

                await Task.Delay(LockRetryInterval, _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                WriteHolder(held, taskId);
                return held;
            }
            catch
            {
                await held.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Writes who holds the lock into the open file, for a person diagnosing a stuck run. Nothing reads it.</summary>
    private void WriteHolder(Stream held, string taskId)
    {
        byte[] text = Encoding.UTF8.GetBytes(
            $"pid {_environment.ProcessId}\ntask {taskId}\nopened {_time.GetUtcNow():O}\n");
        held.SetLength(0);
        held.Position = 0;
        held.Write(text);
        held.Flush();
    }

    /// <summary>Copies the task's scratchpad into the run worktree before the task worktree is removed.</summary>
    /// <returns>Why the copy failed, or null when it succeeded or there was nothing to copy.</returns>
    private string? CopyEvidence(string taskWorktree, string runWorktree, string taskId)
    {
        try
        {
            string source = Path.Combine(taskWorktree, ScratchLayout.Root, taskId);
            if (!_files.DirectoryExists(source))
            {
                return null;
            }

            string destination = Path.Combine(runWorktree, ScratchLayout.Root, taskId);
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

    private async Task<string[]> UnmergedPathsAsync(string worktree, CancellationToken cancellationToken)
    {
        GitOutcome outcome = await _git.RunAsync(
            worktree,
            ["-C", worktree, "diff", "--name-only", UnmergedFiles],
            cancellationToken).ConfigureAwait(false);
        return outcome.Succeeded ? PathsOfOutput(outcome.StandardOutput) : [];
    }

    private static string[] PathsOfStatus(string porcelain) =>
        porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    /// <summary>
    /// Whether one dirty status entry is a path the task changes, reading a rename's two sides so either one counts.
    /// </summary>
    private static bool Touches(string dirtyEntry, HashSet<string> taskPaths)
    {
        if (taskPaths.Contains(dirtyEntry))
        {
            return true;
        }

        int arrow = dirtyEntry.IndexOf(" -> ", StringComparison.Ordinal);
        return arrow >= 0
            && (taskPaths.Contains(dirtyEntry[..arrow]) || taskPaths.Contains(dirtyEntry[(arrow + 4)..]));
    }

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
    /// <param name="worktree">The worktree whose repository is read.</param>
    /// <param name="cancellationToken">Cancels the git children the read runs.</param>
    /// <returns>The facts, or null when git cannot name the worktree's repository.</returns>
    private async Task<WorktreeFacts?> ReadFactsAsync(string worktree, CancellationToken cancellationToken)
    {
        string? gitDirectory = await GitTextAsync(
            worktree, cancellationToken, "rev-parse", GitDirectoryOption).ConfigureAwait(false);
        if (gitDirectory is null)
        {
            return null;
        }

        string? commonText = await GitTextAsync(
            worktree,
            cancellationToken,
            "rev-parse",
            "--path-format=absolute",
            CommonDirectoryOption).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the run branch's head once the task's commits are already on it — after the apply, or on a retry. A git
    /// child failure there is a failure of reading work that has landed, not of the landing itself,
    /// so it is caught here instead of escaping to IntegrateAsync's catch, which would report landed commits as
    /// "could not be integrated" or a bare timeout.
    /// </summary>
    /// <param name="runWorktree">The run's worktree.</param>
    /// <param name="cancellationToken">Cancels the git child the read runs.</param>
    /// <returns>The head sha, or a failure when the read failed.</returns>
    private async Task<LandedHead> ReadLandedHeadAsync(string runWorktree, CancellationToken cancellationToken)
    {
        try
        {
            return new LandedHead(
                await GitTextAsync(runWorktree, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false),
                null);
        }
        catch (Exception exception) when (IsGitProcessFailure(exception))
        {
            return new LandedHead(null, exception.Message);
        }
    }

    /// <summary>
    /// Whether a git child could not be launched at all, as opposed to running and failing afterwards: the executable
    /// is missing or not executable, its working directory is gone, or the operating system refused the launch.
    /// </summary>
    /// <param name="exception">The exception a git call threw.</param>
    internal static bool IsGitLaunchFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception
        || (exception is InvalidOperationException && exception is not OutputLimitExceededException);

    /// <summary>
    /// Whether a git call threw a process-runner failure — either a launch failure or a child that timed out, was
    /// cancelled, or exceeded its output budget. Every catch that reports what already landed shares this predicate.
    /// </summary>
    /// <param name="exception">The exception a git call threw.</param>
    internal static bool IsGitProcessFailure(Exception exception) =>
        IsGitLaunchFailure(exception)
        || exception is TimeoutException or OutputLimitExceededException or OperationCanceledException;

    private async Task<string?> GitTextAsync(
        string worktree,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        List<string> argv = ["-C", worktree];
        argv.AddRange(arguments);
        GitOutcome outcome = await _git.RunAsync(worktree, argv, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The run worktree an integration resolved, or the refusal that stopped it.</summary>
    /// <param name="RunWorktree">The run's worktree, empty when the resolution refused.</param>
    /// <param name="Outcome">The refusal, or null when a worktree was found.</param>
    private sealed record RunWorktreeResolution(string RunWorktree, RunIntegrateOutcome? Outcome);

    /// <summary>Git's answer to an ancestry question.</summary>
    /// <param name="IsAncestor">True or false from git's exit code, or null when git itself failed.</param>
    /// <param name="Outcome">The git outcome, whose error line describes a failure.</param>
    private sealed record Ancestry(bool? IsAncestor, GitOutcome Outcome);

    /// <summary>The run branch's head after the commits landed, or why reading it failed.</summary>
    /// <param name="Head">The head sha, or null when the read failed.</param>
    /// <param name="Failure">Why the read failed, or null when it ran and found a head.</param>
    private sealed record LandedHead(string? Head, string? Failure);

    /// <summary>The head of an apply, or the outcome that stopped it.</summary>
    /// <param name="Stopped">The outcome to report, or null when the commits were applied.</param>
    /// <param name="Head">The run branch's head once the commits were applied.</param>
    private sealed record AppliedCommits(RunIntegrateOutcome? Stopped, string? Head);
}
