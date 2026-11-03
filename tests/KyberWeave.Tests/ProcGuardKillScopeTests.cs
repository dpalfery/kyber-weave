using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for what <c>scripts/proc-guard.sh run</c> may signal after a command exits.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog must never kill outside its own tree. A re-parented process that merely names the
/// repository root is reported, but it is signalled only when it has a positive link to the run.
/// The caller's own orphaned shell, and a sibling directory that shares the root as a prefix, are
/// never matched.
/// </para>
/// <para>
/// Safety: every case runs <c>sh -c "exit 0"</c>, which exits at once. The process table is a fixture
/// (<c>KW_PROC_GUARD_PS</c>), and signals go to <c>kill-recorder.sh</c> (<c>KW_PROC_GUARD_KILL</c>) which
/// appends them to a log file instead of signalling anything, so no fake pid and no real pid is ever
/// signalled. No case starts a real build server, node, or any long-lived child.
/// </para>
/// </remarks>
public sealed class ProcGuardKillScopeTests
{
    private const int LeftoversExitCode = 87;
    private const int UnlinkedOrphanPid = 2147483031;
    private const int SiblingPid = 2147483032;
    private const int LinkedOrphanPid = 2147483033;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An orphan with the repo path in its command line but no link to the run is reported with exit 87 and not
    /// signalled, and the guard's own parent is neither reported nor signalled.
    /// </summary>
    [Fact]
    public void RunWhenOrphanNamesRootButIsUnlinked_ReportsItWithoutSignallingAndSparesTheCaller()
    {
        SkipIfBashUnavailable();
        using TempDirectory logDirectory = new();

        (ProcessResult result, string killLog) = RunGuard("ps-kill-scope-unlinked.sh", null, logDirectory.Path);

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(UnlinkedOrphanPid.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("zsh", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, killLog);
    }

    /// <summary>
    /// A sibling directory that shares the root as a bare prefix (<c>root-2</c>) is not a match.
    /// </summary>
    [Fact]
    public void RunWhenOrphanNamesSiblingPathSharingRootPrefix_DoesNotMatch()
    {
        SkipIfBashUnavailable();
        using TempDirectory logDirectory = new();

        (ProcessResult result, string killLog) = RunGuard("ps-kill-scope-sibling.sh", null, logDirectory.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(SiblingPid.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, killLog);
    }

    /// <summary>
    /// An orphan that started as the run began is reported with exit 87 and is signalled.
    /// </summary>
    [Fact]
    public void RunWhenOrphanStartedWithTheRun_ReportsItExits87AndSignalsIt()
    {
        SkipIfBashUnavailable();
        using TempDirectory logDirectory = new();

        (ProcessResult result, string killLog) = RunGuard("ps-kill-scope-linked.sh", "etime-kill-scope-linked.sh", logDirectory.Path);

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(LinkedOrphanPid.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
        Assert.Contains("TERM " + LinkedOrphanPid.ToString(CultureInfo.InvariantCulture), killLog, StringComparison.Ordinal);
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
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests", "Fixtures", "proc-guard", fixtureName);
    }

    /// <summary>
    /// Runs the guard around a command that exits 0 at once, with a fixture process table and a recording kill.
    /// </summary>
    private static (ProcessResult Result, string KillLog) RunGuard(string psFixtureName, string? etimeFixtureName, string logDirectory)
    {
        string killLogPath = Path.Combine(logDirectory, "kills.log");
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
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");
        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath(psFixtureName);
        startInfo.Environment["KW_PROC_GUARD_KILL"] = FixturePath("kill-recorder.sh");
        startInfo.Environment["KW_PROC_GUARD_KILL_LOG"] = killLogPath;
        if (etimeFixtureName is not null)
        {
            startInfo.Environment["KW_PROC_GUARD_ETIME"] = FixturePath(etimeFixtureName);
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
        string killLog = File.Exists(killLogPath) ? File.ReadAllText(killLogPath) : string.Empty;
        return (result, killLog);
    }
}
