using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class HookRouterTests
{
    private static readonly ProjectFacts Facts = new("/repo", "KyberWeave.sln", "src/App/App.csproj", "tests/App.Tests/App.Tests.csproj", ["App.Tests.FooTests"]);

    private static HooksConfig WithTestsOn() => HooksConfigMerger.Merge(
        HooksDefaults.Config,
        new HooksConfigOverlay(null, null, null, null, [new HookRuleOverlay("dotnet-test", null, null, null, null, null, null, true)], []),
        HookConfigSource.Repository);

    [Fact]
    public void PlansBothFormatChecksForACSharpFileWithTheProjectAndFile()
    {
        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(HooksDefaults.Config, "src/App/Foo.cs", Facts);

        Assert.Equal(["dotnet-format-whitespace", "dotnet-format-style"], plan.Select(p => p.RuleId));
        Assert.Equal(["dotnet", "format", "src/App/App.csproj", "whitespace", "--verify-no-changes", "--include", "src/App/Foo.cs", "-v", "minimal"], plan[0].Argv);
    }

    [Fact]
    public void PlansNothingForAnUnrelatedFile() =>
        Assert.Empty(HookRouter.Plan(HooksDefaults.Config, "docs/readme.md", Facts));

    [Fact]
    public void ExcludesBuildOutput() =>
        Assert.Empty(HookRouter.Plan(HooksDefaults.Config, "src/App/obj/Debug/Gen.cs", Facts));

    [Theory]
    [InlineData("src/App/Foo.CS")]
    [InlineData("src\\App\\Foo.cs")]
    public void MatchesGlobsIgnoringCaseAndSeparatorStyle(string path) =>
        Assert.NotEmpty(HookRouter.Plan(HooksDefaults.Config, path, Facts));

    [Fact]
    public void ExcludesBuildOutputIgnoringCase() =>
        Assert.Empty(HookRouter.Plan(HooksDefaults.Config, "src/App/OBJ/Debug/Gen.cs", Facts));

    [Fact]
    public void SkipsATestRuleThatIsOffByDefault() =>
        Assert.DoesNotContain(HookRouter.Plan(HooksDefaults.Config, "src/App/Foo.cs", Facts), p => p.Kind == HookRuleKind.Test);

    [Fact]
    public void RunsLintBeforeTestsAndExpandsTestClassesToOneArgumentEach()
    {
        ProjectFacts facts = Facts with { TestClasses = ["A.FooTests", "B.FooTests"] };

        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(WithTestsOn(), "src/App/Foo.cs", facts);

        Assert.Equal(HookRuleKind.Test, plan[^1].Kind);
        Assert.All(plan.Take(plan.Count - 1), p => Assert.Equal(HookRuleKind.Lint, p.Kind));
        Assert.Equal(
            ["dotnet", "test", "--project", "tests/App.Tests/App.Tests.csproj", "-c", "Release", "--filter-class", "A.FooTests", "B.FooTests"],
            plan[^1].Argv);
    }

    [Fact]
    public void LeavesOutATestRuleWhenNoTestClassCoversTheFile()
    {
        ProjectFacts facts = Facts with { TestClasses = [], TestProject = null };

        Assert.DoesNotContain(HookRouter.Plan(WithTestsOn(), "src/App/Foo.cs", facts), p => p.Kind == HookRuleKind.Test);
    }

    [Fact]
    public void ReportsARuleThatEmbedsTestClassesInsideALargerArgument()
    {
        HookRule rule = new("embedded", HookRuleKind.Test, ["**"], [], ["dotnet", "--filter-class={testClasses}"], 10, true, true);
        List<string> notes = [];

        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan([rule], "f", Facts, notes);

        Assert.Empty(plan);
        Assert.Equal(["rule 'embedded' was not run: {testClasses} must be a whole argument, not part of a longer one."], notes);
    }

    [Fact]
    public void ReportsAnEmbeddedRuleThroughTheConfigOverloadToo()
    {
        HookRule rule = new("embedded", HookRuleKind.Test, ["**"], [], ["dotnet", "--filter-class={testClasses}"], 10, true, true);
        List<string> notes = [];

        _ = HookRouter.Plan(new HooksConfig(30, false, 30, 3000, [rule], []), "f", Facts, notes);

        Assert.Contains("rule 'embedded' was not run", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void AddsNoNoteForAMissingTestClassValue()
    {
        List<string> notes = [];

        _ = HookRouter.Plan(HookRouter.MatchingRules(WithTestsOn(), "src/App/Foo.cs"), "src/App/Foo.cs", Facts with { TestClasses = [], TestProject = null }, notes);

        Assert.Empty(notes);
    }

    [Fact]
    public void FallsBackToTheSolutionWhenThereIsNoProject()
    {
        PlannedCommand first = HookRouter.Plan(HooksDefaults.Config, "src/App/Foo.cs", Facts with { Project = null })[0];

        Assert.Equal("KyberWeave.sln", first.Argv[2]);
    }

    [Fact]
    public void ExpandsMarkerToTheSolutionEvenWhenAProjectExists()
    {
        HookRule rule = new("custom", HookRuleKind.Lint, ["**"], [], ["tool", "{marker}", "{target}"], 10, true, true);

        PlannedCommand command = Assert.Single(HookRouter.Plan(new HooksConfig(30, false, 30, 3000, [rule], []), "f", Facts));

        Assert.Equal(["tool", "KyberWeave.sln", "src/App/App.csproj"], command.Argv);
    }

    [Fact]
    public void LeavesOutARuleWhoseTargetIsMissing() =>
        Assert.Empty(HookRouter.Plan(HooksDefaults.Config, "src/App/Foo.cs", Facts with { Marker = null, Project = null }));

    [Fact]
    public void ExpandsRootProjectAndFileTokensInsideLargerArguments()
    {
        HookRule rule = new("custom", HookRuleKind.Lint, ["**/*.ts"], [], ["tool", "--cfg={root}/c.json", "{project}", "{file}"], 10, true, true);
        HooksConfig config = new(30, false, 30, 3000, [rule], []);

        PlannedCommand command = Assert.Single(HookRouter.Plan(config, "web/a.ts", Facts));

        Assert.Equal(["tool", "--cfg=/repo/c.json", "src/App/App.csproj", "web/a.ts"], command.Argv);
        Assert.Equal(10, command.TimeoutSeconds);
    }

    [Fact]
    public void MatchingRulesListsOnlyEnabledRulesWhoseGlobsMatch()
    {
        Assert.Equal(["dotnet-format-whitespace", "dotnet-format-style"], HookRouter.MatchingRules(HooksDefaults.Config, "src/App/Foo.cs").Select(r => r.Id));
        Assert.Equal(["dotnet-format-whitespace", "dotnet-format-style", "dotnet-test"], HookRouter.MatchingRules(WithTestsOn(), "src/App/Foo.cs").Select(r => r.Id));
        Assert.Empty(HookRouter.MatchingRules(WithTestsOn(), "docs/readme.md"));
    }

    [Fact]
    public void PlacesLintBeforeTestsEvenWhenTheConfigListsATestFirst()
    {
        HookRule t = new("t", HookRuleKind.Test, ["**"], [], ["y"], 5, true, true);
        HookRule a = new("a", HookRuleKind.Lint, ["**"], [], ["x"], 5, true, true);

        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(new HooksConfig(30, false, 30, 3000, [t, a], []), "f", Facts);

        Assert.Equal(["a", "t"], plan.Select(p => p.RuleId));
    }

    [Fact]
    public void KeepsConfigOrderWithinAKind()
    {
        HookRule a = new("a", HookRuleKind.Lint, ["**"], [], ["x"], 5, true, true);
        HookRule t = new("t", HookRuleKind.Test, ["**"], [], ["y"], 5, true, true);
        HookRule b = new("b", HookRuleKind.Lint, ["**"], [], ["z"], 5, true, true);

        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(new HooksConfig(30, false, 30, 3000, [a, t, b], []), "f", Facts);

        Assert.Equal(["a", "b", "t"], plan.Select(p => p.RuleId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.cs")]
    [InlineData("src/../../escape.cs")]
    public void RejectsAnUnusableRelativePath(string path)
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => HookRouter.MatchingRules(HooksDefaults.Config, path));
        _ = Assert.ThrowsAny<ArgumentException>(() => HookRouter.Plan(HooksDefaults.Config, path, Facts));
    }

    [Fact]
    public void RejectsANullRelativePath() =>
        _ = Assert.Throws<ArgumentNullException>(() => HookRouter.MatchingRules(HooksDefaults.Config, null!));

    [Fact]
    public void NormalizesDotSegmentsBeforeMatchingAndExpanding()
    {
        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(HooksDefaults.Config, "src/./Other/../App/Foo.cs", Facts);

        Assert.Contains("src/App/Foo.cs", plan[0].Argv);
    }

    [Fact]
    public void LeavesOutARuleWhoseTestClassesTokenIsEmbeddedInALargerArgument()
    {
        HookRule rule = new("embedded", HookRuleKind.Test, ["**/*.cs"], [], ["dotnet", "test", "--filter={testClasses}"], 30, true, true);
        HooksConfig config = HooksDefaults.Config with { Rules = [rule] };

        Assert.Empty(HookRouter.Plan(config, "src/App/Foo.cs", Facts));
    }
}
