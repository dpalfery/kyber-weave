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

namespace KyberWeave.Tests;

/// <summary>
/// Task 11.3: <c>squad status</c> lists owned blocks with file and entry count,
/// and <c>status</c> plus <c>doctor</c> report hand-edited or missing owned
/// entries as drift naming the file and the container (Req 8.4).
/// </summary>
public sealed class SquadOwnedBlockStatusTests
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Status_ListsOwnedBlocksWithFileAndEntryCount()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(plan);

        SquadStatusCommand command = new(stateStore: store);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = false,
            }));

        Assert.Equal(0, execution.Result);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("cursor", execution.Output, StringComparison.Ordinal);
        Assert.Contains("1", execution.Output, StringComparison.Ordinal);
        Assert.Contains("entr", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_ReportsHandEditedOwnedEntryAsDriftByFileAndContainer()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(plan);

        DriftOwnedEntry(fixture.Path, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        SquadStatusCommand command = new(stateStore: store);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = false,
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("/hooks/preToolUse/0", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ReportsMissingBlockFileAsDriftByFileAndContainer()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(plan);

        File.Delete(Path.Combine(fixture.Path, ".cursor", "hooks.json"));

        SquadStatusCommand command = new(stateStore: store);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = fixture.Path,
                Global = false,
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("/hooks/", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ReportsHandEditedOwnedEntryAsDriftByFileAndContainer()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(plan);

        DriftOwnedEntry(fixture.Path, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(fixture.Path, "user-home"));
        SquadDoctorCommand command = new(executor, userPaths, workingDirectory: fixture.Path);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = fixture.Path,
                Global = false,
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("/hooks/preToolUse/0", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ReportsMissingBlockFileAsDriftByFileAndContainer()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(plan);

        File.Delete(Path.Combine(fixture.Path, ".cursor", "hooks.json"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(fixture.Path, "user-home"));
        SquadDoctorCommand command = new(executor, userPaths, workingDirectory: fixture.Path);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = fixture.Path,
                Global = false,
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("/hooks/", execution.Output, StringComparison.Ordinal);
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

    private static void DriftOwnedEntry(string root, string relativePath, string marker, string replacement)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        JsonNode rootNode = JsonNode.Parse(File.ReadAllText(path))!;
        JsonArray pre = Assert.IsType<JsonObject>(rootNode)["hooks"]!.AsObject()["preToolUse"]!.AsArray();
        JsonObject owned = Assert.IsType<JsonObject>(pre.Single(
            node => node!.AsObject()["command"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal)));
        owned["command"] = owned["command"]!.GetValue<string>().Replace(
            marker,
            replacement,
            StringComparison.Ordinal);
        File.WriteAllText(path, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static SquadStateStore Store(string applicationData) =>
        new(new FakeSquadUserPaths(applicationData));

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
