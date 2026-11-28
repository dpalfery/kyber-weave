using System.ComponentModel;
using KyberWeave.Application.RunIntegrate;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunIntegrate;

/// <summary>
/// Resolving the two worktrees, choosing between a fast-forward and a cherry-pick, refusing what cannot be
/// integrated, and the integration lock — all over the in-memory file system and a scripted git runner.
/// </summary>
public sealed class RunIntegrateServiceTests : IDisposable
{
    private const string Canonical = "/work/repo";
    private const string Run = "/work/run";
    private const string Task = "/work/task";
    private const string Home = "/home/dev";
    private const string RunGitDirectory = Canonical + "/.git/worktrees/run";
    private const string TaskGitDirectory = Canonical + "/.git/worktrees/task";
    private const string CanonicalGitDirectory = Canonical + "/.git";
    private const string RunBranch = "run/main";
    private const string TaskBranch = "task/T3";
    private const string BeforeHead = "1111111111111111111111111111111111111111";
    private const string AfterHead = "2222222222222222222222222222222222222222";
    private const string LockPath = RunGitDirectory + "/" + RunIntegrateService.LockFileName;

    private readonly ParkObservingTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment =
        new(currentDirectory: "/work", homeDirectory: Home, operatingSystem: OperatingSystemKind.Linux);
    private readonly FakeRunCheckout _checkouts = new()
    {
        CanonicalCheckouts = { [CanonicalGitDirectory] = Canonical }
    };
    private readonly ScriptedGitCommandRunner _git;

    private bool _runIsAncestor = true;
    private bool _mergeSucceeds = true;
    private bool _cherryPickSucceeds = true;
    private bool _abortSucceeds = true;
    private bool _removeSucceeds = true;
    private Exception? _gitLaunchFailure;
    private bool _worktreeRemoveThrows;
    private bool _branchDeleteThrows;
    private bool _mergeThrows;
    private bool _headReadThrows;
    private bool _evidencePresentAtRemoval;
    private int _commitsAhead = 1;
    private string _dirtyStatus = string.Empty;
    private string _runDirtyStatus = string.Empty;
    private string _taskChangedPaths = "src/Task.cs";
    private string _taskBranch = TaskBranch;
    private string _runBranch = RunBranch;
    private int? _mergeBaseErrorCode;
    private string _mergeBaseError = string.Empty;
    private bool _candidateCheckFails;
    private string _candidateCheckError = string.Empty;
    private string[] _conflicted = [];
    private string _head = BeforeHead;

    /// <summary>The run branches git considers ancestors of the task branch, read in the task worktree.</summary>
    private HashSet<string> _ancestorRunBranches = new(StringComparer.Ordinal) { RunBranch };

    public RunIntegrateServiceTests()
    {
        _files = new InMemoryFileSystem(_time);
        _paths = new FakePathResolver(_files);
        _git = new ScriptedGitCommandRunner(Answer);

        _files.CreateDirectory(Canonical);
        _files.CreateDirectory(CanonicalGitDirectory);
        _files.CreateDirectory(RunGitDirectory);
        _files.CreateDirectory(TaskGitDirectory);
        _files.CreateDirectory(Run);
        _files.CreateDirectory(Task);
        _files.WriteAllText(Run + "/.git", "gitdir: " + RunGitDirectory + "\n");
        _files.WriteAllText(Task + "/.git", "gitdir: " + TaskGitDirectory + "\n");
    }

    public void Dispose()
    {
        _time.Dispose();
    }

    [Fact]
    public async Task ATaskWorktreeThatDoesNotExistIsRefused()
    {
        RunIntegrateOutcome outcome = await Integrate(Request(task: "/work/absent"));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("/work/absent", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTaskWorktreeDefaultsToTheWorkingDirectory()
    {
        RunIntegrateOutcome outcome = await Service().IntegrateAsync(new RunIntegrateRequest("T3", null, Run, Task, Home));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Task, "rev-parse", "--show-toplevel"));
    }

