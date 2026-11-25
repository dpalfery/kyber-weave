using KyberDash.Application;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>Proves the pure tray-process decisions: counting, the running verdict and the wait before a kill.</summary>
public class TrayProcessesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void CountIsZeroForEmptyMacosOutput(string output)
    {
        Assert.Equal(0, TrayProcesses.CountTrayProcesses(output, windows: false));
    }

    [Theory]
    [InlineData("pgrep: warning: something odd\n4242\n", 1)]
    [InlineData("some tool warning\n", 0)]
    [InlineData("12abc\n", 0)]
    [InlineData("4242 kyberdash-tray\n", 1)]
    public void CountOnMacosRequiresALeadingPidSoToolWarningsDoNotCount(string output, int expected)
    {
        Assert.Equal(expected, TrayProcesses.CountTrayProcesses(output, windows: false));
    }

    [Fact]
    public void CountIsOneForOneMacosPgrepLine()
    {
        Assert.Equal(1, TrayProcesses.CountTrayProcesses("4242\n", windows: false));
    }

    [Fact]
    public void CountIsThreeForThreeMacosPgrepLines()
    {
        Assert.Equal(3, TrayProcesses.CountTrayProcesses("101\n202\r\n303\n", windows: false));
    }

    [Fact]
    public void CountOnWindowsCountsOnlyTheTrayCsvLinesIgnoringCase()
    {
        const string Output =
            "\"kyberdash-tray.exe\",\"1234\",\"Console\",\"1\",\"12,000 K\"\r\n" +
            "\"KYBERDASH-TRAY.EXE\",\"99\",\"Console\",\"1\",\"1 K\"\r\n" +
            "\"notepad.exe\",\"5\",\"Console\",\"1\",\"8 K\"\r\n";

        Assert.Equal(2, TrayProcesses.CountTrayProcesses(Output, windows: true));
    }

    [Fact]
    public void CountOnWindowsIsZeroForTheNoTasksLine()
    {
        Assert.Equal(0, TrayProcesses.CountTrayProcesses("INFO: No tasks are running which match the specified criteria.\r\n", windows: true));
    }

    [Fact]
    public void RunningOutcomeSucceedsOnlyForExactlyOneTray()
    {
        Assert.Equal(0, TrayProcesses.RunningOutcome(1).ExitCode);
    }

    [Fact]
    public void RunningOutcomeFailsWhenNoTrayRuns()
    {
        Assert.Equal(1, TrayProcesses.RunningOutcome(0).ExitCode);
        Assert.Equal("kyberdash: no tray process running", TrayProcesses.RunningOutcome(0).Message);
    }

    [Fact]
    public void RunningOutcomeFailsAndCountsWhenSeveralTraysRun()
    {
        Assert.Equal(1, TrayProcesses.RunningOutcome(2).ExitCode);
        Assert.Equal("kyberdash: 2 tray processes running", TrayProcesses.RunningOutcome(2).Message);
    }

    [Fact]
    public async Task WaitReturnsWithoutDelayingWhenTheFirstPollFindsNoTray()
    {
        List<TimeSpan> delays = [];

        bool exited = await TrayProcesses.WaitForTrayExitAsync(
            _ => Task.FromResult(false),
            (span, _) => { delays.Add(span); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(exited);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task WaitPollsAgainAfterTheIntervalUntilTheTrayIsGone()
    {
        Queue<bool> polls = new([true, true, false]);
        List<TimeSpan> delays = [];
        TimeSpan interval = TimeSpan.FromMilliseconds(150);

        bool exited = await TrayProcesses.WaitForTrayExitAsync(
            _ => Task.FromResult(polls.Dequeue()),
            (span, _) => { delays.Add(span); return Task.CompletedTask; },
            interval,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(exited);
        Assert.Equal(new[] { interval, interval }, delays);
    }

    [Fact]
    public async Task WaitReturnsFalseWhenEveryPollUntilTheTimeoutFindsATray()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        TimeSpan interval = TimeSpan.FromMilliseconds(150);
        TimeSpan timeout = TimeSpan.FromSeconds(1);

        bool exited = await TrayProcesses.WaitForTrayExitAsync(
            _ => Task.FromResult(true),
            (span, _) => { elapsed += span; return Task.CompletedTask; },
            interval,
            timeout,
            TestContext.Current.CancellationToken);

        Assert.False(exited);
        Assert.True(elapsed >= timeout);
    }
}
