using KyberWeave.Testing.Squad;
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
    private readonly InMemorySquadState _state = new();

    /// <summary>
    /// The bare global hook path four of the five formats write beneath their own root; only the
    /// target tells them apart, which is the whole subject of the global cases below.
    /// </summary>
    private const string GlobalHooksPath = "hooks.json";

    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Status_ListsOwnedBlocksWithFileAndEntryCount()
    {
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(plan);

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(plan);

        DriftOwnedEntry(root, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(plan);

        _state.Files.DeleteFile(Path.Combine(root, ".cursor", "hooks.json"));

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(plan);

        DriftOwnedEntry(root, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: root);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(plan);

        _state.Files.DeleteFile(Path.Combine(root, ".cursor", "hooks.json"));

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(_state, executor, workingDirectory: root);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = root,
                Global = false,
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("drift", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cursor/hooks.json", execution.Output, StringComparison.Ordinal);
        Assert.Contains("/hooks/", execution.Output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Global scope: each target's block sits beneath that target's own root
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// A <c>--global</c> status names every block with its own target, because four of the five
    /// formats write the same bare <c>hooks.json</c> beneath their own global root: the target is
    /// the only thing that tells the two lines apart, and a block whose recorded path is that bare
    /// name must be reported as healthy rather than called an unknown shared hook file.
    /// </summary>
    [Fact]
    public void Status_GlobalReportsEveryBlockWithItsTargetLabel()
    {
        GlobalBlockDeployment installed = InstallGlobalBlocks();

        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(_state, installed.GlobalRoots);
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = installed.Home,
                Global = true,
            }));

        Assert.True(
            execution.Result == 0,
            $"A global home with two healthy blocks reported exit {execution.Result}:{Environment.NewLine}"
            + execution.Output);
        Assert.Contains("hooks.json (cursor) 2 entries", execution.Output, StringComparison.Ordinal);
        Assert.Contains("hooks.json (codex) 2 entries", execution.Output, StringComparison.Ordinal);
        Assert.Contains(
            "All deployed files match the recorded receipt.",
            execution.Output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>--global</c> doctor finds a two-block home healthy. Reading the receipt is the whole
    /// check: both blocks resolve through their own target's root, so a green result has to mean
    /// both were inspected, not that the first one hid the second.
    /// </summary>
    [Fact]
    public void Doctor_GlobalFindsEveryBlockHealthy()
    {
        GlobalBlockDeployment installed = InstallGlobalBlocks();

        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            installed.Home,
            installed.GlobalRoots);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings
            {
                Path = installed.Home,
                Global = true,
            }));

        Assert.True(
            execution.Result == 0,
            $"A global home with two healthy blocks reported exit {execution.Result}:{Environment.NewLine}"
            + execution.Output);
        Assert.Contains("Owned blocks: 2 blocks, 4 entries healthy", execution.Output, StringComparison.Ordinal);
    }

    private static SquadRenderedBlock CursorBlock(string marker) =>
        new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [new SquadRenderedBlockEntry("preToolUse", CursorEntry($"kyber-weave-arbiter hook --harness cursor --probe {marker}"))]);

    /// <summary>
    /// One global block. The marker rides the matcher, never the command: the command is the
    /// ownership signature, so a marker inside it would make the entry unowned.
    /// </summary>
    private static SquadRenderedBlock GlobalHooksBlock(SquadTarget target)
    {
        string token = SquadTargetCatalog.GetToken(target);
        string command = $"kyber-weave-arbiter hook --harness {token} --scope global";
        return target switch
        {
            SquadTarget.Cursor => new SquadRenderedBlock(
                token,
                GlobalHooksPath,
                SquadHookBlockFormat.Cursor,
                [
                    new SquadRenderedBlockEntry("preToolUse", CursorEntry(command)),
                    new SquadRenderedBlockEntry("postToolUse", CursorEntry(command)),
                ]),
            SquadTarget.Codex => new SquadRenderedBlock(
                token,
                GlobalHooksPath,
                SquadHookBlockFormat.Codex,
                [
                    new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(command)),
                    new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(command)),
                ]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Only the targets whose global hook file is named hooks.json are built here."),
        };
    }

    private static JsonObject MatcherGroup(string command) =>
        new()
        {
            ["matcher"] = "Task",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }),
        };

    /// <summary>
    /// A home holding one global receipt with a cursor and a codex block, installed through the
    /// same plan and transaction an install uses. Each block is written beneath its own target's
    /// global root, which is why both record the bare path <c>hooks.json</c>.
    /// </summary>
    private GlobalBlockDeployment InstallGlobalBlocks()
    {
        string home = _state.UserPaths.HomeDirectory;
        _state.Files.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [GlobalHooksBlock(SquadTarget.Cursor), GlobalHooksBlock(SquadTarget.Codex)]);
        _state.Transaction().Execute(plan);
        return new GlobalBlockDeployment(store, globalRoots, home);
    }

    private static JsonObject CursorEntry(string command) =>
        new()
        {
            ["command"] = command,
            ["matcher"] = "Task",
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    private void DriftOwnedEntry(string root, string relativePath, string marker, string replacement)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        JsonNode rootNode = JsonNode.Parse(_state.Files.ReadAllText(path))!;
        JsonArray pre = Assert.IsType<JsonObject>(rootNode)["hooks"]!.AsObject()["preToolUse"]!.AsArray();
        JsonObject owned = Assert.IsType<JsonObject>(pre.Single(
            node => node!.AsObject()["command"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal)));
        owned["command"] = owned["command"]!.GetValue<string>().Replace(
            marker,
            replacement,
            StringComparison.Ordinal);
        _state.Files.WriteAllText(path, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private string ProjectRoot()
    {
        string root = Path.Combine(_state.Files.Root, "work", "project");
        _state.Files.CreateDirectory(root);
        return root;
    }

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

    /// <summary>
    /// An isolated home with a global deployment in it: the store that reads its receipt, the
    /// resolver that finds each target's root, and the home these global commands are anchored at.
    /// </summary>
    private sealed record GlobalBlockDeployment(
        SquadStateStore Store,
        SquadGlobalRoots GlobalRoots,
        string Home);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
