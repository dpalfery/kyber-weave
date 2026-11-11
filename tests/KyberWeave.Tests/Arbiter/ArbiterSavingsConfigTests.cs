using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.3 (config): the decision-model savings keys under <c>arbiter:</c>, their
/// defaults, the <c>KW-ARB-CONFIG-012</c> to <c>-015</c> diagnostics, the shadow override rule for
/// shipped rules, and that this repository's own host config stays provider-free.
/// RED: the savings keys, <c>ArbiterRule.Shadow</c> and the new diagnostics do not exist yet.
/// </summary>
public sealed class ArbiterSavingsConfigTests
{
    private static readonly string[] SixProfiles =
    [
        "architect",
        "deep-planning",
        "fast",
        "general",
        "orchestration",
        "reviewer",
    ];

    [Fact]
    public void Defaults_WithNoSavingsKeys_UseShadowModeAndTheShippedLadder()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults;

        Assert.Equal("shadow", config.ModelTargeting.Mode);
        Assert.Equal("fast", config.ModelTargeting.DifficultyProfiles["easy"]);
        Assert.Equal("general", config.ModelTargeting.DifficultyProfiles["standard"]);
        Assert.Equal("deep-planning", config.ModelTargeting.DifficultyProfiles["hard"]);
        Assert.Equal(["fast", "general", "deep-planning"], config.ModelTargeting.Ladder);
        Assert.Equal(16384, config.Stores.MaxItemBytes);
        Assert.Equal(14, config.Stores.RetainDays);
        Assert.Empty(config.CommentTriage.BotLogins);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("on")]
    public void UnknownModelTargetingMode_RaisesConfig012NamingTheModes(string mode)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($"""
                arbiter:
                  model-targeting:
                    mode: {mode}
                """));

        Assert.Contains("[KW-ARB-CONFIG-012]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("model-targeting.mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifficultyValueOutsideTheSixProfiles_RaisesConfig012ListingTheSix()
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml("""
                arbiter:
                  model-targeting:
                    difficulty-profiles:
                      easy: claude-sonnet-5-5
                """));

        Assert.Contains("[KW-ARB-CONFIG-012]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("claude-sonnet-5-5", exception.Message, StringComparison.Ordinal);
        foreach (string profile in SixProfiles)
            Assert.Contains(profile, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ladder: []")]
    [InlineData("ladder: [fast, fast, general]")]
    [InlineData("ladder: [fast, claude-sonnet-5-5]")]
    public void InvalidLadder_RaisesConfig013WithAnExampleLadder(string ladder)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($"""
                arbiter:
                  model-targeting:
                    {ladder}
                """));

        Assert.Contains("[KW-ARB-CONFIG-013]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("deep-planning", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KW-ARB-DIFFICULTY-001", "true")]
    [InlineData("KW-ARB-DIFFICULTY-001", "false")]
    [InlineData("KW-ARB-LADDER-001", "true")]
    [InlineData("KW-ARB-LADDER-001", "false")]
    public void ShadowOverrideOnDifficultyOrLadderRule_RaisesConfig014NamingModeKey(string ruleId, string shadow)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($"""
                arbiter:
                  rules:
                    - id: {ruleId}
                      shadow: {shadow}
                """));

        Assert.Contains("[KW-ARB-CONFIG-014]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("model-targeting.mode", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("max-item-bytes: 0")]
    [InlineData("max-item-bytes: -1")]
    [InlineData("max-item-bytes: 1048577")]
    [InlineData("retain-days: 0")]
    public void OutOfRangeStoreLimit_RaisesConfig015(string setting)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($"""
                arbiter:
                  stores:
                    {setting}
                """));

        Assert.Contains("[KW-ARB-CONFIG-015]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankBotLogin_RaisesConfig015()
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml("""
                arbiter:
                  comment-triage:
                    bot-logins: ["  "]
                """));

        Assert.Contains("[KW-ARB-CONFIG-015]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("for-each: file")]
    public void HostRuleUsingForEach_RaisesConfig015(string extraField)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($$"""
                arbiter:
                  rules:
                    - id: HOST-SAVINGS-001
                      trigger: delegate
                      question: Is this a host savings rule?
                      answers: [yes, no]
                      effects: { yes: allow, no: escalate }
                      {{extraField}}
                """));

        Assert.Contains("[KW-ARB-CONFIG-015]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("retarget")]
    [InlineData("spawn")]
    [InlineData("answer")]
    [InlineData("same-failure")]
    [InlineData("advise")]
    [InlineData("drop")]
    [InlineData("duplicate")]
    public void HostRuleEffectInTheForbiddenSet_RaisesConfig015(string effect)
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml($$"""
                arbiter:
                  rules:
                    - id: HOST-SAVINGS-002
                      trigger: delegate
                      question: Is this a host savings rule?
                      answers: [yes, no]
                      effects: { yes: {{effect}}, no: escalate }
                """));

        Assert.Contains("[KW-ARB-CONFIG-015]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedRuleShadowFalse_IsAcceptedAndSetsRuleShadow()
    {
        ArbiterConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              rules:
                - id: KW-ARB-PLAN-001
                  shadow: false
            """).Arbiter;

        ArbiterRule rule = Assert.Single(config.Rules, candidate => candidate.Id == "KW-ARB-PLAN-001");
        Assert.False(rule.Shadow);
    }

    [Fact]
    public void ThisRepositoryHostConfig_LoadsWithProviderKindNone()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(KyberWeaveTestPaths.ToolRoot);

        Assert.Equal(ArbiterProviderKind.None, config.Arbiter.Provider.Kind);
    }
}
