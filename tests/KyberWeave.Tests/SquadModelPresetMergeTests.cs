using System.Text;
using System.Text.Json;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for Squad model preset merging (<see cref="SquadModelPreset.ApplyTo" />),
///     preset validation (<see cref="SquadModelPresetValidator.Validate" />),
///     model token resolution (<see cref="SquadModelResolution.ResolveToken" />),
///     and agent model diffing (<see cref="SquadModelDiff.Compute" />).
/// </summary>
public sealed class SquadModelPresetMergeTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    [Fact]
    public void ApplyToPreservesBundledCategoriesWhenPresetDoesNotMentionThem()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelProfiles bundled = source.ModelProfiles;

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      claude: claude-3-7-sonnet
                                  """;
        SquadModelPreset preset =
            SquadSourceLoader.ParseModelPreset("test-preset.yml", Encoding.UTF8.GetBytes(presetYaml));

        SquadModelProfiles merged = preset.ApplyTo(bundled);

        Assert.NotNull(merged);
        Assert.Equal(bundled.Schema, merged.Schema);
        Assert.True(merged.Profiles.ContainsKey("architect"), "Category 'architect' should be preserved.");
        Assert.Equal(bundled.Profiles["architect"], merged.Profiles["architect"]);

        Assert.True(merged.Profiles.ContainsKey("deep-planning"), "Category 'deep-planning' should be preserved.");
        Assert.Equal(bundled.Profiles["deep-planning"], merged.Profiles["deep-planning"]);

        Assert.True(merged.Profiles.ContainsKey("fast"), "Category 'fast' should be preserved.");
        Assert.Equal(bundled.Profiles["fast"], merged.Profiles["fast"]);

        Assert.True(merged.Profiles.ContainsKey("orchestration"), "Category 'orchestration' should be preserved.");
        Assert.Equal(bundled.Profiles["orchestration"], merged.Profiles["orchestration"]);

        Assert.True(merged.Profiles.ContainsKey("reviewer"), "Category 'reviewer' should be preserved.");
        Assert.Equal(bundled.Profiles["reviewer"], merged.Profiles["reviewer"]);
    }

    [Fact]
    public void ApplyToOverridesSpecifiedCategoryDefaultAndHarnessKeys()
    {
        SquadModelProfile bundledGeneral = new(
            "inherit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "claude-sonnet-5-5",
                ["copilot"] = "Grok 4.7 (copilot)",
                ["cursor"] = "grok-4.7[]"
            });

        SquadModelProfiles bundled = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["general"] = bundledGeneral
            },
            "profiles/models.yml");

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      default: gpt-4o
                                      claude: claude-3-7-sonnet
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset("preset.yml", Encoding.UTF8.GetBytes(presetYaml));

        SquadModelProfiles merged = preset.ApplyTo(bundled);

        Assert.True(merged.Profiles.TryGetValue("general", out SquadModelProfile? mergedGeneral));
        Assert.NotNull(mergedGeneral);
        Assert.Equal("gpt-4o", mergedGeneral.Default);
        Assert.Equal("claude-3-7-sonnet", mergedGeneral.HarnessModels["claude"]);
        Assert.Equal("Grok 4.7 (copilot)", mergedGeneral.HarnessModels["copilot"]);
        Assert.Equal("grok-4.7[]", mergedGeneral.HarnessModels["cursor"]);
    }

    [Fact]
    public void ApplyToPreservesBundledCategoryDefaultWhenPresetOmitsDefault()
    {
        SquadModelProfile bundledFast = new(
            "inherit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "claude-haiku-5-5",
                ["copilot"] = "MAI-Code-1.1-Flash (copilot)"
            });

        SquadModelProfiles bundled = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["fast"] = bundledFast
            },
            "profiles/models.yml");

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    fast:
                                      claude: custom-haiku
                                  """;
        SquadModelPreset preset =
            SquadSourceLoader.ParseModelPreset("no-default.yml", Encoding.UTF8.GetBytes(presetYaml));

        SquadModelProfiles merged = preset.ApplyTo(bundled);

        Assert.True(merged.Profiles.TryGetValue("fast", out SquadModelProfile? mergedFast));
        Assert.NotNull(mergedFast);
        Assert.Equal("inherit", mergedFast.Default);
        Assert.Equal("custom-haiku", mergedFast.HarnessModels["claude"]);
        Assert.Equal("MAI-Code-1.1-Flash (copilot)", mergedFast.HarnessModels["copilot"]);
    }

    [Fact]
    public void ApplyToWhenBothPresetAndBundledProfileHaveNullDefaultThrowsInvalidOperationException()
    {
        SquadModelProfile bundledFast = new(
            null!,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "claude-haiku-5-5"
            });

        SquadModelProfiles bundled = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["fast"] = bundledFast
            },
            "profiles/models.yml");

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    fast:
                                      copilot: inherit
                                  """;
        SquadModelPreset preset =
            SquadSourceLoader.ParseModelPreset("no-default.yml", Encoding.UTF8.GetBytes(presetYaml));

        Assert.Throws<InvalidOperationException>(() => preset.ApplyTo(bundled));
    }

    [Fact]
    public void ApplyToPresetDefaultReachesOnlyHarnessesNeitherFileNames()
    {
        SquadModelProfile bundledProfile = new(
            "bundled-default",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["copilot"] = "bundled-copilot"
            });

        SquadModelProfiles bundled = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["planning"] = bundledProfile
            },
            "profiles/models.yml");

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    planning:
                                      default: preset-default
                                      claude: preset-claude
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset("rules.yml", Encoding.UTF8.GetBytes(presetYaml));

        SquadModelProfiles merged = preset.ApplyTo(bundled);

        // copilot was named by bundled -> bundled copilot preserved, preset default must not overwrite it
        string? resolvedCopilot = SquadModelResolution.ResolveToken(merged, "planning", "copilot");
        Assert.Equal("bundled-copilot", resolvedCopilot);

        // claude was named by preset -> preset claude used
        string? resolvedClaude = SquadModelResolution.ResolveToken(merged, "planning", "claude");
        Assert.Equal("preset-claude", resolvedClaude);

        // cursor was named by neither file -> preset default takes effect
        string? resolvedCursor = SquadModelResolution.ResolveToken(merged, "planning", "cursor");
        Assert.Equal("preset-default", resolvedCursor);
    }

    [Fact]
    public void ValidateAcceptsPresetWhenAllCategoriesAreDeclaredInBundledProfiles()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelProfiles bundled = source.ModelProfiles;

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      claude: claude-3-7-sonnet
                                    fast:
                                      copilot: inherit
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset("valid.yml", Encoding.UTF8.GetBytes(presetYaml));

        SquadModelPresetValidator.Validate(preset, bundled);
    }

    [Fact]
    public void ValidateThrowsSquadSourceValidationExceptionNamingUnknownCategoryKeyAndFile()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelProfiles bundled = source.ModelProfiles;

        const string relativePath = "presets/unknown-category.yml";
        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    nonexistent-category:
                                      default: inherit
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(relativePath, Encoding.UTF8.GetBytes(presetYaml));

        SquadSourceValidationException exception =
            Assert.Throws<SquadSourceValidationException>(() => SquadModelPresetValidator.Validate(preset, bundled));

        Diagnostic diagnostic = Assert.Single(exception.Diagnostics.Items);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.True(
            diagnostic.Subject.Contains("profiles.nonexistent-category", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("profiles.nonexistent-category", StringComparison.Ordinal),
            $"Diagnostic message or subject must name 'profiles.nonexistent-category'. Actual message: '{diagnostic.Message}', subject: '{diagnostic.Subject}'.");
        Assert.Equal(relativePath, diagnostic.FilePath);
        Assert.NotNull(diagnostic.StartLine);
    }

    [Fact]
    public void ValidateThrowsWhenBundledSourceHasExpectedCategoryRemoved()
    {
        SquadModelProfiles bundledWithoutFast = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["general"] = new("inherit", new Dictionary<string, string>(StringComparer.Ordinal))
            },
            "profiles/models.yml");

        const string relativePath = "presets/needs-fast.yml";
        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    fast:
                                      claude: claude-haiku-5-5
                                  """;
        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(relativePath, Encoding.UTF8.GetBytes(presetYaml));

        SquadSourceValidationException exception =
            Assert.Throws<SquadSourceValidationException>(() =>
                SquadModelPresetValidator.Validate(preset, bundledWithoutFast));

        Diagnostic diagnostic = Assert.Single(exception.Diagnostics.Items);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.True(
            diagnostic.Subject.Contains("profiles.fast", StringComparison.Ordinal) ||
            diagnostic.Message.Contains("profiles.fast", StringComparison.Ordinal),
            $"Diagnostic message or subject must name 'profiles.fast'. Actual message: '{diagnostic.Message}', subject: '{diagnostic.Subject}'.");
    }

    [Fact]
    public void ValidateOverloadAcceptingSquadSourceValidatesAgainstModelProfiles()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        const string validYaml = """
                                 schema: kyber-squad.model-preset/v1
                                 profiles:
                                   architect:
                                     claude: opus
                                   reviewer:
                                     claude: sonnet
                                 """;
        SquadModelPreset validPreset =
            SquadSourceLoader.ParseModelPreset("valid-source.yml", Encoding.UTF8.GetBytes(validYaml));
        SquadModelPresetValidator.Validate(validPreset, source);

        const string invalidYaml = """
                                   schema: kyber-squad.model-preset/v1
                                   profiles:
                                     invalid-profile-name:
                                       default: inherit
                                   """;
        SquadModelPreset invalidPreset =
            SquadSourceLoader.ParseModelPreset("invalid-source.yml", Encoding.UTF8.GetBytes(invalidYaml));
        Assert.Throws<SquadSourceValidationException>(() => SquadModelPresetValidator.Validate(invalidPreset, source));
    }

    [Fact]
    public void ResolveTokenReturnsTargetSpecificModelWhenPresent()
    {
        SquadModelProfile profile = new(
            "default-model",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "claude-sonnet-5-5",
                ["copilot"] = "gpt-4o"
            });

        SquadModelProfiles profiles = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["general"] = profile
            },
            "models.yml");

        string? resolved = SquadModelResolution.ResolveToken(profiles, "general", "claude");

        Assert.Equal("claude-sonnet-5-5", resolved);
    }

    [Fact]
    public void ResolveTokenFallsBackToDefaultWhenTargetHarnessNotExplicitlyNamed()
    {
        SquadModelProfile profile = new(
            "default-model",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "claude-sonnet-5-5"
            });

        SquadModelProfiles profiles = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["general"] = profile
            },
            "models.yml");

        string? resolved = SquadModelResolution.ResolveToken(profiles, "general", "cursor");

        Assert.Equal("default-model", resolved);
    }

    [Fact]
    public void ResolveTokenReturnsNullWhenTargetHarnessSpecifiesInherit()
    {
        SquadModelProfile profile = new(
            "gpt-4o",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["devin"] = "inherit"
            });

        SquadModelProfiles profiles = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["orchestration"] = profile
            },
            "models.yml");

        string? resolved = SquadModelResolution.ResolveToken(profiles, "orchestration", "devin");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveTokenReturnsNullWhenDefaultIsInheritAndTargetHarnessNotNamed()
    {
        SquadModelProfile profile = new(
            "inherit",
            new Dictionary<string, string>(StringComparer.Ordinal));

        SquadModelProfiles profiles = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["orchestration"] = profile
            },
            "models.yml");

        string? resolved = SquadModelResolution.ResolveToken(profiles, "orchestration", "cursor");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveTokenReturnsCustomModelWhenTargetHarnessOverridesInheritDefault()
    {
        SquadModelProfile profile = new(
            "inherit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude"] = "opus"
            });

        SquadModelProfiles profiles = new(
            "kyber-squad.model-profiles/v1",
            new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal)
            {
                ["architect"] = profile
            },
            "models.yml");

        string? resolved = SquadModelResolution.ResolveToken(profiles, "architect", "claude");

        Assert.Equal("opus", resolved);
    }

    [Fact]
    public void ComputeReturnsEmptyDiffWhenProfilesAreIdentical()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelProfiles profiles = source.ModelProfiles;

        IReadOnlyList<SquadModelDiffItem> diffs = SquadModelDiff.Compute(source, "claude", profiles, profiles);

        Assert.Empty(diffs);
    }

    [Fact]
    public void ComputeReturnsDiffItemsOnlyForAgentsWhoseResolvedModelChanges()
    {
        SquadAgent agentAlpha = CreateAgent("alpha", "general");
        SquadAgent agentBeta = CreateAgent("beta", "fast");
        SquadAgent agentGamma = CreateAgent("gamma", "reviewer");

        SquadSource source = CreateSyntheticSource([agentAlpha, agentBeta, agentGamma]);

        SquadModelProfiles beforeProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["general"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-sonnet-5-5" }),
                ["fast"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-haiku-5-5" }),
                ["reviewer"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "sonnet" })
            });

        SquadModelProfiles afterProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["general"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-3-7-sonnet" }),
                ["fast"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-haiku-5-5" }),
                ["reviewer"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "sonnet" })
            });

        IReadOnlyList<SquadModelDiffItem> diffs =
            SquadModelDiff.Compute(source, "claude", beforeProfiles, afterProfiles);

        SquadModelDiffItem item = Assert.Single(diffs);
        Assert.Equal("alpha", item.Agent);
        Assert.Equal("claude-sonnet-5-5", item.OldModel);
        Assert.Equal("claude-3-7-sonnet", item.NewModel);
    }

    [Fact]
    public void ComputeOrdersDiffItemsAlphabeticallyByAgentName()
    {
        SquadAgent agentZeta = CreateAgent("zeta", "fast");
        SquadAgent agentAlpha = CreateAgent("alpha", "fast");
        SquadAgent agentMike = CreateAgent("mike", "fast");

        SquadSource source = CreateSyntheticSource([agentZeta, agentAlpha, agentMike]);

        SquadModelProfiles beforeProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["fast"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "haiku-old" })
            });

        SquadModelProfiles afterProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["fast"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "haiku-new" })
            });

        IReadOnlyList<SquadModelDiffItem> diffs =
            SquadModelDiff.Compute(source, "claude", beforeProfiles, afterProfiles);

        Assert.Equal(3, diffs.Count);
        Assert.Equal("alpha", diffs[0].Agent);
        Assert.Equal("mike", diffs[1].Agent);
        Assert.Equal("zeta", diffs[2].Agent);
    }

    [Fact]
    public void ComputeTreatsInheritAsNullInDiffItems()
    {
        SquadAgent agentToExplicit = CreateAgent("agent-to-explicit", "cat-a");
        SquadAgent agentToInherit = CreateAgent("agent-to-inherit", "cat-b");

        SquadSource source = CreateSyntheticSource([agentToExplicit, agentToInherit]);

        SquadModelProfiles beforeProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["cat-a"] = ("inherit", new Dictionary<string, string>(StringComparer.Ordinal)),
                ["cat-b"] = ("default-model",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "custom-claude" })
            });

        SquadModelProfiles afterProfiles = CreateProfiles(
            new Dictionary<string, (string Default, Dictionary<string, string> HarnessModels)>(StringComparer.Ordinal)
            {
                ["cat-a"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "new-claude" }),
                ["cat-b"] = ("inherit",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "inherit" })
            });

        IReadOnlyList<SquadModelDiffItem> diffs =
            SquadModelDiff.Compute(source, "claude", beforeProfiles, afterProfiles);

        Assert.Equal(2, diffs.Count);

        SquadModelDiffItem first = diffs[0];
        Assert.Equal("agent-to-explicit", first.Agent);
        Assert.Null(first.OldModel);
        Assert.Equal("new-claude", first.NewModel);

        SquadModelDiffItem second = diffs[1];
        Assert.Equal("agent-to-inherit", second.Agent);
        Assert.Equal("custom-claude", second.OldModel);
        Assert.Null(second.NewModel);
    }

    [Fact]
    public void ComputeDetectsExpectedDiffsAcrossAgentsAgainstCanonicalSquadSource()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadModelProfiles bundled = source.ModelProfiles;

        const string presetYaml = """
                                  schema: kyber-squad.model-preset/v1
                                  profiles:
                                    general:
                                      claude: claude-override
                                  """;
        SquadModelPreset preset =
            SquadSourceLoader.ParseModelPreset("override.yml", Encoding.UTF8.GetBytes(presetYaml));
        SquadModelProfiles merged = preset.ApplyTo(bundled);

        IReadOnlyList<SquadModelDiffItem> diffs = SquadModelDiff.Compute(source, "claude", bundled, merged);

        Assert.NotEmpty(diffs);

        List<string> expectedChangedAgentNames = source.Agents
            .Where(agent => string.Equals(agent.ModelProfile, "general", StringComparison.Ordinal))
            .Select(agent => agent.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedChangedAgentNames);

        List<string> actualChangedAgentNames = diffs.Select(diff => diff.Agent).ToList();
        Assert.Equal(expectedChangedAgentNames, actualChangedAgentNames);

        foreach (SquadModelDiffItem item in diffs)
        {
            Assert.Equal("claude-sonnet-5-5", item.OldModel);
            Assert.Equal("claude-override", item.NewModel);
        }
    }

    private static SquadAgent CreateAgent(string name, string modelProfile)
    {
        return new SquadAgent(
            "kyber-squad.agent/v1",
            name,
            $"Test agent {name}",
            SquadInvocation.Primary,
            modelProfile,
            "standard",
            null,
            [],
            [],
            [],
            null,
            [],
            "none",
            [],
            "Test instructions",
            "digest",
            $"agents/{name}.md",
            []);
    }

    private static SquadSource CreateSyntheticSource(IReadOnlyList<SquadAgent> agents)
    {
        using JsonDocument doc = JsonDocument.Parse("{}");
        JsonElement root = doc.RootElement.Clone();

        return new SquadSource(
            "synthetic",
            new SquadManifest(
                "kyber-squad.squad/v1",
                "test-squad",
                "kyber-weave-assembly",
                "default",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["default"] = "bundle.yml" },
                new SquadProfilePaths("models.yml", "caps.yml", "fallbacks.yml"),
                "toolchain.yml",
                "mcp.json",
                "squad.yml"),
            new SquadBundle(
                "kyber-squad.bundle/v1",
                "default",
                agents.Select(a => a.Name).ToList(),
                [],
                "bundle.yml"),
            agents,
            [],
            new SquadModelProfiles(
                "kyber-squad.model-profiles/v1",
                new Dictionary<string, SquadModelProfile>(StringComparer.Ordinal),
                "models.yml"),
            new SquadCapabilityProfiles(
                "kyber-squad.capability-profiles/v1",
                [],
                new Dictionary<string, SquadCapabilityProfile>(StringComparer.Ordinal),
                "caps.yml"),
            new SquadFallbackProfiles(
                "kyber-squad.fallback-profiles/v1",
                new Dictionary<string, SquadFallbackProfile>(StringComparer.Ordinal),
                "fallbacks.yml"),
            new SquadToolchain(
                "kyber-squad.toolchain/v1",
                [],
                null,
                "toolchain.yml",
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)),
            root);
    }

    private static SquadModelProfiles CreateProfiles(
        IReadOnlyDictionary<string, (string Default, Dictionary<string, string> HarnessModels)> entries)
    {
        Dictionary<string, SquadModelProfile> map = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, (string Default, Dictionary<string, string> HarnessModels)> entry in entries)
            map[entry.Key] = new SquadModelProfile(entry.Value.Default, entry.Value.HarnessModels);

        return new SquadModelProfiles("kyber-squad.model-profiles/v1", map, "models.yml");
    }
}
