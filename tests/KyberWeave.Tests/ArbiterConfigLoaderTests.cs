using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for the user/global arbiter configuration schema and validation.
/// </summary>
public sealed class ArbiterConfigLoaderTests
{
    [Fact]
    public void UserLevelGlobalTargetsRoundTripThroughLoader()
    {
        string yaml = """
            arbiter:
              enabled: true
              global:
                targets: [harness-token-01, harness-token-02]
            """;

        ArbiterConfig config = ArbiterConfigLoader.LoadUserConfig(yaml);

        Assert.Equal(["harness-token-01", "harness-token-02"], config.Global.Targets);
    }

    [Fact]
    public void ProjectLevelGlobalTargetsFailValidationWithRuleId011()
    {
        YamlException exception = Assert.Throws<YamlException>(() =>
            KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              global:
                targets: [harness-token-01]
            """));

        Assert.Contains("KW-ARB-CONFIG-011", exception.Message, StringComparison.Ordinal);
    }
}
