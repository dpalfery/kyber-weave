using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review:</c> section decides what a reviewer is allowed to execute and what it may
/// never approve, so every constraint is enforced where the operator wrote it rather than
/// where something later acts on it.
/// </summary>
public class ReviewConfigTests
{
    private static ReviewConfig Load(string yaml) =>
        KyberWeaveConfigLoader.LoadFromYaml(yaml).Review;

    private static YamlException Invalid(string yaml) =>
        Assert.ThrowsAny<YamlException>(() => Load(yaml));

    [Fact]
    public void AnUnconfiguredHostDeclaresNoGatesAndNoReservedPaths()
    {
        ReviewConfig config = Load("ontology:\n  docs-root: docs\n");

        Assert.Empty(config.Gates);
        Assert.Empty(config.Policy.AlwaysHuman);
        Assert.Equal(10_000, config.Policy.MaxReviewableLines);
        Assert.Equal(7, config.Policy.MinConfidence);
        Assert.False(config.Coverage.IsDeclared);
    }

    [Fact]
    public void GatesAreReadAsArgvInDeclarationOrder()
    {
        ReviewConfig config = Load("""
            review:
              gates:
                - id: build
                  run: [dotnet, build, -c, Release]
                - id: test
                  run: [dotnet, test]
                  blocking: false
            """);

        Assert.Equal(["build", "test"], config.Gates.Select(g => g.Id));
        Assert.Equal(["dotnet", "build", "-c", "Release"], config.Gates[0].Run);
        Assert.True(config.Gates[0].Blocking);
        Assert.False(config.Gates[1].Blocking);
    }

    /// <summary>
    /// A gate id is cited in reports and suppressions, so a duplicate makes every downstream
    /// reference ambiguous rather than merely untidy.
    /// </summary>
    [Fact]
    public void ADuplicateGateIdIsRejected() =>
        Assert.Contains(
            "more than once",
            Invalid("review:\n  gates:\n    - id: build\n      run: [a]\n    - id: build\n      run: [b]\n").Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("Build")]
    [InlineData("build_gate")]
    [InlineData("-build")]
    public void AGateIdThatIsNotALookupNameIsRejected(string id) =>
        Assert.Contains(
            "lookup name",
            Invalid($"review:\n  gates:\n    - id: {id}\n      run: [a]\n").Message,
            StringComparison.Ordinal);

    [Fact]
    public void AGateWithNoCommandIsRejected() =>
        Assert.Contains(
            "no run command",
            Invalid("review:\n  gates:\n    - id: build\n      run: []\n").Message,
            StringComparison.Ordinal);

    /// <summary>
    /// A suppression nobody can evaluate later is a permanent one, and a permanent
    /// suppression is how a review system quietly stops reviewing.
    /// </summary>
    [Fact]
    public void ASuppressionWithoutAReasonIsRejected() =>
        Assert.Contains(
            "no reason",
            Invalid("review:\n  policy:\n    suppressions:\n      - id: a/b\n        expires: 2026-11-18\n").Message,
            StringComparison.Ordinal);

    [Fact]
    public void ASuppressionWithoutAnExpiryIsRejected() =>
        Assert.Contains(
            "do not last forever",
            Invalid("review:\n  policy:\n    suppressions:\n      - id: a/b\n        reason: Generated code.\n").Message,
            StringComparison.Ordinal);

    [Fact]
    public void PolicyValuesAreReadAndBoundsChecked()
    {
        ReviewConfig config = Load("""
            review:
              coverage:
                file-line-percent: 85
                class-line-percent: 80
              policy:
                always-human:
                  - "**/auth/**"
                max-reviewable-lines: 4000
                major-count-blocks: 2
                min-confidence: 8
                suppressions:
                  - id: correctness/generated
                    reason: Regenerated client.
                    expires: 2026-11-18
            """);

        Assert.Equal(["**/auth/**"], config.Policy.AlwaysHuman);
        Assert.Equal(4000, config.Policy.MaxReviewableLines);
        Assert.Equal(2, config.Policy.MajorCountBlocks);
        Assert.Equal(8, config.Policy.MinConfidence);
        Assert.True(config.Coverage.IsDeclared);
        Assert.Equal(new DateOnly(2026, 11, 18), Assert.Single(config.Policy.Suppressions).Expires);
    }

    [Theory]
    [InlineData("min-confidence: 0", "min-confidence")]
    [InlineData("min-confidence: 11", "min-confidence")]
    [InlineData("max-reviewable-lines: 0", "max-reviewable-lines")]
    [InlineData("major-count-blocks: 0", "major-count-blocks")]
    public void OutOfRangePolicyValuesAreRejected(string line, string expected) =>
        Assert.Contains(expected, Invalid($"review:\n  policy:\n    {line}\n").Message, StringComparison.Ordinal);

    [Fact]
    public void CoverageOutsideZeroToOneHundredIsRejected() =>
        Assert.Contains(
            "between 0 and 100",
            Invalid("review:\n  coverage:\n    file-line-percent: 101\n").Message,
            StringComparison.Ordinal);

    /// <summary>
    /// This repository runs its own review through this configuration, so the checked-in file
    /// has to actually parse — a broken one would otherwise only be discovered by a reviewer
    /// mid-review.
    /// </summary>
    [Fact]
    public void ThisRepositorysOwnReviewConfigurationLoadsAndReservesItsGovernanceArtifacts()
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(KyberWeaveTestPaths.ToolRoot);

        Assert.True(result.Success, result.Error);
        ReviewConfig review = result.Config!.Review;

        Assert.Contains(review.Gates, g => g.Id == "build");
        Assert.Contains(review.Gates, g => g.Id == "test");
        Assert.All(review.Gates, g => Assert.NotEmpty(g.Run));

        Assert.True(
            PathGlob.FirstMatch(review.Policy.AlwaysHuman, "products/kyber-squad/profiles/capabilities.yml") is not null,
            "The capability profiles decide what every agent may do and must never be approvable by an agent.");
        Assert.True(
            PathGlob.FirstMatch(review.Policy.AlwaysHuman, "products/kyber-squad/agents/code-reviewer.md") is not null,
            "Instruction surfaces must escalate to a human.");
    }
}
