using System.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins <see cref="WorktreeIndexGuard" />: the CodeGraph index a worktree or a review gets before any work starts.
/// </summary>
/// <remarks>
///     A worktree has no index of its own, and nothing else builds one, so the guard is the only thing standing
///     between a worker and an unindexed tree. Each case is a repository on disk and a scripted CodeGraph child; no
///     real <c>codegraph</c> runs.
/// </remarks>
public sealed class WorktreeIndexGuardTests : IDisposable
{
    private const string Governed = "onboarding: done\n";
    private const string StopOff = "stop-on-tool-failure: false\nonboarding: done\n";
    private const string IndexCheckId = "codegraph.index";

    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly string _locks;

    public WorktreeIndexGuardTests()
    {
        _root = Path.Combine(_temp.Path, "tree");
        _locks = Path.Combine(_temp.Path, "locks");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_locks);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void AMissingIndexIsInitialisedAtTheTreeAndIsReadyAndCreated()
    {
        Govern(Governed);
        ScriptedProcessExecutor executor = Codegraph();

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.Ready, outcome.Status);
        Assert.True(outcome.Created, "A tree with no .codegraph folder must report that this run created the index.");
        Assert.False(outcome.StopsRun, "A ready index never stops a run.");
        ProcessStartInfo init = Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.Equal(_root, init.WorkingDirectory);
    }

    [Fact]
    public void AnExistingIndexIsSyncedAndIsReadyButNotCreated()
    {
        Govern(Governed);
        Directory.CreateDirectory(Path.Combine(_root, ".codegraph"));
        ScriptedProcessExecutor executor = Codegraph();

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.Ready, outcome.Status);
        Assert.False(outcome.Created, "An index that was already on disk was not created by this call.");
        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("sync", StringComparer.Ordinal));
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
    }

    [Fact]
    public void AFolderCodeGraphSaysIsNotInitialisedFallsBackToInitAndIsReady()
    {
        Govern(Governed);
        Directory.CreateDirectory(Path.Combine(_root, ".codegraph"));
        ScriptedProcessExecutor executor = new(startInfo => startInfo.FileName switch
        {
            "git" => DoctorHarness.Ok(),
            "codegraph" when startInfo.ArgumentList.Contains("sync", StringComparer.Ordinal) =>
                new ProcessResult(1, string.Empty, "CodeGraph not initialized"),
            "codegraph" => DoctorHarness.Ok(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.Ready, outcome.Status);
        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.True(outcome.Created, "An index that init built counts as created, even when the folder was already there.");
    }

    [Fact]
    public void AnUnreadableConfigurationIsAFailedIndexNotACrash()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The test removes read permission with a Unix file mode.");
            return;
        }

        Govern(Governed);
        string config = Path.Combine(_root, ".kyber-weave", "kyber-weave.yml");
        File.SetUnixFileMode(config, UnixFileMode.None);
        try
        {
            if (CanStillRead(config))
            {
                Assert.Skip("File permissions are not enforced for this user, as when the suite runs as root.");
            }

            WorktreeIndexOutcome outcome = Ensure(Codegraph());

            Assert.Equal(WorktreeIndexStatus.Failed, outcome.Status);
            Assert.Contains(outcome.Checks, check => !check.Passed);
        }
        finally
        {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void AnInitialisationThatFailsAfterTheOneRepairIsFailedAndStopsTheRunByDefault()
    {
        Govern(Governed);
        ScriptedProcessExecutor executor = Codegraph("init", "index");

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.Failed, outcome.Status);
        Assert.True(
            outcome.StopsRun,
            "With stop-on-tool-failure absent, a failed index must stop the run; absence is the stop setting.");
        Assert.False(outcome.Created, "An index that never came up was not created.");
        Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("index", StringComparer.Ordinal));
        Assert.Contains(
            outcome.Checks,
            check => check.Id == IndexCheckId && !check.Passed && check.Fix is not null);
    }

    [Fact]
    public void AFailedIndexDoesNotStopTheRunWhenStopOnToolFailureIsFalse()
    {
        Govern(StopOff);

        WorktreeIndexOutcome outcome = Ensure(Codegraph("init", "index"));

        Assert.Equal(WorktreeIndexStatus.Failed, outcome.Status);
        Assert.False(outcome.StopsRun, "stop-on-tool-failure: false must let the run continue past a failed index.");
    }

    [Fact]
    public void AMissingCodeGraphBinaryIsBinaryMissingAndStopsTheRunByDefault()
    {
        Govern(Governed);
        ScriptedProcessExecutor executor = new(startInfo => startInfo.FileName switch
        {
            "git" => DoctorHarness.Ok(),
            "codegraph" => throw DoctorHarness.BinaryNotFound(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.BinaryMissing, outcome.Status);
        Assert.True(outcome.StopsRun, "A missing binary stops the run unless the operator turned the stop off.");
        Assert.False(outcome.Created);
    }

    [Fact]
    public void AMissingCodeGraphBinaryDoesNotStopTheRunWhenStopOnToolFailureIsFalse()
    {
        Govern(StopOff);
        ScriptedProcessExecutor executor = new(startInfo => startInfo.FileName switch
        {
            "git" => DoctorHarness.Ok(),
            "codegraph" => throw DoctorHarness.BinaryNotFound(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.BinaryMissing, outcome.Status);
        Assert.False(outcome.StopsRun);
    }

    [Fact]
    public void ARepositoryWithoutTheHostConfigurationIsNotGovernedAndStartsNoCodeGraphChild()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        ScriptedProcessExecutor executor = Codegraph();

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.NotGoverned, outcome.Status);
        Assert.Empty(executor.CallsTo("codegraph"));
        Assert.False(outcome.StopsRun);
        Assert.False(outcome.Created);
    }

    [Fact]
    public void ARepositoryWhoseDocsRootIsMissingIsNotGovernedAndStartsNoCodeGraphChild()
    {
        Govern(Governed, createDocs: false);
        ScriptedProcessExecutor executor = Codegraph();

        WorktreeIndexOutcome outcome = Ensure(executor);

        Assert.Equal(WorktreeIndexStatus.NotGoverned, outcome.Status);
        Assert.Empty(executor.CallsTo("codegraph"));
    }

    private static bool CanStillRead(string path)
    {
        try
        {
            _ = File.ReadAllText(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Govern(string yaml, bool createDocs = true)
    {
        string directory = Path.Combine(_root, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml);
        if (createDocs)
            Directory.CreateDirectory(Path.Combine(_root, "docs"));
    }

    private WorktreeIndexOutcome Ensure(ScriptedProcessExecutor executor) =>
        WorktreeIndexGuard.Ensure(_root, DoctorHarness.Options("kyber-weave-mcp"), executor, _locks);

    /// <summary>A CodeGraph that answers every verb healthily, except that each named verb exits 1.</summary>
    private static ScriptedProcessExecutor Codegraph(params string[] failingVerbs) =>
        new(startInfo => startInfo.FileName switch
        {
            "git" => DoctorHarness.Ok(),
            "codegraph" => startInfo.ArgumentList.Any(argument => failingVerbs.Contains(argument, StringComparer.Ordinal))
                ? new ProcessResult(1, string.Empty, "boom")
                : DoctorHarness.Ok(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });
}
