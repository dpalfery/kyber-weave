using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for the <c>--since &lt;epoch-seconds&gt;</c> option of <c>scripts/proc-guard.sh check-leftovers</c>.
/// </summary>
/// <remarks>
/// <para>
/// Design under test. A survivor's start time is derived from its elapsed time: start equals the clock minus
/// elapsed. The clock is <c>KW_PROC_GUARD_NOW</c> (epoch seconds) when it is set, and <c>date +%s</c> otherwise.
/// Elapsed time comes from <c>KW_PROC_GUARD_ETIME</c> when it is set. That names a fixture that prints one row per
/// pid, <c>&lt;pid&gt; &lt;etime&gt;</c>, in ps etime form. Otherwise the guard runs <c>ps -o etime= -p &lt;pid&gt;</c>.
/// A survivor counts when its start is at or after <c>--since</c>, or when its elapsed time is unknown. The unknown
/// case matches run mode, where a row the table listed is a survivor.
/// </para>
/// <para>
/// Why this shape. Start time is not a column of the process table the guard reads, and adding one would change
/// the real <c>ps</c> invocation on both macOS and Linux. Injecting the clock and the elapsed lookup keeps every
/// case deterministic, and no case needs a real process older or newer than the test.
/// </para>
/// <para>
/// Safety of this suite: every case starts only the guard itself, under <c>/bin/bash</c>. The process table and the
/// elapsed lookup are fixtures under <c>Fixtures/proc-guard/</c>, and the fake pids sit far above any real pid_max.
/// No case starts node, vitest, playwright, or the dashboard.
/// </para>
/// </remarks>
public sealed class ProcGuardSinceTests
{
    private const int LeftoversExitCode = 87;

    /// <summary>The injected clock, in epoch seconds. Any fixed value works; the since values are offsets from it.</summary>
    private const long Now = 1800000000L;

    /// <summary>Pid of the first orphan row in <c>ps-orphan.sh</c> and its etime fixtures.</summary>
    private const int OrphanPid = 2147483003;

    /// <summary>Pid of the second orphan row in <c>ps-orphan-two.sh</c>.</summary>
    private const int SecondOrphanPid = 2147483004;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A survivor that started one hour before <c>--since</c> must not count. The guard exits 0 and does not list it.
    /// </summary>
    /// <remarks>
    /// Without this, the watchdog would flag every leftover from an earlier run on the same machine, so a developer
    /// would see yesterday's processes blamed on today's test run.
    /// </remarks>
    [Fact]
    public void CheckLeftoversWithSince_SurvivorStartedBeforeSince_IsIgnoredAndExits0()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunCheckLeftovers(root.Path, Now - 60, "ps-orphan.sh", "etime-old.sh");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// A survivor that started five seconds ago, well after the cutoff of one minute ago, counts. The guard exits 87 and
    /// lists its pid.
    /// </summary>
    /// <remarks>
    /// This is the case that makes the leftover check useful for a CI job: the process was started by this job, so it
    /// is this job's leftover and must fail the job.
    /// </remarks>
    [Fact]
    public void CheckLeftoversWithSince_SurvivorStartedAfterSince_Exits87AndListsIt()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunCheckLeftovers(root.Path, Now - 60, "ps-orphan.sh", "etime-new.sh");
        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(OrphanPid.ToString(CultureInfo.InvariantCulture), combinedOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A survivor that started at exactly <c>--since</c> counts. The window is inclusive at its start.
    /// </summary>
    /// <remarks>
    /// The boundary is pinned because an off-by-one of one second is the most likely way to get this comparison wrong.
    /// A survivor started in the same second as the job began is part of that job.
    /// </remarks>
    [Fact]
    public void CheckLeftoversWithSince_SurvivorStartedExactlyAtSince_CountsAndExits87()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunCheckLeftovers(root.Path, Now - 60, "ps-orphan.sh", "etime-boundary.sh");

        Assert.Equal(LeftoversExitCode, result.ExitCode);
    }

    /// <summary>
    /// With one old and one new survivor under the same root, only the new one is listed, and the guard exits 87.
    /// </summary>
    /// <remarks>
    /// Mixed input is the realistic case: a machine where an earlier run left a process behind and this run left
    /// another. The report must name only the leftover that belongs to this run, or the developer would chase a
    /// process that was never theirs to clean up.
    /// </remarks>
    [Fact]
    public void CheckLeftoversWithSince_ListsOnlySurvivorsStartedAtOrAfterSince()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunCheckLeftovers(root.Path, Now - 60, "ps-orphan-two.sh", "etime-mixed.sh");
        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(SecondOrphanPid.ToString(CultureInfo.InvariantCulture), combinedOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(OrphanPid.ToString(CultureInfo.InvariantCulture), combinedOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A survivor whose elapsed time cannot be read still counts when <c>--since</c> is given, and the guard exits 87.
    /// </summary>
    /// <remarks>
    /// An unknown age must not hide a leftover. Run mode already treats an unknown age as new for the same reason, and
    /// this keeps the two modes consistent.
    /// </remarks>
    [Fact]
    public void CheckLeftoversWithSince_SurvivorWithUnknownElapsedTime_CountsAsNew()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunCheckLeftovers(root.Path, Now - 60, "ps-orphan.sh", "etime-none.sh");

        Assert.Equal(LeftoversExitCode, result.ExitCode);
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Process-guard script tests require /bin/bash on a POSIX environment.");
        }
    }

    private static string FixturePath(string fixtureName)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests", "Fixtures", "proc-guard",
            fixtureName);
    }

    /// <summary>
    /// Runs <c>scripts/proc-guard.sh check-leftovers --root &lt;root&gt; --since &lt;since&gt;</c> with a fixed clock, a fixed
    /// process table, and a fixed elapsed-time lookup.
    /// </summary>
    /// <remarks>
    /// The environment is set on the child's <see cref="ProcessStartInfo"/>, never on the test process, so parallel test
    /// classes cannot observe it.
    /// </remarks>
    private static ProcessResult RunCheckLeftovers(string root, long since, string tableFixture, string etimeFixture)
    {
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "proc-guard.sh"));
        startInfo.ArgumentList.Add("check-leftovers");
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("--since");
        startInfo.ArgumentList.Add(since.ToString(CultureInfo.InvariantCulture));

        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath(tableFixture);
        startInfo.Environment["KW_PROC_GUARD_ETIME"] = FixturePath(etimeFixture);
        startInfo.Environment["KW_PROC_GUARD_NOW"] = Now.ToString(CultureInfo.InvariantCulture);

        return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
    }
}
