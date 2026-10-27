using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="CouncilGateSelection.Plan"/> decides which repository-wide council gates run for one
/// change. A gate whose <c>include</c> matches no changed file is skipped rather than reported as
/// passed; a gate for another platform is skipped; and an unknown changed set never skips a gate
/// for <c>include</c>, because a gate skipped on a guess would hide the evidence it exists to give.
/// </summary>
public sealed class CouncilGateSelectionTests
{
    private static readonly string[] Changed = ["src/KyberWeave.Core/Review/PathGlob.cs"];

    [Fact]
    public void AnIncludeThatMatchesAChangedFileRunsTheGate()
    {
        ReviewGate gate = new("build", ["dotnet", "build"]) { Include = ["**/*.cs"] };

        CouncilGatePlan plan = CouncilGateSelection.Plan([gate], Changed, "unix");

        Assert.Equal("build", Assert.Single(plan.Run).Id);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void AnIncludeThatMatchesNoChangedFileSkipsTheGateWithAReason()
    {
        ReviewGate gate = new("web-lint", ["npx", "eslint"]) { Include = ["**/*.ts"] };

        CouncilGatePlan plan = CouncilGateSelection.Plan([gate], Changed, "unix");

        Assert.Empty(plan.Run);
        SkippedTaskGate skipped = Assert.Single(plan.Skipped);
        Assert.Equal("web-lint", skipped.Id);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Reason), "A skipped gate must say why it was skipped.");
    }

    [Fact]
    public void AGateWithNoIncludeAlwaysRuns()
    {
        ReviewGate gate = new("build", ["dotnet", "build"]);

        CouncilGatePlan plan = CouncilGateSelection.Plan([gate], ["docs/README.md"], "unix");

        Assert.Equal("build", Assert.Single(plan.Run).Id);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void AGateForAnotherPlatformIsSkippedWithAReason()
    {
        ReviewGate gate = new("windows-only", ["dotnet", "build"], Os: "windows");

        CouncilGatePlan plan = CouncilGateSelection.Plan([gate], Changed, "unix");

        Assert.Empty(plan.Run);
        SkippedTaskGate skipped = Assert.Single(plan.Skipped);
        Assert.Equal("windows-only", skipped.Id);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Reason), "A skipped gate must say why it was skipped.");
    }

    [Fact]
    public void AGateForThisPlatformRuns()
    {
        ReviewGate gate = new("windows-only", ["dotnet", "build"], Os: "windows");

        CouncilGatePlan plan = CouncilGateSelection.Plan([gate], Changed, "windows");

        Assert.Equal("windows-only", Assert.Single(plan.Run).Id);
        Assert.Empty(plan.Skipped);
    }

    /// <summary>
    /// When git cannot list the changed files the caller passes <c>null</c>. Every gate must still run
    /// for its platform, including a gate whose <c>include</c> would match nothing: a guess that skips
    /// evidence is the failure this rule exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnknownChangedSetSkipsNothingForIncludeButStillSkipsOnPlatform()
    {
        ReviewGate scoped = new("web-lint", ["npx", "eslint"]) { Include = ["**/*.ts"] };
        ReviewGate everywhere = new("build", ["dotnet", "build"]);
        ReviewGate otherOs = new("windows-only", ["dotnet", "build"], Os: "windows");

        CouncilGatePlan plan = CouncilGateSelection.Plan([scoped, everywhere, otherOs], null, "unix");

        Assert.Equal(["web-lint", "build"], plan.Run.Select(gate => gate.Id).ToArray());
        SkippedTaskGate skipped = Assert.Single(plan.Skipped);
        Assert.Equal("windows-only", skipped.Id);
    }

    [Fact]
    public void AnUnknownChangedSetNeverSkipsAGateWithAMatchingPlatform()
    {
        ReviewGate scoped = new("web-lint", ["npx", "eslint"]) { Include = ["**/*.ts"] };

        CouncilGatePlan plan = CouncilGateSelection.Plan([scoped], null, "unix");

        Assert.Equal("web-lint", Assert.Single(plan.Run).Id);
        Assert.Empty(plan.Skipped);
    }
}
