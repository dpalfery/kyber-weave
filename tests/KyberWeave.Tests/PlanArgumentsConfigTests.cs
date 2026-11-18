using KyberWeave.Cli.Commands;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Resolving a plan argument reads the configuration through the repository, so a unit test can pin the loaded file
/// and the product-defaults fallback without one on disk.
/// </summary>
public sealed class PlanArgumentsConfigTests
{
    [Fact]
    public void TheRepositoryConfigIsUsedWhenItLoads()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig("/repo", "task-prompt-budget: 41008\n");

        KyberWeaveConfig config = PlanArguments.ConfigForResolving("/repo", null, configs);

        Assert.Equal(41008, config.TaskPromptBudget);
    }

    [Fact]
    public void TheProductDefaultsAreUsedWhenTheConfigDoesNotLoad()
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig("/repo", "task-prompt-budget: not-a-number\n");

        KyberWeaveConfig config = PlanArguments.ConfigForResolving("/repo", null, configs);

        Assert.Equal(KyberWeaveConfig.DefaultTaskPromptBudget, config.TaskPromptBudget);
    }
}
