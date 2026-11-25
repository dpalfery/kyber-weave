using KyberDash.Application;
using KyberDash.Core.Processes;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Proves the quit, launch and running decisions of <see cref="MenubarService"/> in memory: which binary is
/// signalled, when nothing is, and when a force-kill is allowed.
/// </summary>
public class MenubarServiceTrayTests : IDisposable
{
    private readonly TempDirectory state = new();
    private readonly List<(string File, string[] Args)> calls = [];
    private bool trayRunning;
    private bool killStopsTray = true;
    private bool quitThrowsTimeout;
    private Exception? quitThrows;
    private int? probeExitCode;
    private int killTakesPolls;
    private Action<string> startDetached = _ => { };
    private readonly List<string> started = [];
    private int pendingPolls;
    private bool quitStopsTray = true;

    /// <summary>Deletes the scratch state directory.</summary>
    public void Dispose()
    {
        state.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task QuitRefusesAPlatformTheTrayDoesNotShipFor()
    {
        MenubarService service = Create(TrayPlatform.Other);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("platform", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task QuitWithNoRecordedInstallExitsOneAndRunsNothing()
    {
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("no tray install recorded", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task QuitWithAMissingBundleExitsZeroAndRunsNothing()
    {
        RecordInstall(TrayPlatform.MacOS, createBinary: false);
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("not running", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task QuitWhenTheRecordedTrayIsNotRunningExitsZeroWithoutSignallingIt()
    {
        string binary = RecordInstall(TrayPlatform.MacOS);
        trayRunning = false;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain(calls, call => call.File == binary);
        Assert.DoesNotContain(calls, call => call.File == "/usr/bin/pkill");
    }

    [Fact]
    public async Task QuitAsksOnlyTheRecordedBinaryToQuitAndNeverKillsATrayThatExited()
    {
        string binary = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(Called(binary, "--quit"));
        Assert.DoesNotContain(calls, call => call.File == "/usr/bin/pkill");
        // Every lookup names the recorded binary, never the generic tray pattern a developer's own tray also matches.
        Assert.All(
            calls.Where(call => call.File == "/usr/bin/pgrep"),
            call => Assert.Equal(["-f", AnchoredPattern(binary)], call.Args));
    }

    [Fact]
    public async Task QuitForceKillsOnlyTheRecordedBinaryOnMacosWhenItIgnoresTheQuit()
    {
        string binary = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitThrowsTimeout = true;
        quitStopsTray = false;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(Called("/usr/bin/pkill", "-f", AnchoredPattern(binary)));
    }

    [Fact]
    public async Task QuitReportsAFailureOutcomeWhenTheQuitToolCannotBeStarted()
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitThrows = new System.ComponentModel.Win32Exception("exec format error");
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.StartsWith("kyberdash menubar: quit:", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, call => call.File == "/usr/bin/pkill");
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task QuitReportsAFailureOutcomeForAnIoOrAccessFailure(Type exceptionType)
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitThrows = (Exception)Activator.CreateInstance(exceptionType, "denied")!;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("denied", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuitSignalsWithAPatternAnchoredToTheStartOfTheCommandLine()
    {
        string binary = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitStopsTray = false;
        MenubarService service = Create(TrayPlatform.MacOS);

        _ = await service.QuitAsync();

        string pattern = calls.Single(call => call.File == "/usr/bin/pkill").Args[1];
        System.Text.RegularExpressions.Regex matcher = new(pattern);
        Assert.Matches(matcher, binary);
        Assert.Matches(matcher, binary + " --flag");
        Assert.DoesNotMatch(matcher, "/usr/bin/vim " + binary);
        Assert.DoesNotMatch(matcher, binary + "-other");
        Assert.DoesNotMatch(matcher, binary.Replace('.', 'x'));
    }

    [Fact]
    public async Task QuitGivesAForceKilledTrayAFewPollsToDisappearBeforeReportingFailure()
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitStopsTray = false;
        killTakesPolls = 3;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task QuitStillReportsFailureWhenTheKilledTrayNeverDisappears()
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitStopsTray = false;
        killTakesPolls = 500;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
    }

    [Fact]
    public async Task QuitReportsAFailureWhenTheTrayOutlivesTheForceKill()
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        quitStopsTray = false;
        killStopsTray = false;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("after quit and kill", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuitNeverForceKillsOnWindows()
    {
        _ = RecordInstall(TrayPlatform.Windows);
        trayRunning = true;
        quitStopsTray = false;
        MenubarService service = Create(TrayPlatform.Windows);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("still running after quit", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, call => call.File == "/usr/bin/pkill");
    }

    [Fact]
    public async Task LaunchWithNoRecordedInstallExitsOneAndStartsNothing()
    {
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.LaunchAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task LaunchOpensTheRecordedBundleOnMacos()
    {
        _ = RecordInstall(TrayPlatform.MacOS);
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.LaunchAsync();

        Assert.Equal(0, outcome.ExitCode);
        (string File, string[] Args) open = Assert.Single(calls);
        Assert.Equal("/usr/bin/open", open.File);
        Assert.Equal([Path.Combine(state.Path, "KyberDash.app")], open.Args);
    }

    [Fact]
    public async Task LaunchStartsTheRecordedExecutableOnWindows()
    {
        string binary = RecordInstall(TrayPlatform.Windows);
        MenubarService service = Create(TrayPlatform.Windows);

        MenubarInstallOutcome outcome = await service.LaunchAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal([binary], started);
    }

    [Theory]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task LaunchReportsAFailureOutcomeWhenWindowsCannotStartTheTray(Type exceptionType)
    {
        _ = RecordInstall(TrayPlatform.Windows);
        startDetached = _ => throw (Exception)Activator.CreateInstance(exceptionType, "blocked")!;
        MenubarService service = Create(TrayPlatform.Windows);

        MenubarInstallOutcome outcome = await service.LaunchAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("kyberdash menubar: launch: blocked", outcome.Message);
    }

    [Theory]
    [InlineData("4242\n", 0)]
    [InlineData("", 1)]
    public async Task CountRunningIsZeroForOneTrayAndOneForNone(string listing, int exitCode)
    {
        listingOutput = listing;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.CountRunningAsync();

        Assert.Equal(exitCode, outcome.ExitCode);
    }

    [Fact]
    public async Task CountRunningReportsAFailureWhenPgrepFailsInsteadOfCountingItsOutput()
    {
        listingOutput = "4242\n";
        probeExitCode = 2;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.CountRunningAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("probe", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is running", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountRunningTreatsPgrepExitOneAsNoTrayEvenWithStrayOutput()
    {
        listingOutput = "4242\n";
        probeExitCode = 1;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.CountRunningAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("kyberdash: no tray process running", outcome.Message);
    }

    [Fact]
    public async Task QuitReportsAFailureWhenTheRunningProbeFailsAndSignalsNothing()
    {
        string binary = RecordInstall(TrayPlatform.MacOS);
        trayRunning = true;
        probeExitCode = 2;
        MenubarService service = Create(TrayPlatform.MacOS);

        MenubarInstallOutcome outcome = await service.QuitAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("probe", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(calls, call => call.File == binary);
        Assert.DoesNotContain(calls, call => call.File == "/usr/bin/pkill");
    }

    [Fact]
    public async Task CountRunningRefusesAPlatformTheTrayDoesNotShipFor()
    {
        MenubarService service = Create(TrayPlatform.Other);

        MenubarInstallOutcome outcome = await service.CountRunningAsync();

        Assert.Equal(1, outcome.ExitCode);
        Assert.Empty(calls);
    }

    private string? listingOutput;

    private MenubarService Create(TrayPlatform platform) => new(
        new FakeServerStateRepository(state.Path),
        RunToolAsync,
        platform,
        (_, _) => Task.CompletedTask,
        path =>
        {
            started.Add(path);
            startDetached(path);
        });

    /// <summary>Writes the tray record and, unless told not to, the binary it names; returns the binary's path.</summary>
    private string RecordInstall(TrayPlatform platform, bool createBinary = true)
    {
        bool mac = platform == TrayPlatform.MacOS;
        string recorded = mac ? Path.Combine(state.Path, "KyberDash.app") : Path.Combine(state.Path, "kyberdash-tray.exe");
        string binary = mac ? Path.Combine(recorded, "Contents", "MacOS", "kyberdash-tray") : recorded;
        if (createBinary)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllText(binary, string.Empty);
        }

        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            path = recorded,
            version = "1.0.0",
            installedAt = "2026-01-01T00:00:00.000Z",
            platform = mac ? "darwin" : "win32",
        });
        File.WriteAllText(Path.Combine(state.Path, MenubarService.TrayRecordFile), json);
        return binary;
    }

    private Task<ProcessResult> RunToolAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        calls.Add((fileName, arguments));
        if (fileName == "/usr/bin/pkill")
        {
            if (killStopsTray && killTakesPolls > 0)
            {
                pendingPolls = killTakesPolls;
            }
            else
            {
                trayRunning = trayRunning && !killStopsTray;
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        if (fileName == "/usr/bin/pgrep" || fileName == "tasklist")
        {
            string output = listingOutput ?? (trayRunning ? "4242\n" : string.Empty);
            if (pendingPolls > 0 && --pendingPolls == 0)
            {
                trayRunning = false;
            }

            if (fileName == "tasklist" && trayRunning && listingOutput is null)
            {
                output = "\"kyberdash-tray.exe\",\"1\",\"Console\",\"1\",\"1 K\"\r\n";
            }

            return Task.FromResult(new ProcessResult(probeExitCode ?? (output.Length > 0 ? 0 : 1), output, probeExitCode is null ? string.Empty : "probe broke"));
        }

        // Anything else is the tray binary asked to quit.
        if (quitThrows is not null)
        {
            throw quitThrows;
        }

        if (quitThrowsTimeout)
        {
            throw new TimeoutException("the tray did not answer");
        }

        trayRunning = trayRunning && !quitStopsTray;
        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    private static string AnchoredPattern(string binary) =>
        "^" + System.Text.RegularExpressions.Regex.Replace(binary, @"[\\.\[\]{}()*+?^$|]", "\\$0") + "( |$)";

    private bool Called(string file, params string[] args) =>
        calls.Any(call => call.File == file && call.Args.SequenceEqual(args));
}
