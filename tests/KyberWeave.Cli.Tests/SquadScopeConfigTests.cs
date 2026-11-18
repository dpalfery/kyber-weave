using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A Squad verb's configuration read answers from the repository it is given, so a test arranges the home files in
/// memory and no branch of the read consults the disk behind the fake.
/// </summary>
public sealed class SquadScopeConfigTests
{
    private const string CustomVersion = "squad:\n  version: 9.9.9\n";

    private const string Home = "/kw/home";

    private readonly SquadCommandRoots _roots = new(
        new InMemoryFileSystem(),
        FakePathResolver.OverEmptyFileSystem(),
        new FakeProcessEnvironment(),
        new SquadConsole(new TestConsole(), new FakeConsole()));

    [Fact]
    public void GlobalScopeReadsTheLegacyHomeConfigTheRepositoryHolds()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteLegacyConfig(Home, CustomVersion);

        KyberWeaveConfigLoadResult result = _roots.TryLoadScopeConfig(
            configs, Home, SquadDeploymentScope.Global, Home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.LegacyConfigPath(Home), result.ConfigPath);
        Assert.Equal("9.9.9", result.Config!.Squad.Version);
    }

    [Fact]
    public void GlobalScopePrefersTheUserConfigOverTheLegacyRootFile()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteUserConfig(Home, "squad:\n  version: 1.1.1\n");
        configs.WriteLegacyConfig(Home, CustomVersion);

        KyberWeaveConfigLoadResult result = _roots.TryLoadScopeConfig(
            configs, Home, SquadDeploymentScope.Global, Home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.UserConfigPath(Home), result.ConfigPath);
        Assert.Equal("1.1.1", result.Config!.Squad.Version);
    }

    [Fact]
    public void ProjectScopeReadsTheRepositoryConfigTheRepositoryHolds()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig(Home, CustomVersion);

        KyberWeaveConfigLoadResult result = _roots.TryLoadScopeConfig(
            configs, Home, SquadDeploymentScope.Project, Home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.RepositoryConfigPath(Home), result.ConfigPath);
        Assert.Equal("9.9.9", result.Config!.Squad.Version);
    }
}
