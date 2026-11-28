using KyberWeave.Testing.Squad;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Task 11.3 on the real disk: <c>status</c> and <c>doctor</c> report an owned-block file the process cannot read as
/// drift instead of crashing. The file mode is a real operating-system fact, so the case cannot run in memory; the other
/// owned-block status and doctor cases are in <c>KyberWeave.Cli.Tests</c>.
/// </summary>
public sealed class SquadOwnedBlockUnreadableFileTests
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Status_ReportsAnUnreadableBlockFileAsDriftInsteadOfCrashing()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows, so the unreadable file cannot be simulated; a privileged process (root) ignores the mode too, so it is skipped there as well.");

        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        RealSquadDeployment.Transaction(store).Execute(plan);
        string path = Path.Combine(fixture.Path, ".cursor", "hooks.json");
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            SquadStatusCommand command = SquadTestCommands.Status(stateStore: store);
            CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
                null!,
                new SquadStatusSettings
                {
                    Path = fixture.Path,
                    Global = false,
                }));

            Assert.Equal(1, execution.Result);
            Assert.Contains("unreadable", execution.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Doctor_ReportsAnUnreadableBlockFileAsDriftInsteadOfCrashing()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows, so the unreadable file cannot be simulated; a privileged process (root) ignores the mode too, so it is skipped there as well.");

        using TempDirectory fixture = new TempDirectory();
        FakeSquadUserPaths userPaths = new(Path.Combine(fixture.Path, "user-home"));
        SquadStateStore store = RealSquadState.Store(userPaths);
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        RealSquadDeployment.Transaction(store).Execute(plan);
        string path = Path.Combine(fixture.Path, ".cursor", "hooks.json");
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            FakeProcessExecutor executor = new FakeProcessExecutor()
                .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
            SquadDoctorCommand command = SquadTestCommands.Doctor(executor, userPaths, workingDirectory: fixture.Path);

            CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(() => command.ExecuteAsync(
                null!,
                new SquadDoctorSettings
                {
                    Path = fixture.Path,
                    Global = false,
                }, CancellationToken.None));

            Assert.Equal(1, execution.Result);
            Assert.Contains("unreadable", execution.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static SquadRenderedBlock CursorBlock(string marker) =>
        new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [new SquadRenderedBlockEntry("preToolUse", CursorEntry($"kyber-weave-arbiter hook --harness cursor --probe {marker}"))]);

    private static JsonObject CursorEntry(string command) =>
        new()
        {
            ["command"] = command,
            ["matcher"] = "Task",
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    private static SquadStateStore Store(string applicationData) =>
        RealSquadState.Store(new FakeSquadUserPaths(applicationData));

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["cursor"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
