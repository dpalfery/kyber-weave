using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>dev-hard</c> is a separate model profile with a row for every harness, and the conductor has no
/// model profile at all: it renders no model on any harness and runs on the model its session started with.
/// </summary>
public sealed class ModelProfileOptionalTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<SquadSource> Source = new(() => SquadSourceLoader.Load(ProductRoot));

    private static readonly Regex ModelKey = new(
        @"^\s*""?model""?\s*[:=]", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void DevHardExistsAndOrchestrationIsGone()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.Value.ModelProfiles.Profiles;

        Assert.True(profiles.ContainsKey("dev-hard"));
        Assert.False(profiles.ContainsKey("orchestration"));
    }

    [Fact]
    public void DevHardHasARowForEveryHarnessAndSharesNoRowWithThePlanningProfiles()
    {
        IReadOnlyDictionary<string, SquadModelProfile> profiles = Source.Value.ModelProfiles.Profiles;
        SquadModelProfile devHard = profiles["dev-hard"];
        SquadModelProfile architect = profiles["architect"];
        SquadModelProfile deepPlanning = profiles["deep-planning"];

        Assert.Equal(architect.HarnessModels.Keys.Order(StringComparer.Ordinal), devHard.HarnessModels.Keys.Order(StringComparer.Ordinal));
        foreach ((string harness, string value) in devHard.HarnessModels)
        {
            Assert.NotEqual(architect.HarnessModels[harness], value);
            Assert.NotEqual(deepPlanning.HarnessModels[harness], value);
        }
    }

    [Fact]
    public void TheConductorHasNoModelProfileAndEveryOtherAgentKeepsItsOwn()
    {
        SquadSource source = Source.Value;
        SquadAgent conductor = source.Agents.Single(agent => agent.Name == "conductor");

        Assert.True(string.IsNullOrWhiteSpace(conductor.ModelProfile));
        foreach (SquadAgent agent in source.Agents.Where(agent => agent.Name != "conductor"))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(agent.ModelProfile),
                $"Agent '{agent.Name}' must still name its model profile.");
            Assert.True(
                source.ModelProfiles.Profiles.ContainsKey(agent.ModelProfile),
                $"Agent '{agent.Name}' names unknown profile '{agent.ModelProfile}'.");
        }
    }

    [Fact]
    public void TheConductorResolvesNoModelOnAnyHarness()
    {
        SquadSource source = Source.Value;
        SquadAgent conductor = source.Agents.Single(agent => agent.Name == "conductor");

        foreach (string harness in conductor.ModelProfile.Length == 0
                     ? new[] { "claude", "codex", "copilot", "cursor", "devin", "factory", "kilo", "opencode", "pi", "zcode", "antigravity" }
                     : [])
        {
            Assert.Null(SquadHarnessLowering.ResolveModel(
                conductor, source.ModelProfiles.Profiles, harness, SquadModelPolicy.InheritOmitted));
        }
    }

    [Fact]
    public async Task TheRenderedConductorFileNamesNoModelOnAnyHarness()
    {
        foreach (SquadTarget target in Enum.GetValues<SquadTarget>())
        {
            SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(target);
            SquadDeploymentFile? conductor = render.Files.FirstOrDefault(file =>
                file.RelativePath.Contains("conductor", StringComparison.OrdinalIgnoreCase));
            if (conductor is null)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(conductor.Content.Span);
            Assert.DoesNotMatch(ModelKey, text);
        }
    }
}
