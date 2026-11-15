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

        Assert.Equal(["dotnet", "format", "{target}", "whitespace", "--verify-no-changes", "--include", "{file}", "-v", "minimal"], rule.Run);
        Assert.Equal(["**/*.cs", "**/*.csproj", "**/*.props"], rule.Include);
        Assert.True(rule.Enabled);
        Assert.Equal(HookRuleKind.Lint, rule.Kind);
    }

    [Fact]
    public void StyleRuleUsesWarnSeverity()
    {
        HookRule rule = Rule("dotnet-format-style");

        Assert.Equal(["dotnet", "format", "{target}", "style", "--verify-no-changes", "--severity", "warn", "--include", "{file}", "-v", "minimal"], rule.Run);
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

    [Fact]
    public void MergeClampsOutOfRangeNumbersAndWarns()
    {
        HooksConfigOverlay overlay = new(
            0,
            null,
            2_000_000,
            0,
            [new HookRuleOverlay("dotnet-test", null, null, null, null, 100_000, null, null)],
            []);

        HooksConfig merged = HooksConfigMerger.Merge(HooksDefaults.Config, overlay);

        Assert.Equal(1, merged.TimeBudgetSeconds);
        Assert.Equal(500, merged.MaxOutputLines);
        Assert.Equal(100, merged.MaxOutputBytes);
        Assert.Equal(300, Assert.Single(merged.Rules, r => r.Id == "dotnet-test").TimeoutSeconds);
        Assert.Contains(merged.Warnings, w => w.Contains("'time-budget-seconds' must be between 1 and 120; got 0; using 1.", StringComparison.Ordinal));
        Assert.Contains(merged.Warnings, w => w.Contains("rule 'dotnet-test': 'timeout-seconds' must be between ", StringComparison.Ordinal) && w.Contains("; got 100000; using ", StringComparison.Ordinal));
        Assert.Equal(4, merged.Warnings.Count);
    }

    [Fact]
    public void MergeLeavesInRangeNumbersAlone()
    {
        HooksConfigOverlay overlay = new(60, null, 10, 2000, [], []);

        HooksConfig merged = HooksConfigMerger.Merge(HooksDefaults.Config, overlay);

        Assert.Equal((60, 10, 2000), (merged.TimeBudgetSeconds, merged.MaxOutputLines, merged.MaxOutputBytes));
        Assert.Empty(merged.Warnings);
    }
}
