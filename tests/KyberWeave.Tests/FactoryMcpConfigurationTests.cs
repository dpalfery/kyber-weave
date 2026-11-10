using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the warning a Factory deployment prints when droids are allowed MCP servers that no
/// Factory config file defines. Each fact runs a real dry-run deployment of the checked-in squad.
/// </summary>
public sealed class FactoryMcpConfigurationTests : IDisposable
{
    private const string Version = "1.2.3";

    private readonly TempDirectory _temp = new();
    private readonly FakeSquadUserPaths _paths;
    private readonly string _home;
    private readonly string _project;

    public FactoryMcpConfigurationTests()
    {
        _paths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "app-data"));
        _home = _paths.HomeDirectory;
        _project = Path.Combine(_temp.Path, "work", "project");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_project);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ProjectInstall_NoConfigAnywhere_WarnsWithEachServerAndItsLaunchCommand()
    {
        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Project));

        Assert.Contains("kw-docs (kyber-weave-mcp --repo-root .)", warning, StringComparison.Ordinal);
        Assert.Contains("codegraph (codegraph serve --mcp --path .)", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("kw-arbiter", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInstall_ProjectConfigDefinesEveryServer_DoesNotWarn()
    {
        WriteConfig(_project, "kw-docs", "codegraph");

        Assert.Empty(await FactoryWarningsAsync(SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task ProjectInstall_ProjectConfigDefinesOneServer_WarnsOnlyAboutTheOther()
    {
        WriteConfig(_project, "kw-docs");

        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Project));

        Assert.Contains("codegraph (", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("kw-docs (", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInstall_UserConfigDefinesEveryServer_DoesNotWarn()
    {
        WriteConfig(_home, "kw-docs", "codegraph");

        Assert.Empty(await FactoryWarningsAsync(SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task ProjectInstall_AncestorConfigDefinesEveryServer_DoesNotWarn()
    {
        WriteConfig(Path.Combine(_temp.Path, "work"), "kw-docs", "codegraph");

        Assert.Empty(await FactoryWarningsAsync(SquadDeploymentScope.Project));
    }

    [Fact]
    public async Task GlobalInstall_ProjectConfigAloneDoesNotCount_WarnsThatOnlyProjectsCanSupplyThem()
    {
        WriteConfig(_project, "kw-docs", "codegraph");

        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Global));

        Assert.Contains("~/.factory/mcp.json does not define them", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalInstall_UserConfigDefinesEveryServer_DoesNotWarn()
    {
        WriteConfig(_home, "kw-docs", "codegraph");

        Assert.Empty(await FactoryWarningsAsync(SquadDeploymentScope.Global));
    }

    [Fact]
    public void InstanceDeployment_NoConfigAnywhere_DoesNotWarn()
    {
        Assert.Empty(FactoryMcpConfiguration.Warnings(
            [SquadTarget.Factory],
            SquadDeploymentScope.Instance,
            TestSquadCorpus.CanonicalSource,
            _project,
            _home));
    }

    [Fact]
    public async Task ProjectUpdate_NoConfigAnywhere_Warns()
    {
        using SyntheticValidSquadReleaseSource release = new();
        SquadLifecycleService service = NewService(release, new FactoryRenderer());
        await service.InstallAsync(
            new SquadInstallRequest(_project, SquadDeploymentScope.Project, [SquadTarget.Factory], Version: Version));

        SquadLifecycleResult result = await service.UpdateAsync(
            new SquadUpdateRequest(
                _project,
                SquadDeploymentScope.Project,
                [SquadTarget.Factory],
                Version: Version,
                DryRun: true));

        Assert.True(result.Success);
        Assert.Single(FactoryWarnings(result));
    }

    [Fact]
    public async Task ProjectInstall_MalformedConfig_WarnsRatherThanThrows()
    {
        string directory = Path.Combine(_project, ".factory");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "mcp.json");
        await File.WriteAllTextAsync(file, "{ not json");

        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Project));

        Assert.Contains("Fix these unreadable files first: ", warning, StringComparison.Ordinal);
        Assert.Contains($"{file} (", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInstall_ServersKeyIsNotAnObject_NamesTheFile()
    {
        string directory = Path.Combine(_project, ".factory");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "mcp.json");
        await File.WriteAllTextAsync(file, """{ "mcpServers": [] }""");

        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Project));

        Assert.Contains($"{file} (mcpServers is not a JSON object)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectInstall_BrokenHomeConfigAboveTheProject_IsNamedOnce()
    {
        string directory = Path.Combine(_home, ".factory");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "mcp.json");
        File.WriteAllText(file, "[]");
        string project = Path.Combine(_home, "repo");
        Directory.CreateDirectory(project);

        string warning = Assert.Single(FactoryMcpConfiguration.Warnings(
            [SquadTarget.Factory],
            SquadDeploymentScope.Project,
            TestSquadCorpus.CanonicalSource,
            project,
            _home));

        Assert.Single(warning.Split(file, StringSplitOptions.None)[1..]);
    }

    [Fact]
    public void Warning_ServerTextWithControlCharacters_StaysOnOneLine()
    {
        using JsonDocument servers = JsonDocument.Parse(
            """{ "mcpServers": { "bad\nname": { "command": "run\rit", "args": ["a\tb"] } } }""");
        SquadSource source = TestSquadCorpus.CanonicalSource with { McpConfiguration = servers.RootElement };

        string warning = Assert.Single(FactoryMcpConfiguration.Warnings(
            [SquadTarget.Factory],
            SquadDeploymentScope.Project,
            source,
            _project,
            _home));

        Assert.DoesNotContain(warning, char.IsControl);
        Assert.Contains("bad?name (run?it a?b)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInstall_NoConfigAnywhere_SaysAParentFolderCounts()
    {
        string warning = Assert.Single(await FactoryWarningsAsync(SquadDeploymentScope.Project));

        Assert.Contains("a parent folder", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInstall_FactoryNotATarget_DoesNotWarn()
    {
        Assert.Empty(await WarningsAsync(SquadDeploymentScope.Project, SquadTarget.Copilot, new CopilotRenderer()));
    }

    private void WriteConfig(string root, params string[] servers)
    {
        string directory = Path.Combine(root, ".factory");
        Directory.CreateDirectory(directory);
        string entries = string.Join(",", servers.Select(server => $"\"{server}\": {{ \"command\": \"x\" }}"));
        File.WriteAllText(Path.Combine(directory, "mcp.json"), $"{{ \"mcpServers\": {{ {entries} }} }}");
    }

    private Task<IReadOnlyList<string>> FactoryWarningsAsync(SquadDeploymentScope scope) =>
        WarningsAsync(scope, SquadTarget.Factory, new FactoryRenderer());

    private async Task<IReadOnlyList<string>> WarningsAsync(
        SquadDeploymentScope scope,
        SquadTarget target,
        ISquadRenderer renderer)
    {
        using SyntheticValidSquadReleaseSource release = new();

        SquadLifecycleResult result = await NewService(release, renderer).InstallAsync(
            new SquadInstallRequest(
                scope == SquadDeploymentScope.Global ? _home : _project,
                scope,
                [target],
                Version: Version,
                DryRun: true));

        Assert.True(result.Success);
        return FactoryWarnings(result);
    }

    private SquadLifecycleService NewService(ISquadReleaseSource release, ISquadRenderer renderer) =>
        new(
            release,
            new SquadRendererRegistry([renderer]),
            new SquadStateStore(_paths),
            globalRoots: SquadOperatorChannelFixture.GlobalRoots(Path.Combine(_home, ".copilot"), _home));

    private static IReadOnlyList<string> FactoryWarnings(SquadLifecycleResult result) =>
    [
        .. (result.Warnings ?? []).Where(warning =>
            warning.StartsWith(FactoryMcpConfiguration.WarningCode, StringComparison.Ordinal))
    ];
}
