using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad status</c> reads and prints, over the in-memory file system, the shared console and the state fakes.</summary>
public sealed class SquadStatusCommandTests : IDisposable
{
    private const string Project = "/kw/work";
    private const string Home = "/kw/home";
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeSquadStateStore _state = new(new FakeSquadUserPaths("/kw/appdata", Home));
    private readonly FakeSquadStateMigration _migration = new();
    private readonly ScriptedPresets _presets = new();

    public SquadStatusCommandTests()
    {
        _ansi.Profile.Width = 200;
        _files.CreateDirectory(Project);
    }

    public void Dispose()
    {
        _ansi.Dispose();
    }

    [Fact]
    public void Execute_NoReceipt_SaysNoDeploymentAndExitsOne()
    {
        int exit = Run(new SquadStatusSettings());

        Assert.Equal(1, exit);
        Assert.Equal(
            "No Kyber-Squad deployment found at /kw/work.\nRun kyber-weave squad install to deploy agents and skills.\n",
            Output());
        Assert.Equal([Project], _migration.InvokingDirectories);
    }

    [Fact]
    public void Execute_ConflictingPaths_IsRefusedAsClientInputBeforeAnythingIsRead()
    {
        int exit = Run(new SquadStatusSettings { Path = "one", PathOption = "two" });

        Assert.Equal(2, exit);
        Assert.StartsWith("kyber-weave squad: error: ", Output(), StringComparison.Ordinal);
        Assert.Empty(_migration.InvokingDirectories);
    }

    [Fact]
    public void Execute_EveryFileMatches_ReportsOkAndExitsZero()
    {
        string bytes = "You are architect.\n";
        _files.CreateDirectory("/kw/work/.claude/agents");
        _files.WriteAllText("/kw/work/.claude/agents/a.md", bytes);
        _state.Add(ProjectReceipt(OwnedFile(".claude/agents/a.md", bytes)));

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(0, exit);
        Assert.Equal(
            "Kyber-Squad deployment at /kw/work (project):\n"
            + "  ok      .claude/agents/a.md\n"
            + "\n"
            + "Model presets: none\n"
            + "\n"
            + "All deployed files match the recorded receipt.\n",
            Output());
    }

