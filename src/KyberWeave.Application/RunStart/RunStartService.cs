using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.RunStart;

/// <summary>
///     Starts a run: fetches, resolves the branch the work belongs on, and creates it from the right remote
///     base before anyone investigates or plans.
/// </summary>
/// <remarks>
///     The order is the point. A run that plans against stale history is the failure this step exists to
///     prevent, so the fetch comes first and a fetch failure stops the run unless the host turned
///     <c>stop-on-tool-failure</c> off. Nothing is written until the base, the branch name and the place the
///     branch goes are all decided. The service reports through <see cref="RunStartMessage"/> and
///     <see cref="RunStartFacts"/>; the command decides how they are printed.
/// </remarks>
public sealed class RunStartService(
    IRunStartRunnerFactory runners,
    IWorktreeIndexGuard worktreeIndex,
    IRunStateStore state,
    IWritableFileSystem files,
    IProcessEnvironment environment,
    IGitRepositoryFacts gitFacts,
    IBoundedProcessExecutor processes,
    TimeProvider clock) : IRunStartService
{
    /// <inheritdoc />
    public RunStartOutcome Start(RunStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<RunStartMessage> messages = [];
        string? autonomy = BriefAutonomy.Normalize(request.Autonomy);
        if (request.Autonomy is not null && autonomy is null)
        {
            return Refuse(
                messages,
                $"'{request.Autonomy}' is not an autonomy. Use one of: {string.Join(", ", BriefFacts.Vocabulary["autonomy"])}.");
        }

        string repositoryRoot = request.RepositoryRoot;
        string branch = request.Branch;
        KyberWeaveConfig config = request.Config;
        IRunStartRunner runner = runners.Open(repositoryRoot);

        RunStartOutcome? prerequisiteStopped = CheckPrerequisites(
            runner,
            config,
            request,
            repositoryRoot,
            messages);
        if (prerequisiteStopped is not null)
        {
            return prerequisiteStopped;
        }

        RunStartOutcome? fetchStopped = HandleFetch(runner.Fetch(), config, messages);
        if (fetchStopped is not null)
        {
            return fetchStopped;
        }

        string baseRef = RunStartPlanner.BaseRefFor(config.ConfigReg.RunBaseBranch);
        string? originHead = runner.OriginHeadSymbolicRef();
        string baseBranch = RunStartPlanner.BaseBranchName(baseRef, originHead);
        string? startRef = ResolveStartRef(runner, baseRef, originHead);
        if (startRef is null)
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Error,
                $"no ref for the base {baseRef} resolves, so there is nothing to create the branch from."));
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Remedy,
                "set 'run.base-branch' in .kyber-weave/kyber-weave.yml to the branch the run is " +
                "compared against, or re-run once the fetch can reach origin."));
            return Stopped(RunStartStatus.ToolFailure, messages);
        }

        string? resume = string.IsNullOrWhiteSpace(request.Resume) ? null : request.Resume.Trim();
        string resumeRef = string.Empty;
        string? resumeTarget = null;
        if (resume is not null)
        {
            (string resumeRefCandidate, string? resumeTargetCandidate, string? refusal) =
                ResolveResume(runner, resume);
            if (refusal is not null)
            {
                return Refuse(messages, refusal);
            }

            resumeRef = resumeRefCandidate;
            resumeTarget = resumeTargetCandidate;
        }

        // Checked after the fetch, so a branch pushed from elsewhere counts. Git's own error would not say that
        // resuming is the way to continue the branch.
        if (resume is null && (runner.HasLocalBranch(branch) || runner.HasRemoteBranch(branch)))
        {
            return Refuse(messages, BranchExists(branch));
        }

        // The finished tasks come from the resumed branch's own commits, read while it is still the branch
        // named rather than the one checked out. A branch carrying none is a normal beginning, not a
        // failure, and a log that cannot be read is reported without stopping a resume that can still
        // continue the work.
        IReadOnlyList<RunStartCompletedTask> completed = [];
        string? completedWarning = null;
        if (resumeRef.Length > 0)
        {
            TryReadResumeLog(
                () => runner.CommitMessages(startRef, resumeRef),
                resumeRef,
                out completed,
                out completedWarning);
        }

        // Read before anything is switched: the versions and the divergence describe the checkout as the
        // operator left it, which is the state a defect was observed in. A resumed branch is described by
        // its own tip, because that is what continuing on it means.
        string? headRevision = runner.ResolveRevision("HEAD");
        string? baseRevision = runner.ResolveRevision(startRef);
        string headVersion = runner.Describe("HEAD") ?? "unknown";
        string baseVersion = runner.Describe(startRef) ?? "unknown";
        string alignmentRef = resume is null ? "HEAD" : resumeRef;
        string? alignmentRevision = runner.ResolveRevision(alignmentRef);
        (int? ahead, int? behind) = runner.Divergence(alignmentRef, startRef);
        string? originUrl = runner.OriginAddress();

        BranchLocation branchLocation = DetermineLocation(runner, messages);
        if (branchLocation.Stopped is not null)
        {
            return branchLocation.Stopped;
        }

        RunStartLocation location = branchLocation.Location;
        GitOutcome worktrees = branchLocation.Worktrees;
        string target = resumeTarget ?? branch;
        BranchPlacement placement = PlaceBranch(
            runner,
            location,
            target,
            resumeTarget,
            startRef,
            resumeRef,
            worktrees.StandardOutput,
            messages);
        if (placement.Stopped is not null)
        {
            return placement.Stopped;
        }

        GitOutcome created = placement.Created;
        string? worktreePath = placement.WorktreePath;

        RunStartOutcome? placementFailure = CheckPlacement(
            created,
            resume,
            runner,
            branch,
            target,
            messages);
        if (placementFailure is not null)
        {
            return placementFailure;
        }

        // The branch is placed: record the base it was created from where a later dispatch
        // hook can read it. The record is auxiliary — the run's own report below still names
        // the base — so a missing home degrades to the hook's default base rather than
        // failing a run that is otherwise ready.
        // Where the run works is recorded with it, so the dispatch guard can tell a started run from none.
        string? repository = gitFacts.GetCommonDirectory(repositoryRoot);
        RecordBase(
            state,
            environment.HomeDirectory,
            target,
            startRef,
            repository is null ? null : new RunStartPlace(repository, worktreePath ?? repositoryRoot, clock.GetUtcNow(), autonomy));

        IndexPreparation indexPreparation = PrepareIndex(
            request.IndexExecutor,
            worktreePath ?? repositoryRoot,
            target,
            messages);
        if (indexPreparation.Stopped is not null)
        {
            return indexPreparation.Stopped;
        }

        string indexTree = indexPreparation.IndexTree;
        string indexLabel = indexPreparation.IndexLabel;

        RunStartFacts facts = new(
            baseBranch,
            startRef,
            target,
            worktreePath,
            location,
            baseRevision,
            resumeTarget,
            indexLabel,
            completed,
            completedWarning,
            headVersion,
            baseVersion,
            originUrl,
            alignmentRevision,
            headRevision,
            ahead,
            behind,
            indexTree);
        return new RunStartOutcome(RunStartStatus.Started, messages, facts);
    }

    /// <summary>
    /// Records the base <paramref name="branch" /> was created from where a later dispatch
    /// hook can read it. Best effort: false when there is nowhere to record (or the record
    /// cannot be written), in which case the hook falls back to its default base.
    /// </summary>
    internal static bool RecordBase(IRunStateStore state, string homeDirectory, string branch, string baseRef, RunStartPlace? place = null)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return false;
        }

        try
        {
            state.Write(homeDirectory, branch, baseRef, place);
            return true;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the resumed branch's finished tasks from its commit log. A log that cannot be read
    /// is a warning on a resume that can still continue the work; anything else is reported
    /// through <paramref name="completedWarning" /> with no finished tasks.
    /// </summary>
    /// <remarks>
    /// The catch stays narrow on purpose: a cancelled wait escapes, and an
    /// <c>ArgumentException</c> — the guards on <c>RunStartRunner.CommitMessages</c> throw it for
    /// an empty ref — is a programming error, not a log that cannot be read, so it escapes too
    /// instead of passing as a soft warning.
    /// </remarks>
    /// <param name="readLog">Reads the commit log between the base and the resumed ref.</param>
    /// <param name="resumeRef">The resumed ref, named in the warning when the log cannot be read.</param>
    /// <param name="completed">The finished tasks the log names, or empty when it cannot be read.</param>
    /// <param name="completedWarning">Null when the log read cleanly.</param>
    internal static void TryReadResumeLog(
        Func<GitOutcome> readLog,
        string resumeRef,
        out IReadOnlyList<RunStartCompletedTask> completed,
        out string? completedWarning)
    {
        ArgumentNullException.ThrowIfNull(readLog);

        GitOutcome log;
        try
        {
            log = readLog();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            completed = [];
            completedWarning =
                $"git log could not read the commits on '{resumeRef}': {ex.Message}";
            return;
        }

        if (log.Succeeded)
        {
            completed = RunStartResumeReader.Read(log.StandardOutput);
            completedWarning = null;
        }
        else
        {
            completed = [];
            completedWarning =
                $"git log could not read the commits on '{resumeRef}': {log.FirstErrorLine}";
        }
    }

    /// <summary>
    ///     Stops a run whose repository has not answered onboarding or whose toolchain is missing.
    /// </summary>
    /// <remarks>
    ///     Onboarding is checked before the tool checks, on purpose. A worker that cannot reach the
    ///     standards and gates this repository chose cannot use the tools anyway, and a tool failure
    ///     reported first would send the operator to repair a remote that was never the thing standing
    ///     in the way.
    /// </remarks>
    private static RunStartOutcome? CheckPrerequisites(
        IRunStartRunner runner,
        KyberWeaveConfig config,
        RunStartRequest request,
        string repositoryRoot,
        List<RunStartMessage> messages)
    {
        if (RunStartPlanner.NeedsOnboarding(config.ConfigReg, request.ConfigPath is not null))
        {
            return StopForOnboarding(config, request.ConfigPath, repositoryRoot);
        }

        if (runner.CodeGraphBinaryIsAvailable())
        {
            return null;
        }

        messages.Add(new RunStartMessage(
            RunStartMessageKind.Error,
            "'codegraph' is not on PATH, so the tools a run depends on cannot be indexed or tested."));
        messages.Add(new RunStartMessage(
            RunStartMessageKind.Remedy,
            "install the CodeGraph CLI so that 'codegraph' resolves on PATH, then start the run again."));
        return Stopped(RunStartStatus.CodeGraphMissing, messages);
    }

    /// <summary>
    ///     Reports a fetch failure, or stops the run when the host asked tool failures to stop it.
    /// </summary>
    private static RunStartOutcome? HandleFetch(
        GitOutcome fetch,
        KyberWeaveConfig config,
        List<RunStartMessage> messages)
    {
        if (fetch.Succeeded)
        {
            return null;
        }

        if (config.ConfigReg.StopsOnToolFailure)
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Error,
                $"'git fetch origin --prune' failed: {fetch.FirstErrorLine}"));
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Remedy,
                "a run that cannot refresh its base plans against stale history. Fix the remote, " +
                "or set 'stop-on-tool-failure: false' in .kyber-weave/kyber-weave.yml to continue from " +
                "the base this checkout last fetched."));
            return Stopped(RunStartStatus.ToolFailure, messages);
        }

        messages.Add(new RunStartMessage(
            RunStartMessageKind.Warning,
            $"'git fetch origin --prune' failed ({fetch.FirstErrorLine}); " +
            "continuing from the base this checkout last fetched."));
        return null;
    }

    /// <summary>Reads the checkout and worktree state and decides where the branch belongs.</summary>
    private static BranchLocation DetermineLocation(
        IRunStartRunner runner,
        List<RunStartMessage> messages)
    {
        GitOutcome status = runner.CheckoutStatus();
        if (!status.Succeeded)
        {
            return new BranchLocation(default, default, Fail(messages, $"git status failed: {status.FirstErrorLine}"));
        }

        GitOutcome worktrees = runner.WorktreeList();
        if (!worktrees.Succeeded)
        {
            return new BranchLocation(
                default,
                default,
                Fail(messages, $"git worktree list failed: {worktrees.FirstErrorLine}"));
        }

        bool anotherWorktreeIsAttached = GitWorktreePorcelain.HasMultiple(worktrees.StandardOutput);

        RunStartLocation location = RunStartPlanner.DecideLocation(
            status.StandardOutput.Trim().Length == 0,
            anotherWorktreeIsAttached);
        return new BranchLocation(location, worktrees, null);
    }

    /// <summary>
    ///     Ensures the CodeGraph index where the run works, reporting an unusable index or a warning.
    /// </summary>
    private IndexPreparation PrepareIndex(
        IProcessExecutor? requestedExecutor,
        string indexTree,
        string target,
        List<RunStartMessage> messages)
    {
        // A new worktree has no index of its own, so the index goes where the run works. A failure here does not
        // undo the branch: the operator repairs the index and the run continues in that tree.
        IProcessExecutor indexExecutor = requestedExecutor ?? new BoundedIndexExecutor(processes, DoctorOptions.Defaults.IndexTimeout);
        WorktreeIndexOutcome index = worktreeIndex.Ensure(
            indexTree,
            DoctorOptions.Defaults,
            indexExecutor,
            environment.TempDirectory);
        string indexLabel = IndexLabel(index);

        if (index.StopsRun)
        {
            string error =
                $"the CodeGraph index for '{indexTree}' is not usable ({IndexReason(index)}), so the run stops here. " +
                $"The branch '{target}' is in place.";
            string remedy =
                $"repair the index with: kyber-weave doctor --ensure {ShellQuote.Quote(indexTree)} (the branch needs no " +
                "second run start: once that command passes, the run continues in that tree).";
            messages.Add(new RunStartMessage(RunStartMessageKind.Error, error, StandardErrorWhenJson: true));
            messages.Add(new RunStartMessage(RunStartMessageKind.Remedy, remedy, StandardErrorWhenJson: true));
            return new IndexPreparation(indexTree, indexLabel, Stopped(RunStartStatus.ToolFailure, messages));
        }

        if (index.Status is WorktreeIndexStatus.Failed or WorktreeIndexStatus.BinaryMissing)
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Warning,
                $"the CodeGraph index for '{indexTree}' could not be prepared ({IndexReason(index)}), so the run continues " +
                $"without it. Repair it with: kyber-weave doctor --ensure {ShellQuote.Quote(indexTree)}",
                StandardErrorWhenJson: true));
        }

        return new IndexPreparation(indexTree, indexLabel, null);
    }

    private sealed record BranchLocation(
        RunStartLocation Location,
        GitOutcome Worktrees,
        RunStartOutcome? Stopped);

    private sealed record IndexPreparation(
        string IndexTree,
        string IndexLabel,
        RunStartOutcome? Stopped);

    /// <summary>
    ///     Resolves the ref and target a resume names, or the refusal to report when no branch matches.
    /// </summary>
    private static (string ResumeRef, string? ResumeTarget, string? Refusal) ResolveResume(
        IRunStartRunner runner,
        string resume)
    {
        string remotePrefix = RunStartPlanner.Remote + "/";
        bool isExplicitRemote = resume.StartsWith(remotePrefix, StringComparison.Ordinal);
        string stripped = isExplicitRemote
            ? resume[remotePrefix.Length..]
            : resume;

        if (string.Equals(stripped, "HEAD", StringComparison.Ordinal))
        {
            return (
                string.Empty,
                null,
                $"'{resume}' is a symbolic reference, not a branch name. Pass the " +
                "branch a plan or pull request already owns.");
        }

        if (isExplicitRemote)
        {
            return runner.HasRemoteBranch(stripped)
                ? (remotePrefix + stripped, stripped, null)
                : (string.Empty, null, MissingResumeBranch(resume));
        }

        if (runner.HasLocalBranch(resume))
        {
            return (resume, resume, null);
        }

        if (runner.HasRemoteBranch(resume))
        {
            return (remotePrefix + resume, resume, null);
        }

        return (string.Empty, null, MissingResumeBranch(resume));
    }

    private static string MissingResumeBranch(string resume) =>
        $"No local or origin branch is named '{resume}', so there is nothing to resume. Pass the " +
        "branch a plan or pull request already owns.";

    /// <summary>
    ///     Places the target branch in place or in a worktree, returning either the git outcome and
    ///     worktree path, or the outcome that stops the run.
    /// </summary>
    private BranchPlacement PlaceBranch(
        IRunStartRunner runner,
        RunStartLocation location,
        string target,
        string? resumeTarget,
        string startRef,
        string? resumeRef,
        string worktreeOutput,
        List<RunStartMessage> messages)
    {
        if (location == RunStartLocation.InPlace)
        {
            GitOutcome created = resumeTarget is null
                ? runner.CreateBranchInPlace(target, startRef)
                : runner.SwitchToBranch(target);
            return new BranchPlacement(created, null, null);
        }

        string? held = resumeTarget is null
            ? null
            : GitWorktreePorcelain.Holding(worktreeOutput, resumeTarget);
        if (held is not null && files.DirectoryExists(held))
        {
            // Git allows a branch in one worktree only, so a resume continues in the one that holds it.
            return new BranchPlacement(new GitOutcome(0, string.Empty, string.Empty), held, null);
        }

        // A folder deleted by hand stays registered and would still claim the branch. Pruning removes
        // only entries whose folder is gone.
        if (held is not null)
        {
            GitOutcome pruned = runner.Git("worktree", "prune");
            if (!pruned.Succeeded)
            {
                return new BranchPlacement(
                    default,
                    null,
                    Fail(
                        messages,
                        $"git worktree prune failed, so the deleted folder '{held}' still claims " +
                        $"'{target}': {pruned.FirstErrorLine}"));
            }
        }

        // Before the folder exists: once git makes the worktree its untracked folder would show in the checkout.
        // A failure stops the run here, so no folder or claim is left behind.
        GitOutcome excluded = runner.EnsureWorktreeFolderExcluded();
        if (!excluded.Succeeded)
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Error,
                $"could not exclude '{AgentWorktreeFolder.ExcludeLine}' from git's local exclude file: {excluded.FirstErrorLine}"));
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Remedy,
                $"add '{AgentWorktreeFolder.ExcludeLine}' to .git/info/exclude and start again."));
            return new BranchPlacement(default, null, Stopped(RunStartStatus.ToolFailure, messages));
        }

        string worktreePath = runner.WorktreePathFor(target, clock.GetUtcNow());
        GitOutcome result;
        try
        {
            result = resumeTarget is null
                ? runner.CreateBranchInWorktree(target, startRef, worktreePath)
                : runner.AddWorktreeForBranch(target, worktreePath, resumeRef);
        }
        finally
        {
            // Released whether git succeeded, failed, or threw, so no claim outlives its run.
            runner.ReleaseWorktreeClaim(worktreePath);
        }

        return new BranchPlacement(result, worktreePath, null);
    }

    private sealed record BranchPlacement(
        GitOutcome Created,
        string? WorktreePath,
        RunStartOutcome? Stopped);

    /// <summary>
    ///     Maps a failed branch creation to the refusal or tool failure the run reports.
    /// </summary>
    /// <remarks>
    ///     The check before creation can lose to another run of the same slug. That loser gets the same
    ///     refusal and exit code as a branch that existed from the start, not a generic git failure.
    /// </remarks>
    private static RunStartOutcome? CheckPlacement(
        GitOutcome created,
        string? resume,
        IRunStartRunner runner,
        string branch,
        string target,
        List<RunStartMessage> messages)
    {
        if (!created.Succeeded && resume is null && runner.HasLocalBranch(branch))
        {
            return Refuse(messages, BranchExists(branch));
        }

        if (!created.Succeeded)
        {
            return Fail(messages, $"git could not put '{target}' in place: {created.FirstErrorLine}");
        }

        return null;
    }

    /// <summary>
    ///     The first ref that resolves for the base: the configured branch, then the default branch, and the
    ///     local branch of that name as a last resort so a repository without remote-tracking refs still has
    ///     somewhere to start.
    /// </summary>
    private static string? ResolveStartRef(IRunStartRunner runner, string baseRef, string? originHead)
    {
        foreach (string candidate in StartRefCandidates(baseRef, originHead))
        {
            if (runner.ResolveRevision(candidate) is not null)
                return candidate;
        }

        return null;
    }

    private static List<string> StartRefCandidates(string baseRef, string? originHead)
    {
        List<string> candidates = [];

        if (!string.Equals(baseRef, RunStartPlanner.RemoteHead, StringComparison.Ordinal))
        {
            candidates.Add(baseRef);
            candidates.Add(RunStartPlanner.BaseBranchName(baseRef, originHead));
            return candidates;
        }

        if (!string.IsNullOrWhiteSpace(originHead))
        {
            candidates.Add(originHead);
            candidates.Add(RunStartPlanner.BaseBranchName(originHead, null));
        }

        candidates.Add(RunStartPlanner.RemoteHead);
        return candidates;
    }

    /// <summary>The index state as the <c>index</c> JSON field and the report line name it.</summary>
    private static string IndexLabel(WorktreeIndexOutcome index) => index.Status switch
    {
        WorktreeIndexStatus.Ready => index.Created ? "created" : "ready",
        WorktreeIndexStatus.NotGoverned => "not-governed",
        _ => "warning",
    };

    /// <summary>The first failed check's reason, which says what stopped the index.</summary>
    private static string IndexReason(WorktreeIndexOutcome index) =>
        index.Checks.FirstOrDefault(check => !check.Passed)?.Reason ?? "no reason was reported";

    private static string BranchExists(string branch) =>
        $"A branch named '{branch}' already exists. To continue it, re-run with --resume {branch}. " +
        "To start separate work, choose a different --slug.";

    private static RunStartOutcome Refuse(List<RunStartMessage> messages, string message)
    {
        messages.Add(new RunStartMessage(RunStartMessageKind.Error, message));
        return Stopped(RunStartStatus.RefusedInput, messages);
    }

    private static RunStartOutcome Fail(List<RunStartMessage> messages, string message)
    {
        messages.Add(new RunStartMessage(RunStartMessageKind.Error, message));
        return Stopped(RunStartStatus.ToolFailure, messages);
    }

    private static RunStartOutcome Stopped(RunStartStatus status, List<RunStartMessage> messages) =>
        new(status, messages, null);

    /// <summary>
    /// Reports that the repository never answered the onboarding question, and what answers it.
    /// </summary>
    /// <remarks>
    /// The message names the skill and the verb rather than only the missing key: a stop that says
    /// "onboarding is unset" leaves the operator to find both. In autonomous mode it also says that this run
    /// will neither ask nor record an answer, because the setting stays exactly as it was found.
    /// </remarks>
    private static RunStartOutcome StopForOnboarding(KyberWeaveConfig config, string? configPath, string repoRoot)
    {
        string location = configPath is not null
            ? Path.GetRelativePath(repoRoot, configPath).Replace('\\', '/')
            : UserStatePaths.RepositoryConfigRelativePath;

        List<RunStartMessage> messages =
        [
            new(
                RunStartMessageKind.Error,
                $"this repository has not completed onboarding: {location} sets no 'onboarding:' value, so a run " +
                "would plan against gates and standards that nobody chose for it."),
            new(
                RunStartMessageKind.Remedy,
                $"run the {RunStartPlanner.OnboardingSkill} skill to choose this " +
                "repository's technologies, gates and standards, then record the answer with " +
                "'kyber-weave docs onboarding done' — or 'kyber-weave docs onboarding declined' to record " +
                "that you chose not to.")
        ];

        if (string.Equals(config.ConfigReg.Autonomy, ConfigRegConfig.AutonomousAutonomy, StringComparison.Ordinal))
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Note,
                "This run is autonomous, so it has asked nothing and written nothing: record the " +
                "answer once, and every later run reads it."));
        }

        return Stopped(RunStartStatus.ToolFailure, messages);
    }

    /// <summary>Runs the CodeGraph child under the bound the index run is held to.</summary>
    private sealed class BoundedIndexExecutor(IBoundedProcessExecutor inner, TimeSpan timeout) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
            inner.Run(startInfo, standardInput, timeout);
    }
}
