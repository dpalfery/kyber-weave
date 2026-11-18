using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A Squad verb's configuration read answers from the repository it is given, so a test arranges the home files in
/// memory and no branch of the read consults the disk behind the fake.
/// </summary>
public sealed class SquadScopeConfigTests
{
    private const string CustomVersion = "squad:\n  version: 9.9.9\n";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "kw-squad-home-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GlobalScopeReadsTheLegacyHomeConfigTheRepositoryHolds()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteLegacyConfig(_home, CustomVersion);

        KyberWeaveConfigLoadResult result = SquadCommandComposition.TryLoadScopeConfig(
            configs, _home, SquadDeploymentScope.Global, _home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.LegacyConfigPath(_home), result.ConfigPath);
        Assert.Equal("9.9.9", result.Config!.Squad.Version);
    }

    [Fact]
    public void GlobalScopePrefersTheUserConfigOverTheLegacyRootFile()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteUserConfig(_home, "squad:\n  version: 1.1.1\n");
        configs.WriteLegacyConfig(_home, CustomVersion);

        KyberWeaveConfigLoadResult result = SquadCommandComposition.TryLoadScopeConfig(
            configs, _home, SquadDeploymentScope.Global, _home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.UserConfigPath(_home), result.ConfigPath);
        Assert.Equal("1.1.1", result.Config!.Squad.Version);
    }

    [Fact]
    public void ProjectScopeReadsTheRepositoryConfigTheRepositoryHolds()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig(_home, CustomVersion);

        KyberWeaveConfigLoadResult result = SquadCommandComposition.TryLoadScopeConfig(
            configs, _home, SquadDeploymentScope.Project, _home);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.RepositoryConfigPath(_home), result.ConfigPath);
        Assert.Equal("9.9.9", result.Config!.Squad.Version);
    }
}
