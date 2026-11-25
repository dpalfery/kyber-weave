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
            call => Assert.Equal(["-f", binary], call.Args));
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
        Assert.True(Called("/usr/bin/pkill", "-f", binary));
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
        (_, _) => Task.CompletedTask);

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
            trayRunning = trayRunning && !killStopsTray;
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        if (fileName == "/usr/bin/pgrep" || fileName == "tasklist")
        {
            string output = listingOutput ?? (trayRunning ? "4242\n" : string.Empty);
            if (fileName == "tasklist" && trayRunning && listingOutput is null)
            {
                output = "\"kyberdash-tray.exe\",\"1\",\"Console\",\"1\",\"1 K\"\r\n";
            }

            return Task.FromResult(new ProcessResult(output.Length > 0 ? 0 : 1, output, string.Empty));
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

    private bool Called(string file, params string[] args) =>
        calls.Any(call => call.File == file && call.Args.SequenceEqual(args));
}
