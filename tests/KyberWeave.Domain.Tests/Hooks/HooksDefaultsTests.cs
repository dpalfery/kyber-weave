using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class HooksDefaultsTests
{
    private static HookRule Rule(string id) => Assert.Single(HooksDefaults.Config.Rules, r => r.Id == id);

    [Fact]
    public void HasThirtySecondBudgetAndReportsAllFailures()
    {
        Assert.Equal(30, HooksDefaults.Config.TimeBudgetSeconds);
        Assert.False(HooksDefaults.Config.StopOnFirstBlocking);
    }

    [Fact]
    public void WhitespaceRuleMatchesTheRepositoryGateShape()
    {
        HookRule rule = Rule("dotnet-format-whitespace");

        Assert.Equal(["dotnet", "format", "{marker}", "whitespace", "--verify-no-changes", "--include", "{file}", "-v", "minimal"], rule.Run);
        Assert.Equal(["**/*.cs", "**/*.csproj", "**/*.props"], rule.Include);
        Assert.True(rule.Enabled);
        Assert.Equal(HookRuleKind.Lint, rule.Kind);
    }

    [Fact]
    public void StyleRuleUsesWarnSeverity()
    {
        HookRule rule = Rule("dotnet-format-style");

        Assert.Equal(["dotnet", "format", "{marker}", "style", "--verify-no-changes", "--severity", "warn", "--include", "{file}", "-v", "minimal"], rule.Run);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void TestRuleExistsButIsOffAndNeverRunsAWholeSuite()
    {
        HookRule rule = Rule("dotnet-test");

        Assert.False(rule.Enabled);
        Assert.Equal(HookRuleKind.Test, rule.Kind);
        Assert.Contains("{testClasses}", rule.Run);
        Assert.Contains("--filter-class", rule.Run);
    }

    [Fact]
    public void MergeOverridesOnlyTheFieldsASameIdRuleSets()
    {
        HooksConfigOverlay overlay = new(null, true, null, null, [new HookRuleOverlay("dotnet-test", null, null, null, null, null, null, true)], []);

        HooksConfig merged = HooksConfigMerger.Merge(HooksDefaults.Config, overlay);

        HookRule test = Assert.Single(merged.Rules, r => r.Id == "dotnet-test");
        Assert.True(test.Enabled);
        Assert.Equal(HooksDefaults.Config.Rules.Single(r => r.Id == "dotnet-test").Run, test.Run);
        Assert.True(merged.StopOnFirstBlocking);
        Assert.Equal(30, merged.TimeBudgetSeconds);
    }

    [Fact]
    public void MergeAppendsACompleteNewRuleAndDropsAnIncompleteOne()
    {
        HooksConfigOverlay overlay = new(
            null,
            null,
            null,
            null,
            [
                new HookRuleOverlay("ts", HookRuleKind.Lint, ["**/*.ts"], null, ["npx", "eslint", "{file}"], null, null, null),
                new HookRuleOverlay("half", HookRuleKind.Lint, null, null, null, null, null, null)
            ],
            []);

        HooksConfig merged = HooksConfigMerger.Merge(HooksDefaults.Config, overlay);

        HookRule added = Assert.Single(merged.Rules, r => r.Id == "ts");
        Assert.True(added.Enabled);
        Assert.True(added.Blocking);
        Assert.DoesNotContain(merged.Rules, r => r.Id == "half");
        Assert.Contains(merged.Warnings, w => w.Contains("'half'", StringComparison.Ordinal));
    }
}
