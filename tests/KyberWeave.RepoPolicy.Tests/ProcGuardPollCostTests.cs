using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Cost contract for the poll loop of <c>scripts/proc-guard.sh</c>: one process-table read per poll,
/// and a short command is noticed well inside one poll interval.
/// </summary>
/// <remarks>
/// The process table is a counting fixture and the only real child is a two-second <c>sleep</c>
/// or <c>true</c>, so nothing here can trip or kill another process.
/// </remarks>
public sealed class ProcGuardPollCostTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A two-second command is polled about five times. At one read per poll plus the start-up check and
    /// the leftover scan that is at most seven table reads; the old two reads per poll made nine or more.
    /// </summary>
    [Fact]
    public void RunWhenCommandRunsTwoSeconds_ReadsTheProcessTableOncePerPoll()
    {
        SkipIfBashUnavailable();
        using TempDirectory directory = new();
        string logPath = Path.Combine(directory.Path, "reads.log");

        ProcessResult result = RunGuard(["run", "--", "sleep", "2"], logPath);

        Assert.True(result.ExitCode == 0, $"guard exited {result.ExitCode}: {result.StandardError}");
        int reads = CountReads(logPath, result);
        Assert.True(reads <= 7, $"expected at most 7 process-table reads, saw {reads}");
    }

    /// <summary>
    /// A command that exits at once returns in well under one poll interval plus the table reads.
    /// </summary>
    [Fact]
    public void RunWhenCommandExitsAtOnce_DoesNotWaitAFullPollInterval()
    {
        SkipIfBashUnavailable();
        using TempDirectory directory = new();
        string logPath = Path.Combine(directory.Path, "reads.log");

        Stopwatch watch = Stopwatch.StartNew();
        ProcessResult result = RunGuard(["run", "--", "sh", "-c", "exit 0"], logPath);
        watch.Stop();

        Assert.True(result.ExitCode == 0, $"guard exited {result.ExitCode}: {result.StandardError}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"guard took {watch.Elapsed}");
        Assert.True(CountReads(logPath, result) <= 4, "an immediate exit needs at most four table reads");
    }

    private static int CountReads(string logPath, ProcessResult result)
    {
        Assert.True(File.Exists(logPath), $"the guard never read the process table fixture: {result.StandardError}");
        return File.ReadAllLines(logPath).Length;
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Process-guard script tests require /bin/bash on a POSIX environment.");
        }
    }

    private static ProcessResult RunGuard(string[] guardArguments, string logPath)
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
        foreach (string argument in guardArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["KW_PROC_GUARD_PS"] = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.RepoPolicy.Tests", "Fixtures", "proc-guard", "ps-counting.sh");
        startInfo.Environment["KW_PROC_GUARD_PS_LOG"] = logPath;
        return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
    }
}
