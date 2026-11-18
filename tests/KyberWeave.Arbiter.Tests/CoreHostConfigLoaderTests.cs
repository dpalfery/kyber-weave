using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Arbiter guards' host-config adapter reads through the configuration repository, so a unit test can pin both
/// the loaded result and the failed one without a config file on disk.
/// </summary>
public sealed class CoreHostConfigLoaderTests
{
    [Fact]
    public void ASuccessfulLoadCarriesTheConfigAndTheFileItRead()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig("/repo", "task-prompt-budget: 41007\n");
        CoreHostConfigLoader loader = new(configs);

        HostConfigLoadResult result = loader.TryLoad("/repo");

        Assert.True(result.Success);
        Assert.Equal(41007, result.Config!.TaskPromptBudget);
        Assert.Equal(FakeKyberWeaveConfigRepository.RepositoryConfigPath("/repo"), result.ConfigPath);
        Assert.Null(result.Error);
        Assert.Equal([Path.GetFullPath("/repo")], configs.LoadedRoots);
    }

    [Fact]
    public void AFailedLoadCarriesTheErrorAndNoConfig()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig("/repo", "task-prompt-budget: not-a-number\n");
        CoreHostConfigLoader loader = new(configs);

        HostConfigLoadResult result = loader.TryLoad("/repo");

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Equal(FakeKyberWeaveConfigRepository.RepositoryConfigPath("/repo"), result.ConfigPath);
    }
}
