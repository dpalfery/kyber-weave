using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;
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
        string repositoryRoot = request.RepositoryRoot;
        string branch = request.Branch;
        KyberWeaveConfig config = request.Config;
        IRunStartRunner runner = runners.Open(repositoryRoot);

        // Before the tool checks, on purpose. A worker that cannot reach the standards and gates this
        // repository chose cannot use the tools anyway, and a tool failure reported first would send the
        // operator to repair a remote that was never the thing standing in the way.
        if (RunStartPlanner.NeedsOnboarding(config.ConfigReg, request.ConfigPath is not null))
        {
            return StopForOnboarding(config, request.ConfigPath, repositoryRoot);
        }

        if (!runner.CodeGraphBinaryIsAvailable())
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Error,
                "'codegraph' is not on PATH, so the tools a run depends on cannot be indexed or tested."));
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Remedy,
                "install the CodeGraph CLI so that 'codegraph' resolves on PATH, then start the run again."));
            return Stopped(RunStartStatus.CodeGraphMissing, messages);
        }

        GitOutcome fetch = runner.Fetch();
        if (!fetch.Succeeded)
        {
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
            string remotePrefix = RunStartPlanner.Remote + "/";
            bool isExplicitRemote = resume.StartsWith(remotePrefix, StringComparison.Ordinal);
            string stripped = isExplicitRemote
                ? resume[remotePrefix.Length..]
                : resume;

            if (string.Equals(stripped, "HEAD", StringComparison.Ordinal))
            {
                return Refuse(
                    messages,
                    $"'{resume}' is a symbolic reference, not a branch name. Pass the " +
                    "branch a plan or pull request already owns.");
            }

            if (isExplicitRemote)
            {
                if (runner.HasRemoteBranch(stripped))
                {
                    resumeRef = remotePrefix + stripped;
                    resumeTarget = stripped;
                }
                else
                {
                    return Refuse(
                        messages,
                        $"No local or origin branch is named '{resume}', so there is nothing to resume. Pass the " +
                        "branch a plan or pull request already owns.");
                }
            }
            else if (runner.HasLocalBranch(resume))
            {
                resumeRef = resume;
                resumeTarget = resume;
            }
            else if (runner.HasRemoteBranch(resume))
            {
                resumeRef = remotePrefix + resume;
                resumeTarget = resume;
            }
            else
            {
                return Refuse(
                    messages,
                    $"No local or origin branch is named '{resume}', so there is nothing to resume. Pass the " +
                    "branch a plan or pull request already owns.");
            }
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

        GitOutcome status = runner.CheckoutStatus();
        if (!status.Succeeded)
        {
            return Fail(messages, $"git status failed: {status.FirstErrorLine}");
        }

        GitOutcome worktrees = runner.WorktreeList();
        if (!worktrees.Succeeded)
        {
            return Fail(messages, $"git worktree list failed: {worktrees.FirstErrorLine}");
        }

        bool anotherWorktreeIsAttached = GitWorktreePorcelain.HasMultiple(worktrees.StandardOutput);

        RunStartLocation location = RunStartPlanner.DecideLocation(
            status.StandardOutput.Trim().Length == 0,
            anotherWorktreeIsAttached);

        string target = resumeTarget ?? branch;
        string? worktreePath = null;
        GitOutcome created;
        if (location == RunStartLocation.InPlace)
        {
            created = resumeTarget is null
                ? runner.CreateBranchInPlace(target, startRef)
                : runner.SwitchToBranch(target);
        }
        else
        {
            string? held = resumeTarget is null
                ? null
                : GitWorktreePorcelain.Holding(worktrees.StandardOutput, resumeTarget);
            if (held is not null && files.DirectoryExists(held))
            {
                // Git allows a branch in one worktree only, so a resume continues in the one that holds it.
                worktreePath = held;
                created = new GitOutcome(0, string.Empty, string.Empty);
            }
            else
            {
                // A folder deleted by hand stays registered and would still claim the branch. Pruning removes
                // only entries whose folder is gone.
                if (held is not null)
                {
                    GitOutcome pruned = runner.Git("worktree", "prune");
                    if (!pruned.Succeeded)
                    {
                        return Fail(
                            messages,
                            $"git worktree prune failed, so the deleted folder '{held}' still claims " +
                            $"'{target}': {pruned.FirstErrorLine}");
                    }
                }

                worktreePath = runner.WorktreePathFor(target, clock.GetUtcNow());
                try
                {
                    created = resumeTarget is null
                        ? runner.CreateBranchInWorktree(target, startRef, worktreePath)
                        : runner.AddWorktreeForBranch(target, worktreePath, resumeRef);
                }
                finally
                {
                    // Released whether git succeeded, failed, or threw, so no claim outlives its run.
                    runner.ReleaseWorktreeClaim(worktreePath);
                }
            }
        }

        // The check before creation can lose to another run of the same slug. That loser gets the same refusal
        // and exit code as a branch that existed from the start, not a generic git failure.
        if (!created.Succeeded && resume is null && runner.HasLocalBranch(branch))
            return Refuse(messages, BranchExists(branch));

        if (!created.Succeeded)
        {
            return Fail(messages, $"git could not put '{target}' in place: {created.FirstErrorLine}");
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
            repository is null ? null : new RunStartPlace(repository, worktreePath ?? repositoryRoot, clock.GetUtcNow(), request.Autonomy));

        // A new worktree has no index of its own, so the index goes where the run works. A failure here does not
        // undo the branch: the operator repairs the index and the run continues in that tree.
        string indexTree = worktreePath ?? repositoryRoot;
        IProcessExecutor indexExecutor = request.IndexExecutor ?? new BoundedIndexExecutor(processes, DoctorOptions.Defaults.IndexTimeout);
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
            return Stopped(RunStartStatus.ToolFailure, messages);
        }

        if (index.Status is WorktreeIndexStatus.Failed or WorktreeIndexStatus.BinaryMissing)
        {
            messages.Add(new RunStartMessage(
                RunStartMessageKind.Warning,
                $"the CodeGraph index for '{indexTree}' could not be prepared ({IndexReason(index)}), so the run continues " +
                $"without it. Repair it with: kyber-weave doctor --ensure {ShellQuote.Quote(indexTree)}",
                StandardErrorWhenJson: true));
        }

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
            : ".kyber-weave/kyber-weave.yml";

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