    [Fact]
    public void Execute_FileIsAbsent_ReportsMissingAndExitsOne()
    {
        _state.Add(ProjectReceipt(OwnedFile(".claude/agents/a.md", "x")));

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(1, exit);
        string output = Output();
        Assert.Contains("  missing .claude/agents/a.md\n", output, StringComparison.Ordinal);
        Assert.EndsWith("Drift or missing files detected in Kyber-Squad deployment.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_FileDiffersFromTheReceipt_ReportsItModifiedLocally()
    {
        _files.CreateDirectory("/kw/work/.claude/agents");
        _files.WriteAllText("/kw/work/.claude/agents/a.md", "edited");
        _state.Add(ProjectReceipt(OwnedFile(".claude/agents/a.md", "shipped")));

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(1, exit);
        string output = Output();
        Assert.Contains(
            "  modified locally .claude/agents/a.md (kept at update; run squad update --replace-managed to take the shipped file)\n",
            output,
            StringComparison.Ordinal);
        Assert.EndsWith("Drift detected in Kyber-Squad deployment.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_OwnedBlockWhoseFileIsAbsent_ReportsEachEntryAsMissing()
    {
        SquadOwnedBlock block = new("settings.json", "claude", false, [new SquadOwnedBlockEntry("/hooks/a/0", Hash)]);
        _state.Add(ProjectReceipt() with { Blocks = [block] });

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(1, exit);
        Assert.Contains("  drift   settings.json /hooks/a/0 (missing)\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AppliedPresetChangedSinceItWasApplied_IsReported()
    {
        _state.Add(ProjectReceipt());
        _state.AddLock(
            Project,
            SquadDeploymentScope.Project,
            Lock(new Dictionary<SquadTarget, SquadAppliedPreset> { [SquadTarget.Claude] = new("zai", "old") }));
        _presets.Add(new SquadModelPreset("zai", "/kw/home/zai.yml", "new", new Dictionary<string, SquadModelProfile>()));

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(0, exit);
        Assert.Contains("Model presets:\n  claude: zai (changed since applied)\n", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AppliedPresetNoLongerExists_IsReportedMissingAndExitsOne()
    {
        _state.Add(ProjectReceipt());
        _state.AddLock(
            Project,
            SquadDeploymentScope.Project,
            Lock(new Dictionary<SquadTarget, SquadAppliedPreset> { [SquadTarget.Claude] = new("zai", "old") }));

        int exit = Run(new SquadStatusSettings());

        Assert.Equal(1, exit);
        string output = Output();
        Assert.Contains("  claude: zai (missing)\n", output, StringComparison.Ordinal);
        Assert.EndsWith("Missing model preset detected in Kyber-Squad deployment.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_GlobalWithOnlyInstances_ListsEachInstanceBeneathTheHomeAndTheRegistry()
    {
        string stateBase = Home + "/.kyber-weave/squad/instance-state";
        SeedInstance(stateBase + "/claude/default", lockText: "lock-a", version: "1.2.3");
        SeedInstance(stateBase + "/codex/plain", lockText: null, version: null);
        SeedInstance(stateBase + "/codex/bad", lockText: "unparsable", version: null);
        _files.CreateDirectory(stateBase + "/codex/empty");
        _files.CreateDirectory("/kw/elsewhere/linked");
        _files.CreateDirectoryLink(stateBase + "/claude/linked", "/kw/elsewhere/linked");
        _files.WriteAllText(stateBase + "/stray.txt", "not a harness");
        _state.AddProject(new SquadProjectRegistryEntry("a", "/kw/work/a", DateTimeOffset.UnixEpoch, ["claude"], false, null));
        _state.AddProject(new SquadProjectRegistryEntry("b", "/kw/work/b", DateTimeOffset.UnixEpoch, ["claude"], true, "gone"));

        int exit = Run(new SquadStatusSettings { Global = true });

        Assert.Equal(0, exit);
        Assert.Equal(
            "Kyber-Squad deployment at /kw/home (global):\n"
            + "  none\n"
            + "\n"
            + "Instances:\n"
            + "  claude/default  1.2.3\n"
            + "  codex/bad  unknown\n"
            + "  codex/plain  unknown\n"
            + "\n"
            + "Registered projects:\n"
            + "  ok    /kw/work/a\n"
            + "  stale /kw/work/b (stale: gone)\n",
            Output());
    }

    [Fact]
    public void Execute_GlobalWithAnUnreadableRegistry_SaysSoAndExitsOne()
    {
        string stateBase = Home + "/.kyber-weave/squad/instance-state";
        SeedInstance(stateBase + "/claude/default", lockText: null, version: null);
        _state.ListProjectsFailure = new InvalidDataException("bad registry");

        int exit = Run(new SquadStatusSettings { Global = true });

        Assert.Equal(1, exit);
        string output = Output();
        Assert.Contains("  fail Squad project registry is unreadable: bad registry\n", output, StringComparison.Ordinal);
        Assert.EndsWith("The Kyber-Squad project registry is unreadable.\n", output, StringComparison.Ordinal);
    }

    private int Run(SquadStatusSettings settings)
    {
        SquadConsole console = new(_ansi, new FakeConsole());
        FakePathResolver paths = new(_files);
        SquadStatusCommand command = new(
            _state,
            _migration,
            new FakeSquadGlobalRootResolver(Home),
            _presets,
            new SquadFileSystemPathSemantics(paths),
            console,
            _files,
            new FakeProcessEnvironment(currentDirectory: Project),
            new SquadCommandRoots(_files, paths, new FakeProcessEnvironment(currentDirectory: Project), console));
        return command.Execute(null!, settings);
    }

    private void SeedInstance(string directory, string? lockText, string? version)
    {
        _files.CreateDirectory(directory);
        _files.WriteAllText(directory + "/squad.receipt.json", "{}");
        if (lockText is null) return;

        _files.WriteAllText(directory + "/squad.lock.yml", lockText);
        if (version is not null) _state.AddLockText(lockText, Lock(null, version));
    }

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static SquadReceipt ProjectReceipt(params SquadOwnedFile[] files) =>
        new("kyber-squad.receipt/v2", SquadDeploymentScope.Project, Project, DateTimeOffset.UnixEpoch, [], files);

    private static SquadOwnedFile OwnedFile(string relativePath, string content) =>
        new(relativePath, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), "claude", false);

    private static SquadLock Lock(IReadOnlyDictionary<SquadTarget, SquadAppliedPreset>? presets, string version = "1.0.0") =>
        new("kyber-squad.lock/v1", version, "0.0.0", "0.0.0", "full", ["claude"], [], "best-effort", Hash, Hash, new SquadApmIdentity("a", "b", Hash), presets);

    private sealed class ScriptedPresets : ISquadModelPresetStore
    {
        private readonly Dictionary<string, SquadModelPreset> _presets = new(StringComparer.Ordinal);

        public void Add(SquadModelPreset preset) => _presets[preset.Name] = preset;

        public IReadOnlyList<string> List() => [.. _presets.Keys];

        public SquadModelPreset? TryLoad(string name) => _presets.GetValueOrDefault(name);
    }
}
