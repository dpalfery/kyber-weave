using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A task that fails on <c>dev-medium</c> steps up to <c>dev-hard</c>, so <c>dev-hard</c> must run a
/// stronger model than <c>dev-medium</c>'s <c>general</c> profile on every harness the matrix can
/// express. <c>dev-hard</c> carries the model the <c>architect</c> profile pins on the same harness —
/// that profile's strongest coding model today — while <c>dev-easy</c> and <c>dev-medium</c> keep the
/// models they had.
/// </summary>
public sealed class DevHardModelUpgradeTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<SquadSource> Source = new(() => SquadSourceLoader.Load(ProductRoot));

    /// <summary>
    /// The harnesses where both developer tiers resolve to the same value because the matrix offers
    /// no second model there, with the reason each is exempt. Every other harness must differ.
    /// </summary>
    private static readonly Dictionary<SquadTarget, string> NoSecondModel = new()
    {
        [SquadTarget.Warp] =
            "Warp has no agent primitive: it lowers agents to role skills and renders no model key.",
        [SquadTarget.CommandCode] =
            "models.yml names no commandcode column for any profile, so both tiers take " +
            "'default: inherit' and the harness chooses the model.",
    };

    /// <summary>The <c>fast</c> rows <c>dev-easy</c> runs on, frozen so a change is visible.</summary>
    private static readonly Dictionary<string, string> DevEasyModels = new(StringComparer.Ordinal)
    {
        ["antigravity"] = "flash",
        ["claude"] = "claude-haiku-5-5",
        ["codex"] = "gpt-6-luna",
        ["copilot"] = "GPT-6.1 Terra (copilot)",
        ["cursor"] = "composer-2.5[]",
        ["devin"] = "deepseek-v4-1-flash-high",
        ["factory"] = "deepseek-v4.1-flash[reasoningEffort=high]",
        ["kilo"] = "opencode-go/muse-spark-1.3-contributor",
        ["opencode"] = "opencode-go/muse-spark-1.3-contributor",
        ["pi"] = "opencode-go/muse-spark-1.3-contributor[thinking=low]",
        ["zcode"] = "glm-5.3-flash",
    };

    /// <summary>The <c>general</c> rows <c>dev-medium</c> runs on, frozen so a change is visible.</summary>
    private static readonly Dictionary<string, string> DevMediumModels = new(StringComparer.Ordinal)
    {
        ["antigravity"] = "flash",
        ["claude"] = "claude-sonnet-5-5",
        ["codex"] = "gpt-6.1-terra",
        ["copilot"] = "GPT-6.1 Terra (copilot)",
        ["cursor"] = "grok-4.7[]",
        ["devin"] = "swe-2-high",
        ["factory"] = "deepseek-v4.1-flash[reasoningEffort=high]",
        ["kilo"] = "opencode-go/muse-spark-1.3-contributor",
        ["opencode"] = "opencode-go/muse-spark-1.3-contributor",
        ["pi"] = "opencode-go/muse-spark-1.3-contributor[thinking=medium]",
        ["zcode"] = "glm-5.3-flash",
    };

    [Fact]
    public void DevHardResolvesToADifferentModelFromGeneralOnEveryOtherHarness()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.Value.ModelProfiles.Profiles;
        SquadModelProfile devHard = profiles["dev-hard"];
        SquadModelProfile general = profiles["general"];

        List<string> compared = [];
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string harness = SquadTargetCatalog.GetToken(target);
            string hardModel = Resolve(devHard, harness);
            string generalModel = Resolve(general, harness);

            if (NoSecondModel.TryGetValue(target, out string? reason))
            {
                Assert.True(
                    string.Equals(hardModel, generalModel, StringComparison.Ordinal),
                    $"'{harness}' is exempt from the step-up check because {reason} " +
                    $"But dev-hard resolves to '{hardModel}' and general to '{generalModel}'.");
                continue;
            }

            compared.Add(harness);
            Assert.False(
                string.Equals(hardModel, generalModel, StringComparison.Ordinal),
                $"dev-hard must not run the same model as dev-medium's general profile on '{harness}': " +
                $"both resolve to '{hardModel}', so a failed task's step-up changes no model.");
        }

        Assert.Equal(SquadTargetCatalog.All.Count, compared.Count + NoSecondModel.Count);
    }

    [Fact]
    public void DevHardUsesTheArchitectProfilesModelOnEveryHarnessTheMatrixNames()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.Value.ModelProfiles.Profiles;
        SquadModelProfile devHard = profiles["dev-hard"];
        SquadModelProfile architect = profiles["architect"];

        Assert.Equal(architect.Default, devHard.Default);
        Assert.Equal(
            Sort(architect.HarnessModels),
            Sort(devHard.HarnessModels));
    }

    [Fact]
    public void DevEasyAndDevMediumKeepTheirModels()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.Value.ModelProfiles.Profiles;

        AssertProfileUnchanged(profiles["fast"], DevEasyModels);
        AssertProfileUnchanged(profiles["general"], DevMediumModels);
    }

    private static void AssertProfileUnchanged(
        SquadModelProfile profile,
        IReadOnlyDictionary<string, string> expected)
    {
        Assert.Equal("inherit", profile.Default);
        Assert.Equal(Sort(expected), Sort(profile.HarnessModels));
    }

    private static string Resolve(SquadModelProfile profile, string harness) =>
        profile.HarnessModels.TryGetValue(harness, out string? value) ? value : profile.Default;

    private static KeyValuePair<string, string>[] Sort(IReadOnlyDictionary<string, string> values) =>
        [.. values.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
}
