using System.Text.Json;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What the shipped <c>products/kyber-squad</c> source declares for model profiles and the agents that use them. The
/// subject is the shipped content, loaded from a copy of it held in memory, so these pins cannot be faked.
/// </summary>
public sealed class SquadShippedModelProfileContentTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// Pins the operator-selected model assignments across planning, coding, review, and
    /// profiles that do not change, including unchanged harness values that must remain stable.
    /// </summary>
    [Theory]
    [InlineData("architect", "claude", "opus")]
    [InlineData("architect", "codex", "gpt-6.2-sol")]
    [InlineData("architect", "copilot", "GPT-6.2 Sol (copilot)")]
    [InlineData("architect", "cursor", "claude-sonnet-5-5[effort=high]")]
    [InlineData("architect", "kilo", "zai-coding-plan/glm-5.3")]
    [InlineData("architect", "opencode", "zai-coding-plan/glm-5.4")]
    [InlineData("architect", "pi", "zai/glm-5.3[thinking=high]")]
    [InlineData("architect", "devin", "claude-opus-5-5-high")]
    [InlineData("deep-planning", "claude", "opus")]
    [InlineData("deep-planning", "codex", "gpt-6.2-sol")]
    [InlineData("deep-planning", "copilot", "GPT-6.2 Sol (copilot)")]
    [InlineData("deep-planning", "cursor", "claude-sonnet-5-5[effort=high]")]
    [InlineData("deep-planning", "kilo", "zai-coding-plan/glm-5.3")]
    [InlineData("deep-planning", "opencode", "zai-coding-plan/glm-5.4")]
    [InlineData("deep-planning", "pi", "zai/glm-5.3[thinking=high]")]
    [InlineData("deep-planning", "devin", "claude-opus-5-5-high")]
    [InlineData("fast", "claude", "claude-haiku-5-5")]
    [InlineData("fast", "codex", "gpt-6-luna")]
    [InlineData("fast", "copilot", "MAI Code 2 Flash (copilot)")]
    [InlineData("fast", "cursor", "composer-2.5[]")]
    [InlineData("fast", "kilo", "opencode-go/muse-spark-1.3-contributor")]
    [InlineData("fast", "opencode", "opencode-go/deepseek-v4.1-flash-1017")]
    [InlineData("fast", "pi", "opencode-go/muse-spark-1.3-contributor[thinking=low]")]
    [InlineData("fast", "devin", "deepseek-v4-1-flash-high")]
    [InlineData("general", "claude", "claude-sonnet-5-5")]
    [InlineData("general", "codex", "gpt-6.1-terra")]
    [InlineData("general", "copilot", "MAI Code 2 (copilot)")]
    [InlineData("general", "cursor", "grok-4.7[]")]
    [InlineData("general", "kilo", "opencode-go/muse-spark-1.3-contributor")]
    [InlineData("general", "opencode", "opencode-go/deepseek-v4.1-flash-1017")]
    [InlineData("general", "pi", "opencode-go/muse-spark-1.3-contributor[thinking=medium]")]
    [InlineData("general", "devin", "swe-2-high")]
    [InlineData("reviewer", "claude", "sonnet")]
    [InlineData("reviewer", "codex", "gpt-6.1-terra")]
    [InlineData("reviewer", "copilot", "GPT-6.1 Terra (copilot)")]
    [InlineData("reviewer", "cursor", "kimi-k2.7-code[]")]
    [InlineData("reviewer", "kilo", "opencode-go/kimi-k2.7-code")]
    [InlineData("reviewer", "opencode", "opencode-go/kimi-k2.7-code")]
    [InlineData("reviewer", "pi", "opencode-go/kimi-k2.7-code[thinking=high]")]
    [InlineData("reviewer", "devin", "grok-4-7-high")]
    [InlineData("dev-hard", "claude", "opus")]
    [InlineData("dev-hard", "codex", "gpt-6.1-sol")]
    [InlineData("dev-hard", "copilot", "Gemini 4 Hydrogen (copilot)")]
    [InlineData("dev-hard", "cursor", "claude-sonnet-5-5[effort=high]")]
    [InlineData("dev-hard", "factory", "glm-5.3[reasoningEffort=high]")]
    [InlineData("dev-hard", "kilo", "zai-coding-plan/glm-5.3")]
    [InlineData("dev-hard", "opencode", "zai-coding-plan/glm-5.3")]
    [InlineData("dev-hard", "pi", "zai/glm-5.3[thinking=high]")]
    [InlineData("dev-hard", "devin", "claude-opus-5-5-high")]
    [InlineData("dev-hard", "zcode", "glm-5.3")]
    public void ModelsYmlDeclaresExactHarnessValuesPerProfilePerPlan(string profileName, string harbessName, string expectedValue)
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        Assert.True(
            source.ModelProfiles.Profiles.ContainsKey(profileName),
            $"models.yml does not declare the '{profileName}' profile.");
        SquadModelProfile profile = source.ModelProfiles.Profiles[profileName];

        // default: inherit is always required
        Assert.True(
            profile.Default == "inherit",
            $"Profile '{profileName}' default is '{profile.Default}' instead of 'inherit'.");

        if (expectedValue == "inherit")
        {
            // The harness-model entry should be absent or resolve to inherit
            bool hasEntry = profile.HarnessModels.TryGetValue(harbessName, out string? actualValue);
            Assert.True(
                !hasEntry || actualValue == "inherit",
                $"Profile '{profileName}' harness '{harbessName}' should be absent or 'inherit', but got '{(hasEntry ? actualValue : "(absent)")}' from models.yml.");
        }
        else
        {
            // The harness-model entry must exist and match exactly
            Assert.True(
                profile.HarnessModels.TryGetValue(harbessName, out string? actualValue),
                $"Profile '{profileName}' harness '{harbessName}' is missing from models.yml.");
            Assert.True(
                actualValue == expectedValue,
                $"Profile '{profileName}' harness '{harbessName}' is '{actualValue}' but should be '{expectedValue}'.");
        }
    }

    [Fact]
    public void ModelsYmlUsesHaiku55OnlyForTheFastProfile()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        foreach ((string profileName, SquadModelProfile profile) in source.ModelProfiles.Profiles)
        {
            Assert.DoesNotContain("haiku", profile.Default, StringComparison.OrdinalIgnoreCase);
            foreach ((string harness, string model) in profile.HarnessModels)
            {
                if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(
                        (profileName == "fast" && harness == "claude" && model == "claude-haiku-5-5"),
                        $"Profile '{profileName}' on harness '{harness}' unexpectedly resolves to '{model}'.");
                }
            }
        }
    }


    /// <summary>
    /// Pins Antigravity planning, review and the hard developer tier to Pro while routine work stays on Flash.
    /// This catches unintended model changes independently of the renderer's YAML echo.
    /// </summary>
    [Theory]
    [InlineData("architect", "pro")]
    [InlineData("deep-planning", "pro")]
    [InlineData("fast", "flash")]
    [InlineData("general", "flash")]
    [InlineData("dev-hard", "pro")]
    [InlineData("reviewer", "pro")]
    public void ModelsYmlAntigravityColumnPinsPlanningReviewAndTheHardTierOnProAndOtherProfilesOnFlash(
        string profileName,
        string expectedAntigravityModel)
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        Assert.True(
            source.ModelProfiles.Profiles.ContainsKey(profileName),
            $"models.yml does not declare the '{profileName}' profile.");
        SquadModelProfile profile = source.ModelProfiles.Profiles[profileName];
        Assert.True(
            profile.HarnessModels.TryGetValue("antigravity", out string? actualValue),
            $"Profile '{profileName}' is missing its antigravity column in models.yml.");
        Assert.True(
            actualValue == expectedAntigravityModel,
            $"Profile '{profileName}' antigravity is '{actualValue}' but should be '{expectedAntigravityModel}'.");
    }

    /// <summary>
    /// Keeps the architect/product-owner profile distinct from the other planning roles even
    /// when both profiles currently select the same planning models.
    /// </summary>
    [Fact]
    public void ArchitectAndProductOwnerOwnArchitectModelProfileAndDeepPlanningPeersStayOnDeepPlanning()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");
        Assert.Equal("architect", architect.ModelProfile);

        SquadAgent productOwner = Assert.Single(source.Agents, agent => agent.Name == "product-owner");
        Assert.Equal("architect", productOwner.ModelProfile);

        SquadAgent peer = Assert.Single(source.Agents, agent => agent.Name == "bug-crusher-investigator");
        Assert.Equal("deep-planning", peer.ModelProfile);

        Assert.DoesNotContain(
            source.Agents.Where(agent => agent.Name != "architect" && agent.Name != "product-owner"),
            agent => string.Equals(agent.ModelProfile, "architect", StringComparison.Ordinal));
    }

    /// <summary>
    /// Guard against vacuous tests: reviewer must have members when it exists (plan section 7, T16 criterion 3).
    /// </summary>
    [Fact]
    public void ReviewerProfileHasNonEmptyMembership()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        Assert.True(
            source.ModelProfiles.Profiles.ContainsKey("reviewer"),
            "models.yml does not declare a 'reviewer' profile.");

        string[] reviewerAgents = ["code-reviewer", "review-lens", "review-triage"];
        int reviewerMemberCount = source.Agents.Count(agent => reviewerAgents.Contains(agent.Name));
        Assert.True(
            reviewerMemberCount > 0,
            "No agents are assigned to the 'reviewer' profile; criterion 3 model assertion would be vacuous.");
    }

    [Fact]
    public void MaiCodeFlashKeyIsRemovedFromModelsYml()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        Assert.False(
            source.ModelProfiles.Profiles.ContainsKey("mai-code-flash"),
            "models.yml still contains the 'mai-code-flash' key; it is a model name and must be removed.");
    }

    [Fact]
    public void CodeReviewerReviewLensReviewTriageDeclareReviewerProfile()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;
        string[] reviewerAgents = ["code-reviewer", "review-lens", "review-triage"];

        foreach (string agentName in reviewerAgents)
        {
            SquadAgent agent = Assert.Single(
                source.Agents,
                candidate => candidate.Name == agentName);
            Assert.True(
                agent.ModelProfile == "reviewer",
                $"Agent '{agentName}' declares 'model-profile: {agent.ModelProfile}' instead of 'reviewer'.");
        }
    }

    /// <summary>
    /// The developer agents that the tiered roster dispatches are the ones modelled for cheap work:
    /// <c>dev-easy</c> is the <c>fast</c> profile. The two removed planning and testing agents used to
    /// live here; their profiles are still declared for the roles that keep them.
    /// </summary>
    [Fact]
    public void DevEasyDeclaresModelProfileFast()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        SquadAgent devEasy = Assert.Single(
            source.Agents,
            candidate => candidate.Name == "dev-easy");
        Assert.Equal("fast", devEasy.ModelProfile);
    }

    /// <summary>
    /// The published schema is a second copy of the loader's model-profile field set. Every
    /// other declared target already has a harness-model property there; without a <c>pi</c>
    /// property, schema-aware editors reject a profile the loader accepts.
    /// </summary>
    [Fact]
    public void ModelProfilesSchemaDeclaresAPiHarnessModelField()
    {
        using JsonDocument schema = JsonDocument.Parse(
            Corpus.ReadText("products/kyber-squad/schemas/model-profiles.schema.json"));
        JsonElement harnessModelFields = schema.RootElement
            .GetProperty("properties")
            .GetProperty("profiles")
            .GetProperty("additionalProperties")
            .GetProperty("properties");

        Assert.True(
            harnessModelFields.TryGetProperty("pi", out JsonElement piField),
            "model-profiles.schema.json declares no 'pi' harness-model property.");
        Assert.Equal("string", piField.GetProperty("type").GetString());
        Assert.Equal(1, piField.GetProperty("minLength").GetInt32());
    }
}