    [Fact]
    public async Task TheCanonicalChecksRunBeforeAnyOtherCallInTheTaskWorktree()
    {
        _ = await Integrate(Request());

        Assert.Equal("-C " + Task + " rev-parse --absolute-git-dir", FirstCallIn(Task));
    }

    [Fact]
    public async Task TheRepositorysCanonicalCheckoutIsRefusedAsTheTaskWorktree()
    {
        RunIntegrateOutcome outcome = await Integrate(Request(task: Canonical));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("canonical checkout", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Canonical, "rev-parse", "--absolute-git-dir"));
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Canonical && call.Arguments.Contains("--show-toplevel", StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithNoLiveRunRecordTheStepRefusesAndNamesTheFlagThatSuppliesOne()
    {
        _checkouts.Runs = [];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveRunRecordSuppliesTheRunWorktreeWhenNoneIsNamed()
    {
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch)
        ];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
    }

    [Fact]
    public async Task TheLiveRunWhoseBranchIsAnAncestorOfTheTaskBranchIsChosenWhenSeveralAreLive()
    {
        _ancestorRunBranches = new HashSet<string>(StringComparer.Ordinal) { RunBranch };
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Canonical + "/older");
    }

    [Fact]
    public async Task SeveralLiveRunsWithNoAncestorRunBranchAreRefusedAndNamed()
    {
        _ancestorRunBranches = [];
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("run/older", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(RunBranch, outcome.Message, StringComparison.Ordinal);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Run || call.WorkingDirectory == Canonical + "/older");
    }

    [Fact]
    public async Task SeveralLiveRunsWhoseBranchesAreAllAncestorsAreRefusedAsAmbiguous()
    {
        _ancestorRunBranches = new HashSet<string>(StringComparer.Ordinal) { RunBranch, "run/older" };
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRepositorysCanonicalCheckoutIsRefusedAsTheRunWorktree()
    {
        RunIntegrateOutcome outcome = await Integrate(Request(run: Canonical));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("canonical checkout", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheCanonicalChecksRunBeforeAnyOtherCallInTheRunWorktree()
    {
        _ = await Integrate(Request());

        Assert.Equal("-C " + Run + " rev-parse --absolute-git-dir", FirstCallIn(Run));
    }

    [Fact]
    public async Task ATaskWorktreeThatIsTheRunWorktreeReportsTheRunHeadAndChangesNothing()
    {
        RunIntegrateOutcome outcome = await Integrate(Request(task: Run));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(BeforeHead, outcome.RunHead);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public async Task UncommittedTrackedChangesInTheTaskWorktreeAreRefusedWithTheirPaths()
    {
        _dirtyStatus = " M src/Changed.cs\nA  src/New.cs\n";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Changed.cs", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("src/New.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public async Task UncommittedTrackedChangesInTheRunWorktreeAreRefusedBeforeAFastForward()
    {
        _runDirtyStatus = " M src/Run.cs\n";
        _taskChangedPaths = "src/Run.cs";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Run.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task UncommittedTrackedChangesInTheRunWorktreeAreRefusedBeforeACherryPick()
    {
        _runIsAncestor = false;
        _runDirtyStatus = " M src/Run.cs\n";
        _taskChangedPaths = "src/Run.cs";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Run.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task AnUnrelatedUncommittedFileInTheRunWorktreeIsLeftInPlaceAndReported()
    {
        _files.CreateDirectory(Run + "/docs/plans");
        _files.WriteAllText(Run + "/docs/plans/hill-climb-run-04.md", "the architect's revision\n");
        _runDirtyStatus = " M docs/plans/hill-climb-run-04.md\n";
        _taskChangedPaths = "src/Task.cs";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(AfterHead, outcome.RunHead);
        Assert.Equal(
            "the architect's revision\n",
            _files.ReadAllText(Run + "/docs/plans/hill-climb-run-04.md"));
        Assert.Contains(
            outcome.Notes ?? [],
            note => note.Contains("docs/plans/hill-climb-run-04.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATaskWorktreeOnTheRunBranchIsRefusedRatherThanRemoved()
    {
        _taskBranch = RunBranch;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains(RunBranch, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ATaskBranchThatGitWouldReadAsAnOptionFailsTheIntegration()
    {
        _taskBranch = "-evil";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("-evil", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Task, "check-ref-format", "--branch", "-evil"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("rev-list", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARunBranchThatGitWouldReadAsAnOptionFailsTheIntegration()
    {
        _runBranch = "-evil";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("-evil", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Run, "check-ref-format", "--branch", "-evil"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("rev-list", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMergeBaseFailureIsReportedInsteadOfReadAsNotAnAncestor()
    {
        _mergeBaseErrorCode = 128;
        _mergeBaseError = "fatal: Not a valid object name run/main";

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("Not a valid object name", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMergeBaseFailureWhileChoosingBetweenLiveRunsFailsTheIntegration()
    {
        _candidateCheckFails = true;
        _candidateCheckError = "fatal: Not a valid object name run/older";
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = await Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("Not a valid object name", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoCommitTheRunLacksTheStepReportsNothingToIntegrate()
    {
        _commitsAhead = 0;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.NothingToIntegrate, outcome.Status);
        Assert.Equal(BeforeHead, outcome.RunHead);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARetryThatFindsNothingLeftToIntegrateStillCopiesEvidenceAndRemovesTheTaskWorktree()
    {
        _commitsAhead = 0;
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.NothingToIntegrate, outcome.Status);
        Assert.True(_files.FileExists(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.True(_git.Called("-C", Run, "worktree", "remove", "--force", Task));
        Assert.True(_git.Called("-C", Run, "branch", "-D", "--end-of-options", TaskBranch));
    }

    [Fact]
    public async Task TheRunBranchIsFastForwardedWhenItIsAnAncestorOfTheTaskBranch()
    {
        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(AfterHead, outcome.RunHead);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheTaskCommitsAreCherryPickedDroppingCommitsTheRunAlreadyHas()
    {
        _runIsAncestor = false;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--empty=drop", "--end-of-options", RunBranch + ".." + TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AConflictAbortsTheCherryPickAndLeavesTheTaskWorktreeAlone()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _conflicted = ["src/Left.cs", "src/Right.cs"];

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Conflict, outcome.Status);
        Assert.Equal(["src/Left.cs", "src/Right.cs"], outcome.Files);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--abort"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACherryPickThatFailsWithoutAConflictIsAbortedAndTheNextAttemptCanRun()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _conflicted = [];

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--abort"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));

        _cherryPickSucceeds = true;
        RunIntegrateOutcome retried = await Integrate(Request());

        Assert.True(retried.Status == RunIntegrateStatus.Integrated, retried.Message);
    }

    [Fact]
    public async Task ACherryPickAbortThatFailsIsFinishedWithQuit()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _abortSucceeds = false;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--quit"));
    }

    [Fact]
    public async Task TheScratchpadIsCopiedBeforeTheTaskWorktreeIsRemoved()
    {
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3/logs");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/logs/tail.txt", "tail");

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_files.FileExists(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.Equal("gate passed", _files.ReadAllText(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.Equal("tail", _files.ReadAllText(Run + "/.agent_scratchpad/T3/logs/tail.txt"));
        Assert.True(_evidencePresentAtRemoval, "the scratchpad must be copied before the task worktree is removed");
    }

    [Fact]
    public async Task ACopyEvidenceFailureStillReportsTheIntegratedCommitsAndKeepsTheTaskWorktree()
    {
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");
        _files.FailWhen(operation =>
            operation.Name == nameof(IWritableFileSystem.WriteAllBytes) &&
            operation.Path == Run + "/.agent_scratchpad/T3/report.md");

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.IntegratedWithoutEvidence, outcome.Status);
        Assert.Equal(AfterHead, outcome.RunHead);
        Assert.Contains("scratchpad", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheTaskWorktreeIsRemovedBeforeItsBranchIsDeleted()
    {
        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "worktree", "remove", "--force", Task));
        Assert.True(_git.Called("-C", Run, "branch", "-D", "--end-of-options", TaskBranch));
        Assert.True(IndexOf("worktree") < IndexOf("branch"));
    }

    [Fact]
    public async Task TheLockIsFreeAfterASuccessfulIntegration()
    {
        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task TheLockIsFreeWhenTheIntegrationFails()
    {
        _mergeSucceeds = false;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task TheLockFileHoldsTheHolderTextForDiagnosticsAndIsNeverDeleted()
    {
        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_files.FileExists(LockPath), "the lock file is reused, so it is never deleted");
        string holder = _files.ReadAllText(LockPath);
        Assert.Contains(_environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), holder, StringComparison.Ordinal);
        Assert.Contains("T3", holder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitingIntegrationProceedsOnceTheHolderReleasesTheLock()
    {
        using Stream held = _files.OpenExclusive(LockPath);

        RunIntegrateOutcome outcome = await _time.RunAdvancingOnParksAsync(
            () => Service().IntegrateAsync(Request()),
            TimeSpan.FromMilliseconds(500),
            parkNumber: 1,
            atPark: held.Dispose);

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task ALockThatStaysHeldPastTheWaitFailsAfterTheRetriesAndNamesTheLockFile()
    {
        using Stream held = _files.OpenExclusive(LockPath);

        RunIntegrateOutcome outcome = await _time.RunAdvancingOnParksAsync(
            () => Service().IntegrateAsync(Request()),
            TimeSpan.FromMilliseconds(500),
            parkNumber: int.MaxValue,
            atPark: null);

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(LockPath, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("120", outcome.Message!, StringComparison.Ordinal);
        Assert.False(LockIsFree(), "another worker still holds the lock");
    }

    [Fact]
    public async Task ARemovalThatFailsKeepsTheBranchAndReportsTheIntegrationItAlreadyMade()
    {
        _removeSucceeds = false;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ABranchDeleteThatThrowsStillReportsTheIntegrationItAlreadyMade()
    {
        _branchDeleteThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("deleting the task branch", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("Unable to find the specified file.", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Run, "worktree", "remove", "--force", Task));
        Assert.True(LockIsFree());
    }

    [Fact]
    public async Task AWorktreeRemovalThatThrowsStillReportsTheIntegrationItAlreadyMade()
    {
        _worktreeRemoveThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("removing the task worktree", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AHeadReadThatThrowsAfterTheApplyStillReportsTheLandedCommits()
    {
        _headReadThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task commits are on the run branch, but reading its head failed", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("Unable to find the specified file.", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARetryHeadReadThatThrowsStillReportsTheLandedCommits()
    {
        _commitsAhead = 0;
        _headReadThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task commits are on the run branch, but reading its head failed", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("Unable to find the specified file.", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AThrowBeforeAnyCommitLandsStillReportsTheTaskAsNotIntegrated()
    {
        _mergeThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("win32")]
    [InlineData("invalid-operation")]
    public async Task ALaunchFailureBeforeAnyCommitLandsStillReportsTheTaskAsNotIntegrated(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _mergeThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task could not be integrated", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("win32")]
    [InlineData("invalid-operation")]
    public async Task ALaunchFailureAtThePostApplyHeadReadStillReportsTheLandedCommits(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _headReadThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(
            "the task commits are on the run branch, but reading its head failed",
            outcome.Message!,
            StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win32")]
    [InlineData("invalid-operation")]
    public async Task ALaunchFailureRemovingTheWorktreeStillReportsTheIntegrationItAlreadyMade(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _worktreeRemoveThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("removing the task worktree", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win32")]
    [InlineData("invalid-operation")]
    public async Task ALaunchFailureDeletingTheTaskBranchStillReportsTheIntegrationItAlreadyMade(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _branchDeleteThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("deleting the task branch", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("output-limit")]
    public async Task AProcessFailureBeforeAnyCommitLandsStillReportsTheTaskAsNotIntegrated(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _mergeThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        if (failure is not TimeoutException)
        {
            Assert.Contains("the task could not be integrated", outcome.Message!, StringComparison.Ordinal);
        }

        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("output-limit")]
    public async Task AProcessFailureAtThePostApplyHeadReadStillReportsTheLandedCommits(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _headReadThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(
            "the task commits are on the run branch, but reading its head failed",
            outcome.Message!,
            StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("output-limit")]
    public async Task AProcessFailureRemovingTheWorktreeStillReportsTheIntegrationItAlreadyMade(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _worktreeRemoveThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("removing the task worktree", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("output-limit")]
    public async Task AProcessFailureDeletingTheTaskBranchStillReportsTheIntegrationItAlreadyMade(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _gitLaunchFailure = failure;
        _branchDeleteThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("the task is integrated at " + AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("deleting the task branch", outcome.Message!, StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("output-limit")]
    public async Task ARetryHeadReadProcessFailureStillReportsTheLandedCommits(string failureKind)
    {
        Exception failure = LaunchFailure(failureKind);
        _commitsAhead = 0;
        _gitLaunchFailure = failure;
        _headReadThrows = true;

        RunIntegrateOutcome outcome = await Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(
            "the task commits are on the run branch, but reading its head failed",
            outcome.Message!,
            StringComparison.Ordinal);
        Assert.Contains(failure.Message, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be integrated", outcome.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchFailureExcludesOutputLimitWhileProcessFailureCoversEveryProcessRunnerException()
    {
        OutputLimitExceededException outputLimit = new("too much output");
        TimeoutException timeout = new("timed out");
        OperationCanceledException canceled = new("canceled");
        InvalidOperationException launch = new("failed to start");

        Assert.False(RunIntegrateService.IsGitLaunchFailure(outputLimit));
        Assert.False(RunIntegrateService.IsGitLaunchFailure(timeout));
        Assert.False(RunIntegrateService.IsGitLaunchFailure(canceled));
        Assert.True(RunIntegrateService.IsGitLaunchFailure(launch));

        Assert.True(RunIntegrateService.IsGitProcessFailure(outputLimit));
        Assert.True(RunIntegrateService.IsGitProcessFailure(timeout));
        Assert.True(RunIntegrateService.IsGitProcessFailure(canceled));
        Assert.True(RunIntegrateService.IsGitProcessFailure(launch));
    }

    private async Task<RunIntegrateOutcome> Integrate(RunIntegrateRequest request)
    {
        return await Service().IntegrateAsync(request);
    }

    private RunIntegrateService Service() =>
        new(
            _git,
            _checkouts,
            _files,
            _environment,
            _paths,
            _time);

    private bool LockIsFree()
    {
        try
        {
            _files.OpenExclusive(LockPath).Dispose();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static RunIntegrateRequest Request(string? run = Run, string? task = Task) =>
        new("T3", task, run, "/work", Home);

    private int IndexOf(string argument) =>
        _git.Calls.FindIndex(call => call.Arguments.Contains(argument, StringComparer.Ordinal));

    private string FirstCallIn(string worktree) =>
        "-C " + worktree + " " + string.Join(' ', _git.Calls.First(call => call.WorkingDirectory == worktree).Arguments.Skip(2));

    private GitOutcome Answer(string workingDirectory, IReadOnlyList<string> arguments)
    {
        string[] argv = arguments.Count > 1 && arguments[0] == "-C" ? [.. arguments.Skip(2)] : [.. arguments];
        string command = string.Join(' ', argv);

        if (command == "rev-parse --path-format=absolute --git-common-dir")
        {
            return Ok(CanonicalGitDirectory);
        }

        if (command == "rev-parse --show-toplevel")
        {
            return Ok(workingDirectory);
        }

        if (command == "rev-parse --absolute-git-dir")
        {
            return Ok(workingDirectory switch
            {
                Run => RunGitDirectory,
                Task => TaskGitDirectory,
                _ => CanonicalGitDirectory
            });
        }

        if (command == "symbolic-ref --short HEAD")
        {
            return Ok(workingDirectory == Task ? _taskBranch : _runBranch);
        }

        if (command.StartsWith("check-ref-format --branch ", StringComparison.Ordinal))
        {
            string name = command.Substring("check-ref-format --branch ".Length);
            return name.StartsWith('-') ? Exit(128, $"fatal: '{name}' is not a valid branch name") : Ok(name);
        }

        if (command == "status --porcelain --untracked-files=no")
        {
            return Ok(workingDirectory == Run ? _runDirtyStatus : _dirtyStatus);
        }

        if (command == "diff --name-only " + RunBranch + "..." + TaskBranch)
        {
            return Ok(_taskChangedPaths);
        }

        if (command.StartsWith("rev-list --count ", StringComparison.Ordinal))
        {
            return Ok(_commitsAhead.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (command.StartsWith("merge-base --is-ancestor ", StringComparison.Ordinal))
        {
            if (workingDirectory == Task)
            {
                if (_candidateCheckFails)
                {
                    return Exit(128, _candidateCheckError);
                }

                string branch = argv[^2];
                return Exit(_ancestorRunBranches.Contains(branch) ? 0 : 1);
            }

            if (_mergeBaseErrorCode.HasValue)
            {
                return Exit(_mergeBaseErrorCode.Value, _mergeBaseError);
            }

            return Exit(_runIsAncestor ? 0 : 1);
        }

        if (command == "merge --ff-only --end-of-options " + TaskBranch)
        {
            if (_mergeThrows)
            {
                throw GitLaunchFailure();
            }

            if (!_mergeSucceeds)
            {
                return Exit(1, "merge stopped");
            }

            _head = AfterHead;
            return Ok();
        }

        if (command == "cherry-pick --empty=drop --end-of-options " + RunBranch + ".." + TaskBranch)
        {
            if (!_cherryPickSucceeds)
            {
                return Exit(1, "conflict");
            }

            _head = AfterHead;
            return Ok();
        }

        if (command == "cherry-pick --abort")
        {
            return _abortSucceeds ? Ok() : Exit(128, "no cherry-pick in progress");
        }

        if (command == "cherry-pick --quit")
        {
            return Ok();
        }

        if (command == "diff --name-only --diff-filter=U")
        {
            return Ok(string.Join('\n', _conflicted));
        }

        if (command == "rev-parse HEAD")
        {
            if (_headReadThrows)
            {
                throw GitLaunchFailure();
            }

            return Ok(_head);
        }

        if (command.StartsWith("worktree remove ", StringComparison.Ordinal))
        {
            _evidencePresentAtRemoval = _files.FileExists(Run + "/.agent_scratchpad/T3/report.md");
            if (_worktreeRemoveThrows)
            {
                throw GitLaunchFailure();
            }

            return _removeSucceeds ? Ok() : Exit(1, "worktree is locked");
        }

        if (command.StartsWith("branch -D ", StringComparison.Ordinal))
        {
            if (_branchDeleteThrows)
            {
                throw GitLaunchFailure();
            }

            return Ok();
        }

        return Exit(1, "unscripted: " + command);
    }

    private static GitOutcome Ok(string output = "") => new(0, output, string.Empty);

    private static GitOutcome Exit(int code, string error = "") => new(code, string.Empty, error);

    /// <summary>The exception a git child throws when its executable cannot be launched.</summary>
    private Exception GitLaunchFailure() =>
        _gitLaunchFailure ?? new FileNotFoundException("Unable to find the specified file.");

    /// <summary>A git child launch failure of the kind <paramref name="kind"/> names.</summary>
    private static Exception LaunchFailure(string kind) => kind switch
    {
        "win32" => new Win32Exception(2, "The system cannot find the file specified."),
        "invalid-operation" => new InvalidOperationException("The child process could not be started."),
        "timeout" => new TimeoutException("The process did not exit within 120 seconds."),
        "canceled" => new OperationCanceledException("The operation was canceled."),
        "output-limit" => new OutputLimitExceededException("The process output exceeded the limit."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown launch failure kind.")
    };
}
