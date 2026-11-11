using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for <c>scripts/report-gate.sh</c>, which runs a command whose result is a report file and
/// stops the command when it has written a complete report but does not exit.
/// </summary>
/// <remarks>
/// <para>
/// The script exists for the <c>inspectcode</c> review gate. On macOS 27.3 Beta the analyzer writes its whole
/// report and then never exits, so the gate ran to its time limit with a finished report on disk. The script
/// counts that run as done. Everything else must stay a failure: a command that hangs before its report is
/// complete, a report left over from an earlier run, and a command that exits zero without writing one.
/// </para>
/// <para>
/// Safety of this suite: every case starts only the script under <c>/bin/bash</c> with a <c>sh -c</c> stand-in
/// for the analyzer, inside a temporary directory, and every run is bounded by <see cref="ScriptTimeout"/>.
/// </para>
/// </remarks>
public sealed class ReportGateScriptTests : IDisposable
{
    private const string Report = "report.xml";
    private const string Closing = "</Report>";

    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the temporary directory.</summary>
    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void ACommandThatWritesItsReportAndExitsZeroPasses()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunScript($"echo '<Report>{Closing}' > {Report}");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void ACommandThatFailsKeepsItsOwnExitCode()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunScript($"echo '<Report>{Closing}' > {Report}; exit 7");

        Assert.Equal(7, result.ExitCode);
    }

    /// <summary>The case the script exists for: the report is complete and the command will not exit.</summary>
    [Fact]
    public void ACommandThatHangsAfterWritingACompleteReportIsStoppedAndPasses()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunScript($"echo $$ > pid; echo '<Report>{Closing}' > {Report}; sleep 300");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("did not exit", result.StandardError, StringComparison.Ordinal);
        Assert.False(IsRunning(ReadRecordedPid()), "The stopped command must not outlive the script.");
    }

    /// <summary>A hang with no report is a hang: the script keeps waiting, so the gate's own time limit reports it.</summary>
    [Fact]
    public void ACommandThatHangsWithoutAReportIsNeverPassed()
    {
        SkipIfBashUnavailable();

        Assert.Throws<TimeoutException>(() => RunScript("sleep 300", TimeSpan.FromSeconds(5)));
    }

    /// <summary>A report cut off before its closing text is not a finished analysis.</summary>
    [Fact]
    public void ACommandThatHangsAfterWritingAnIncompleteReportIsNeverPassed()
    {
        SkipIfBashUnavailable();

        Assert.Throws<TimeoutException>(
            () => RunScript($"echo '<Report><Issue />' > {Report}; sleep 300", TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The report path is fixed, so an earlier run's file is still there. It is removed before the command starts
    /// and can never stand in for this run's result.
    /// </summary>
    [Fact]
    public void AReportLeftByAnEarlierRunIsRemovedAndNeverCountsForThisRun()
    {
        SkipIfBashUnavailable();
        string report = Path.Combine(_root.Path, Report);
        File.WriteAllText(report, $"<Report>{Closing}");

        Assert.Throws<TimeoutException>(() => RunScript("sleep 300", TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(report), "The earlier run's report must be gone.");
    }

    /// <summary>The analyzer exits zero whatever it finds, so a zero exit with no report means it did not run.</summary>
    [Fact]
    public void ACommandThatExitsZeroWithoutACompleteReportFails()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunScript("true");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no complete report", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingReportOptionIsAUsageError()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunScriptArguments(["--", "true"], ScriptTimeout);

        Assert.Equal(2, result.ExitCode);
    }

    private ProcessResult RunScript(string standIn) => RunScript(standIn, ScriptTimeout);

    private ProcessResult RunScript(string standIn, TimeSpan timeout) =>
        RunScriptArguments(
            ["--report", Report, "--ends-with", Closing, "--grace", "1", "--", "sh", "-c", standIn], timeout);

    private ProcessResult RunScriptArguments(string[] arguments, TimeSpan timeout)
    {
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = _root.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "report-gate.sh"));
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return ProcessRunner.Run(startInfo, string.Empty, timeout);
    }

    private int ReadRecordedPid() =>
        int.Parse(File.ReadAllText(Path.Combine(_root.Path, "pid")).Trim(), CultureInfo.InvariantCulture);

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Report-gate script tests require /bin/bash on a POSIX environment.");
        }
    }
}
