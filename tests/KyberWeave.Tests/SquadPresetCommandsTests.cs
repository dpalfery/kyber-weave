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

/// <summary>What the <c>squad preset</c> verbs print, read and record, over an in-memory disk, fake stores and a test console.</summary>
public sealed class SquadPresetCommandsTests
{
    private const string Work = "/kw/work";
    private const string Home = "/kw/home";

    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeSquadUserPaths _paths = new("/kw/app", Home);
    private readonly FakeSquadStateStore _state;
    private readonly FakePresetStore _presets = new();
    private readonly FakeSelectionStore _selections = new();
    private SquadCanonicalProfiles? _canonical;

    public SquadPresetCommandsTests()
    {
        _state = new FakeSquadStateStore(_paths);
    }

    [Fact]
    public void List_PresetWithNoUsers_NamesItsFileAndNone()
    {
        _presets.Names.AddRange(["beta", "alpha"]);

        int exit = List().Execute(null!, new SquadPresetListSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(
            $"alpha  {PresetPath("alpha")}  used by: none\nbeta  {PresetPath("beta")}  used by: none\n",
            Output());
    }

    [Fact]
    public void List_SelectionFileInTheHome_CountsTheRootItNames()
    {
        _presets.Names.Add("alpha");
        string selections = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        _files.CreateDirectory(selections);
        _files.WriteAllText(Path.Combine(selections, "other.yml"), "schema: x\nroot: /kw/other\n");
        _selections.Selections["/kw/other"] = new SquadModelPresetSelection("/kw/other", new Dictionary<SquadTarget, string> { [SquadTarget.Codex] = "alpha" });
        _selections.Selections[Work] = new SquadModelPresetSelection(Work, new Dictionary<SquadTarget, string> { [SquadTarget.Claude] = "alpha" });

        List().Execute(null!, new SquadPresetListSettings(), CancellationToken.None);

        Assert.Equal($"alpha  {PresetPath("alpha")}  used by: project:claude, project:codex\n", Output());
    }

    [Fact]
    public void List_SelectionFileThatCannotBeRead_IsSkipped()
    {
        _presets.Names.Add("alpha");
        string selections = Path.Combine(Home, ".kyber-weave", "squad", "selections");
        _files.CreateDirectory(selections);
        _files.WriteAllText(Path.Combine(selections, "other.yml"), "root: /kw/other\n");
        _selections.Selections["/kw/other"] = new SquadModelPresetSelection("/kw/other", new Dictionary<SquadTarget, string> { [SquadTarget.Codex] = "alpha" });
        _files.FailWhen(operation => operation.Path is { } path && path.EndsWith("other.yml", StringComparison.Ordinal), new IOException("locked"));

        int exit = List().Execute(null!, new SquadPresetListSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal($"alpha  {PresetPath("alpha")}  used by: none\n", Output());
    }

    [Fact]
    public void Show_UnknownPreset_RefusesWithExit2()
    {
        int exit = Show().Execute(null!, new SquadPresetShowSettings { Name = "nope", Target = "codex" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad: error: Unknown preset 'nope'.\n", Output());
    }

    [Fact]
    public void Show_WithoutATarget_RefusesWithExit2()
    {
        _presets.Loadable["alpha"] = Preset("alpha");

        int exit = Show().Execute(null!, new SquadPresetShowSettings { Name = "alpha" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad: error: No target specified. Specify a target using --target <target>.\n", Output());
    }

    [Fact]
    public void Show_Warp_SaysItRendersNoModel()
    {
        _presets.Loadable["alpha"] = Preset("alpha");

        int exit = Show().Execute(null!, new SquadPresetShowSettings { Name = "alpha", Target = "warp" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("Warp renders no model.\n", Output());
    }

    [Fact]
    public void Show_NoCanonicalProfiles_ReportsTheSharedRemedyWithExit1()
    {
        _presets.Loadable["alpha"] = Preset("alpha");

        int exit = Show().Execute(null!, new SquadPresetShowSettings { Name = "alpha", Target = "codex" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.StartsWith("kyber-weave squad: error: Could not resolve Squad model profiles from canonical source.", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Show_Profiles_MarksOnlyTheBundledRows()
    {
        _canonical = Canonical("fast", "slow");
        _presets.Loadable["alpha"] = Preset("alpha", ("fast", "m9"));

        int exit = Show().Execute(null!, new SquadPresetShowSettings { Name = "alpha", Target = "codex" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("Canonical model profiles from the current checkout.\nfast: m9\nslow: m-slow (bundled)\n", Output());
    }

    [Fact]
    public void Use_UnknownPreset_ListsTheAvailableOnes()
    {
        _presets.Names.AddRange(["alpha", "beta"]);

        int exit = Use().Execute(null!, new SquadPresetUseSettings { Name = "nope", Targets = ["codex"] }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad: error: Unknown preset 'nope'. Available presets: alpha, beta.\n", Output());
    }

    [Fact]
    public void Use_TargetNotDeployed_RecordsTheSelectionAndSaysSo()
    {
        _presets.Loadable["alpha"] = Preset("alpha");

        int exit = Use().Execute(null!, new SquadPresetUseSettings { Name = "alpha", Targets = ["codex"] }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("Recorded `alpha` for `codex` in this project; it applies on install or `squad run`.\n", Output());
        Assert.Equal([(Work, SquadTarget.Codex, "alpha")], _selections.Recorded);
    }

    [Fact]
    public void Use_DryRun_PrintsTheModelChangeReadFromTheDeployedFile()
    {
        _canonical = Canonical("fast");
        _presets.Loadable["alpha"] = Preset("alpha", ("fast", "m9"));
        string relative = ".codex/agents/worker.md";
        _files.CreateDirectory(Path.Combine(Work, ".codex", "agents"));
        _files.WriteAllBytes(Path.Combine(Work, relative), Encoding.UTF8.GetBytes("---\nname: worker\nmodel-profile: fast\nmodel: m-fast\n---\nbody\n"));
        _state.Add(new SquadReceipt("kyber-squad.receipt/v2", SquadDeploymentScope.Project, Work, DateTimeOffset.UnixEpoch, [], [new SquadOwnedFile(relative, new string('0', 64), "codex", false)]));

        int exit = Use().Execute(null!, new SquadPresetUseSettings { Name = "alpha", Targets = ["codex"], DryRun = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("worker: m-fast -> m9\n", Output());
        Assert.Empty(_selections.Recorded);
    }

    [Fact]
    public void Clear_WithNoTargetAndNoDeployment_RefusesWithExit2()
    {
        int exit = Clear().Execute(null!, new SquadPresetClearSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad: error: No deployment target specified. Specify target(s) using --target <target>.\n", Output());
    }

    [Fact]
    public void Clear_TargetNotDeployed_ClearsTheRecordedSelection()
    {
        int exit = Clear().Execute(null!, new SquadPresetClearSettings { Targets = ["codex"] }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal([(Work, SquadTarget.Codex)], _selections.Cleared);
    }

    [Fact]
    public void Clear_DryRun_PrintsTheModelChangeReadFromTheDeployedFile()
    {
        _canonical = Canonical("fast");
        string relative = ".codex/agents/worker.md";
        _files.CreateDirectory(Path.Combine(Work, ".codex", "agents"));
        _files.WriteAllBytes(Path.Combine(Work, relative), Encoding.UTF8.GetBytes("---\nname: worker\nmodel-profile: fast\nmodel: m9\n---\nbody\n"));
        _state.Add(new SquadReceipt("kyber-squad.receipt/v2", SquadDeploymentScope.Project, Work, DateTimeOffset.UnixEpoch, [], [new SquadOwnedFile(relative, new string('0', 64), "codex", false)]));

        int exit = Clear().Execute(null!, new SquadPresetClearSettings { Targets = ["codex"], DryRun = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal("worker: m9 -> m-fast\n", Output());
    }

    private static string PresetPath(string name) => Path.Combine(Home, ".kyber-weave", "squad", "presets", $"{name}.yml");

    private static SquadModelPreset Preset(string name, params (string Category, string Model)[] overrides) =>
        new(name, "/p/" + name + ".yml", "digest", overrides.ToDictionary(
            entry => entry.Category,
            entry => new SquadModelProfile(entry.Model, new Dictionary<string, string>()),
            StringComparer.Ordinal));

    private static SquadCanonicalProfiles Canonical(params string[] categories) => new(
        string.Empty,
        new SquadModelProfiles(
            "kyber-squad.model-profiles/v1",
            categories.ToDictionary(
                category => category,
                category => new SquadModelProfile("m-" + category, new Dictionary<string, string>()),
                StringComparer.Ordinal),
            "/profiles.yml"),
        new Dictionary<string, string> { ["worker"] = categories[0] });

    private SquadConsole Console() => new(_ansi, _streams);

    private SquadCommandRoots Roots() => SquadTestCommands.InMemoryRoots(Console(), Work, _files);

    private SquadPresetListCommand List() =>
        SquadTestCommands.PresetListOver(_paths, _state, _presets, _selections, Roots(), _files, Console());

    private SquadPresetShowCommand Show() =>
        SquadTestCommands.PresetShowOver(_paths, _presets, _ => _canonical, Console());

    private SquadPresetUseCommand Use() =>
        SquadTestCommands.PresetUseOver(_state, _presets, _selections, _ => _canonical, Roots(), _files, Console());

    private SquadPresetClearCommand Clear() =>
        SquadTestCommands.PresetClearOver(_state, _presets, _selections, _ => _canonical, Roots(), _files, Console());

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class FakePresetStore : ISquadModelPresetStore
    {
        public List<string> Names { get; } = [];

        public Dictionary<string, SquadModelPreset> Loadable { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> List() => Names;

        public SquadModelPreset? TryLoad(string name) => Loadable.GetValueOrDefault(name);
    }

    private sealed class FakeSelectionStore : ISquadModelPresetSelectionStore
    {
        public Dictionary<string, SquadModelPresetSelection> Selections { get; } = new(StringComparer.Ordinal);

        public List<(string Root, SquadTarget Target, string Preset)> Recorded { get; } = [];

        public List<(string Root, SquadTarget Target)> Cleared { get; } = [];

        public SquadModelPresetSelection? Read(string projectRoot) => Selections.GetValueOrDefault(projectRoot);

        public void Record(string projectRoot, SquadTarget target, string presetName) => Recorded.Add((projectRoot, target, presetName));

        public void Clear(string projectRoot, SquadTarget target) => Cleared.Add((projectRoot, target));

        public void Clear(string projectRoot) => throw new NotSupportedException();
    }
}
