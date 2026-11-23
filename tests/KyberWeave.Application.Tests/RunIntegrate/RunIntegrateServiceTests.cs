using KyberWeave.Application.RunIntegrate;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunIntegrate;

/// <summary>
/// Resolving the two worktrees, choosing between a fast-forward and a cherry-pick, refusing what cannot be
/// integrated, and the integration lock — all over the in-memory file system and a scripted git runner.
/// </summary>
public sealed class RunIntegrateServiceTests
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
    private bool _evidencePresentAtRemoval;
    private int _commitsAhead = 1;
    private string _dirtyStatus = string.Empty;
    private string _runDirtyStatus = string.Empty;
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

    [Fact]
    public void ATaskWorktreeThatDoesNotExistIsRefused()
    {
        RunIntegrateOutcome outcome = Integrate(Request(task: "/work/absent"));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("/work/absent", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTaskWorktreeDefaultsToTheWorkingDirectory()
    {
        RunIntegrateOutcome outcome = Service().Integrate(new RunIntegrateRequest("T3", null, Run, Task, Home));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Task, "rev-parse", "--show-toplevel"));
    }

    [Fact]
    public void TheCanonicalChecksRunBeforeAnyOtherCallInTheTaskWorktree()
    {
        _ = Integrate(Request());

        Assert.Equal("-C " + Task + " rev-parse --absolute-git-dir", FirstCallIn(Task));
    }

    [Fact]
    public void TheRepositorysCanonicalCheckoutIsRefusedAsTheTaskWorktree()
    {
        RunIntegrateOutcome outcome = Integrate(Request(task: Canonical));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("canonical checkout", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Canonical, "rev-parse", "--absolute-git-dir"));
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Canonical && call.Arguments.Contains("--show-toplevel", StringComparer.Ordinal));
    }

    [Fact]
    public void WithNoLiveRunRecordTheStepRefusesAndNamesTheFlagThatSuppliesOne()
    {
        _checkouts.Runs = [];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveRunRecordSuppliesTheRunWorktreeWhenNoneIsNamed()
    {
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch)
        ];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
    }

    [Fact]
    public void TheLiveRunWhoseBranchIsAnAncestorOfTheTaskBranchIsChosenWhenSeveralAreLive()
    {
        _ancestorRunBranches = new HashSet<string>(StringComparer.Ordinal) { RunBranch };
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Canonical + "/older");
    }

    [Fact]
    public void SeveralLiveRunsWithNoAncestorRunBranchAreRefusedAndNamed()
    {
        _ancestorRunBranches = [];
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("run/older", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(RunBranch, outcome.Message, StringComparison.Ordinal);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.WorkingDirectory == Run || call.WorkingDirectory == Canonical + "/older");
    }

    [Fact]
    public void SeveralLiveRunsWhoseBranchesAreAllAncestorsAreRefusedAsAmbiguous()
    {
        _ancestorRunBranches = new HashSet<string>(StringComparer.Ordinal) { RunBranch, "run/older" };
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("--run-worktree", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositorysCanonicalCheckoutIsRefusedAsTheRunWorktree()
    {
        RunIntegrateOutcome outcome = Integrate(Request(run: Canonical));

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("canonical checkout", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public void TheCanonicalChecksRunBeforeAnyOtherCallInTheRunWorktree()
    {
        _ = Integrate(Request());

        Assert.Equal("-C " + Run + " rev-parse --absolute-git-dir", FirstCallIn(Run));
    }

    [Fact]
    public void ATaskWorktreeThatIsTheRunWorktreeReportsTheRunHeadAndChangesNothing()
    {
        RunIntegrateOutcome outcome = Integrate(Request(task: Run));

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(BeforeHead, outcome.RunHead);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public void UncommittedTrackedChangesInTheTaskWorktreeAreRefusedWithTheirPaths()
    {
        _dirtyStatus = " M src/Changed.cs\nA  src/New.cs\n";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Changed.cs", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("src/New.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public void UncommittedTrackedChangesInTheRunWorktreeAreRefusedBeforeAFastForward()
    {
        _runDirtyStatus = " M src/Run.cs\n";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Run.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.True(LockIsFree());
    }

    [Fact]
    public void UncommittedTrackedChangesInTheRunWorktreeAreRefusedBeforeACherryPick()
    {
        _runIsAncestor = false;
        _runDirtyStatus = " M src/Run.cs\n";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("src/Run.cs", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
        Assert.True(LockIsFree());
    }

    [Fact]
    public void ATaskWorktreeOnTheRunBranchIsRefusedRatherThanRemoved()
    {
        _taskBranch = RunBranch;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains(RunBranch, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public void ATaskBranchThatGitWouldReadAsAnOptionFailsTheIntegration()
    {
        _taskBranch = "-evil";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("-evil", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Task, "check-ref-format", "--branch", "-evil"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("rev-list", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public void ARunBranchThatGitWouldReadAsAnOptionFailsTheIntegration()
    {
        _runBranch = "-evil";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("-evil", outcome.Message, StringComparison.Ordinal);
        Assert.True(_git.Called("-C", Run, "check-ref-format", "--branch", "-evil"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("rev-list", StringComparer.Ordinal));
    }

    [Fact]
    public void AMergeBaseFailureIsReportedInsteadOfReadAsNotAnAncestor()
    {
        _mergeBaseErrorCode = 128;
        _mergeBaseError = "fatal: Not a valid object name run/main";

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("Not a valid object name", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public void AMergeBaseFailureWhileChoosingBetweenLiveRunsFailsTheIntegration()
    {
        _candidateCheckFails = true;
        _candidateCheckError = "fatal: Not a valid object name run/older";
        _checkouts.Runs =
        [
            new RunRecord(CanonicalGitDirectory, "run/older", Canonical + "/older", DateTimeOffset.UnixEpoch),
            new RunRecord(CanonicalGitDirectory, RunBranch, Run, DateTimeOffset.UnixEpoch.AddHours(1))
        ];

        RunIntegrateOutcome outcome = Integrate(Request(run: null));

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains("Not a valid object name", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoCommitTheRunLacksTheStepReportsNothingToIntegrate()
    {
        _commitsAhead = 0;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.NothingToIntegrate, outcome.Status);
        Assert.Equal(BeforeHead, outcome.RunHead);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public void ARetryThatFindsNothingLeftToIntegrateStillCopiesEvidenceAndRemovesTheTaskWorktree()
    {
        _commitsAhead = 0;
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.NothingToIntegrate, outcome.Status);
        Assert.True(_files.FileExists(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.True(_git.Called("-C", Run, "worktree", "remove", "--force", Task));
        Assert.True(_git.Called("-C", Run, "branch", "-D", "--end-of-options", TaskBranch));
    }

    [Fact]
    public void TheRunBranchIsFastForwardedWhenItIsAnAncestorOfTheTaskBranch()
    {
        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(AfterHead, outcome.RunHead);
        Assert.True(_git.Called("-C", Run, "merge", "--ff-only", "--end-of-options", TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("cherry-pick", StringComparer.Ordinal));
    }

    [Fact]
    public void TheTaskCommitsAreCherryPickedDroppingCommitsTheRunAlreadyHas()
    {
        _runIsAncestor = false;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--empty=drop", "--end-of-options", RunBranch + ".." + TaskBranch));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("merge", StringComparer.Ordinal));
    }

    [Fact]
    public void AConflictAbortsTheCherryPickAndLeavesTheTaskWorktreeAlone()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _conflicted = ["src/Left.cs", "src/Right.cs"];

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Conflict, outcome.Status);
        Assert.Equal(["src/Left.cs", "src/Right.cs"], outcome.Files);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--abort"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
    }

    [Fact]
    public void ACherryPickThatFailsWithoutAConflictIsAbortedAndTheNextAttemptCanRun()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _conflicted = [];

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--abort"));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));

        _cherryPickSucceeds = true;
        RunIntegrateOutcome retried = Integrate(Request());

        Assert.True(retried.Status == RunIntegrateStatus.Integrated, retried.Message);
    }

    [Fact]
    public void ACherryPickAbortThatFailsIsFinishedWithQuit()
    {
        _runIsAncestor = false;
        _cherryPickSucceeds = false;
        _abortSucceeds = false;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(_git.Called("-C", Run, "cherry-pick", "--quit"));
    }

    [Fact]
    public void TheScratchpadIsCopiedBeforeTheTaskWorktreeIsRemoved()
    {
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3/logs");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/logs/tail.txt", "tail");

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_files.FileExists(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.Equal("gate passed", _files.ReadAllText(Run + "/.agent_scratchpad/T3/report.md"));
        Assert.Equal("tail", _files.ReadAllText(Run + "/.agent_scratchpad/T3/logs/tail.txt"));
        Assert.True(_evidencePresentAtRemoval, "the scratchpad must be copied before the task worktree is removed");
    }

    [Fact]
    public void ACopyEvidenceFailureStillReportsTheIntegratedCommitsAndKeepsTheTaskWorktree()
    {
        _files.CreateDirectory(Task + "/.agent_scratchpad/T3");
        _files.WriteAllText(Task + "/.agent_scratchpad/T3/report.md", "gate passed");
        _files.FailWhen(operation =>
            operation.Name == nameof(IWritableFileSystem.WriteAllBytes) &&
            operation.Path == Run + "/.agent_scratchpad/T3/report.md");

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.IntegratedWithoutEvidence, outcome.Status);
        Assert.Equal(AfterHead, outcome.RunHead);
        Assert.Contains("scratchpad", outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("worktree", StringComparer.Ordinal));
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    [Fact]
    public void TheTaskWorktreeIsRemovedBeforeItsBranchIsDeleted()
    {
        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_git.Called("-C", Run, "worktree", "remove", "--force", Task));
        Assert.True(_git.Called("-C", Run, "branch", "-D", "--end-of-options", TaskBranch));
        Assert.True(IndexOf("worktree") < IndexOf("branch"));
    }

    [Fact]
    public void TheLockIsFreeAfterASuccessfulIntegration()
    {
        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(LockIsFree());
    }

    [Fact]
    public void TheLockIsFreeWhenTheIntegrationFails()
    {
        _mergeSucceeds = false;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.True(LockIsFree());
    }

    [Fact]
    public void TheLockFileHoldsTheHolderTextForDiagnosticsAndIsNeverDeleted()
    {
        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(_files.FileExists(LockPath), "the lock file is reused, so it is never deleted");
        string holder = _files.ReadAllText(LockPath);
        Assert.Contains(_environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), holder, StringComparison.Ordinal);
        Assert.Contains("T3", holder, StringComparison.Ordinal);
    }

    [Fact]
    public void AWaitingIntegrationProceedsOnceTheHolderReleasesTheLock()
    {
        using Stream held = _files.OpenExclusive(LockPath);

        RunIntegrateOutcome outcome = _time.RunAdvancingOnParks(
            () => Service().Integrate(Request()),
            TimeSpan.FromMilliseconds(500),
            parkNumber: 1,
            atPark: held.Dispose);

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.True(LockIsFree());
    }

    [Fact]
    public void ALockThatStaysHeldPastTheWaitFailsAfterTheRetriesAndNamesTheLockFile()
    {
        using Stream held = _files.OpenExclusive(LockPath);

        RunIntegrateOutcome outcome = _time.RunAdvancingOnParks(
            () => Service().Integrate(Request()),
            TimeSpan.FromMilliseconds(500),
            parkNumber: int.MaxValue,
            atPark: null);

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(LockPath, outcome.Message!, StringComparison.Ordinal);
        Assert.Contains("120", outcome.Message!, StringComparison.Ordinal);
        Assert.False(LockIsFree(), "another worker still holds the lock");
    }

    [Fact]
    public void ARemovalThatFailsKeepsTheBranchAndReportsTheIntegrationItAlreadyMade()
    {
        _removeSucceeds = false;

        RunIntegrateOutcome outcome = Integrate(Request());

        Assert.Equal(RunIntegrateStatus.Failed, outcome.Status);
        Assert.Contains(AfterHead, outcome.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(_git.Calls, call => call.Arguments.Contains("branch", StringComparer.Ordinal));
    }

    private RunIntegrateOutcome Integrate(RunIntegrateRequest request)
    {
        return Service().Integrate(request);
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
            return Ok(_head);
        }

        if (command.StartsWith("worktree remove ", StringComparison.Ordinal))
        {
            _evidencePresentAtRemoval = _files.FileExists(Run + "/.agent_scratchpad/T3/report.md");
            return _removeSucceeds ? Ok() : Exit(1, "worktree is locked");
        }

        if (command.StartsWith("branch -D ", StringComparison.Ordinal))
        {
            return Ok();
        }

        return Exit(1, "unscripted: " + command);
    }

    private static GitOutcome Ok(string output = "") => new(0, output, string.Empty);

    private static GitOutcome Exit(int code, string error = "") => new(code, string.Empty, error);
}
