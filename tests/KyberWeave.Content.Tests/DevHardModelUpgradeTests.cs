using KyberWeave.Testing.Squad;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A task that fails on <c>dev-medium</c> steps up to <c>dev-hard</c>, so <c>dev-hard</c> must run a
/// different model from <c>dev-medium</c>'s <c>general</c> profile on every harness the matrix can
/// express. <c>dev-hard</c> carries the model the <c>architect</c> profile pinned on the same harness
/// when the tier became a real upgrade, while <c>dev-easy</c> and <c>dev-medium</c> keep the models
/// they had.
/// </summary>
public sealed class DevHardModelUpgradeTests
{
    private static SquadSource Source => CanonicalSquadTree.SharedSource;

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

    /// <summary>
    /// The <c>dev-hard</c> rows as chosen when the tier became a real upgrade: the model
    /// <c>architect</c> pinned on that harness at that moment. The table is a snapshot, not a live
    /// link to <c>architect</c>: <c>dev-hard</c> owns these rows, so a later planner rotation
    /// (Requirement 1.12) neither changes them nor fails this test.
    /// </summary>
    private static readonly Dictionary<string, string> DevHardModels = new(StringComparer.Ordinal)
    {
        ["antigravity"] = "pro",
        ["claude"] = "opus",
        ["codex"] = "gpt-6.1-sol",
        ["copilot"] = "Gemini 4 Hydrogen (copilot)",
        ["cursor"] = "claude-sonnet-5-5[effort=high]",
        ["devin"] = "claude-opus-5-5-high",
        ["factory"] = "glm-5.3[reasoningEffort=high]",
        ["kilo"] = "zai-coding-plan/glm-5.3",
        ["opencode"] = "zai-coding-plan/glm-5.3",
        ["pi"] = "zai/glm-5.3[thinking=high]",
        ["zcode"] = "glm-5.3",
    };

    /// <summary>The <c>fast</c> rows <c>dev-easy</c> runs on, frozen so a change is visible.</summary>
    private static readonly Dictionary<string, string> DevEasyModels = new(StringComparer.Ordinal)
    {
        ["antigravity"] = "flash",
        ["claude"] = "claude-haiku-5-5",
        ["codex"] = "gpt-6-luna",
        ["copilot"] = "Claude Haiku 5.5 (copilot)",
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

    /// <summary>
    /// The model each harness actually emits for <c>dev-hard</c> after its renderer has lowered the
    /// profile — the level the step-up runs at. Warp has no agent file, and Command Code emits no
    /// model key because no profile names a <c>commandcode</c> row.
    /// </summary>
    private static readonly Dictionary<SquadTarget, string> RenderedDevHardModels = new()
    {
        [SquadTarget.Claude] = "opus",
        [SquadTarget.Copilot] = "Gemini 4 Hydrogen (copilot)",
        [SquadTarget.Cursor] = "claude-sonnet-5-5[effort=high]",
        [SquadTarget.Codex] = "gpt-6.1-sol",
        [SquadTarget.OpenCode] = "zai-coding-plan/glm-5.3",
        [SquadTarget.Kilo] = "zai-coding-plan/glm-5.3",
        [SquadTarget.Antigravity] = "pro",
        [SquadTarget.Factory] = "glm-5.3",
        [SquadTarget.Pi] = "zai/glm-5.3",
        [SquadTarget.ZCode] = "glm-5.3",
        [SquadTarget.Devin] = "claude-opus-5-5-high",
    };

    [Fact]
    public void DevHardResolvesToADifferentModelFromGeneralOnEveryOtherHarness()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.ModelProfiles.Profiles;
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
    public void DevHardKeepsTheModelChosenForItOnEveryHarness()
    {
        SquadModelProfile devHard = Source.ModelProfiles.Profiles["dev-hard"];

        Assert.Equal("inherit", devHard.Default);
        Assert.Equal(Sort(DevHardModels), Sort(devHard.HarnessModels));
    }

    [Fact]
    public async Task DevHardRendersADifferentModelFromDevMediumOnEveryHarnessThatEmitsOne()
    {
        foreach ((SquadTarget target, string expected) in RenderedDevHardModels)
        {
            SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(target);
            Assert.True(render.Success, string.Join("; ", render.Errors));

            string hard = RenderedModel(render, target, "dev-hard");
            string medium = RenderedModel(render, target, "dev-medium");

            Assert.Equal(expected, hard);
            Assert.False(
                string.Equals(medium, hard, StringComparison.Ordinal),
                $"'{SquadTargetCatalog.GetToken(target)}' renders both dev-hard and dev-medium on '{hard}', " +
                "so the step-up changes no runtime model even though models.yml differs.");
        }

        Assert.Equal(
            SquadTargetCatalog.All.Count - NoSecondModel.Count,
            RenderedDevHardModels.Count);
    }

    [Fact]
    public void DevEasyAndDevMediumKeepTheirModels()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.ModelProfiles.Profiles;

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

    /// <summary>The rendered model value the harness emits for <paramref name="agent"/>.</summary>
    private static string RenderedModel(SquadRenderResult render, SquadTarget target, string agent)
    {
        string path = AgentFilePath(target, agent);
        SquadDeploymentFile file = Assert.Single(render.Files, candidate => candidate.RelativePath == path);
        string text = Encoding.UTF8.GetString(file.Content.Span);

        // Codex is the one TOML agent; every other format puts the model in a YAML frontmatter block.
        IEnumerable<string> lines = target == SquadTarget.Codex
            ? text.Split('\n')
            : text.Split('\n').Skip(1).TakeWhile(line => line.Trim() != "---");

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOfAny([':', '=']);
            if (separator <= 0 ||
                !string.Equals(trimmed[..separator].Trim().Trim('"', '\''), "model", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed[(separator + 1)..].Trim().Trim('"', '\'').TrimEnd(',');
        }

        Assert.Fail($"'{path}' emits no model key for {agent} on '{SquadTargetCatalog.GetToken(target)}'.");
        return string.Empty;
    }

    private static string AgentFilePath(SquadTarget target, string agent) => target switch
    {
        SquadTarget.Claude => $".claude/agents/{agent}.md",
        SquadTarget.Copilot => $".github/agents/{agent}.agent.md",
        SquadTarget.Cursor => $".cursor/agents/{agent}.md",
        SquadTarget.Codex => $".codex/agents/{agent}.toml",
        SquadTarget.OpenCode => $".opencode/agents/{agent}.md",
        SquadTarget.Kilo => $".kilo/agents/{agent}.md",
        SquadTarget.Antigravity => $".agents/agents/{agent}/agent.md",
        SquadTarget.Factory => $".factory/droids/{agent}.md",
        SquadTarget.Pi => $".pi/agents/{agent}.md",
        SquadTarget.ZCode => $".zcode/agents/{agent}.md",
        SquadTarget.Devin => $".devin/agents/{agent}/AGENT.md",
        _ => throw new ArgumentOutOfRangeException(
            nameof(target),
            target,
            "This harness emits no per-agent model."),
    };

    private static KeyValuePair<string, string>[] Sort(IReadOnlyDictionary<string, string> values) =>
        [.. values.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
}
