using KyberWeave.Testing.Squad;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The three developer agents are one body rendered three times: they differ only in name,
/// description and model profile, so choosing an agent is how a run chooses a model. The body names
/// no technology, because the task prompt carries the one technology's rules, skills and standard.
/// </summary>
public sealed class DeveloperAgentContentTests
{
    private static SquadSource Source => CanonicalSquadTree.SharedSource;

    private static readonly string[] DeveloperAgents = ["dev-easy", "dev-medium", "dev-hard"];

    private static readonly Dictionary<string, string> ExpectedModelProfiles = new(StringComparer.Ordinal)
    {
        ["dev-easy"] = "fast",
        ["dev-medium"] = "general",
        ["dev-hard"] = "dev-hard"
    };

    /// <summary>The agent roster as it stood before the developer agents were added.</summary>
    private static readonly string[] ExistingAgents =
    [
        "architect",
        "platform-researcher",
        "bug-crusher",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "dev-medium",
        "dev-medium",
        "docs-dev",
        "devops",
        "dev-medium",
        "dev-medium",
        "product-owner",
        "dev-medium",
        "dev-medium",
        "dev-medium",
        "research-agent",
        "review-lens",
        "review-triage",
        "dev-medium",
        "architect",
        "dev-medium",
        "dev-medium",
        "dev-medium"
    ];

    private static readonly Regex TechnologyWord = new(
        @"\b(csharp|c#|\.net|dotnet|typescript|javascript|python|rust|react|maui|tauri|pulumi|azure|aws|sql|java|kotlin|swift|php|ruby|npm|cargo|pytest|gradle|maven)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LanguageCommand = new(
        @"\b(dotnet|npm|cargo|pytest|mvn|gradle)\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] SkillNames =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "change-scope",
        "code-review",
        "code-review-loop",
        "create-pull-request",
        "delivery-execution",
        "dp-code-reviewer",
        "github-cli",
        "kilo-review-loop",
        "lm-studio-cli",
        "pr-review-fix-comments",
        "product-owner",
        "review-council",
        "security-review",
        "setup-dev-environment",
        "task-gate"
    ];

    private static SquadAgent Agent(string name) =>
        Assert.Single(Source.Agents, agent => agent.Name == name);

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void TheAgentUsesTheWorkerProfileAndTheSharedWorkerTools(string name)
    {
        SquadSource source = Source;
        SquadAgent agent = Agent(name);
        SquadAgent csharpDev = Agent("dev-medium");

        Assert.Equal("worker", agent.CapabilityProfile);
        Assert.Equal(csharpDev.CopilotTools, agent.CopilotTools);
        Assert.Empty(agent.DelegatesTo);
        Assert.Equal("role-skill", agent.Fallback);
        Assert.Equal(SquadInvocation.Subagent, agent.Invocation);
        Assert.True(
            source.ModelProfiles.Profiles.ContainsKey(agent.ModelProfile),
            $"Agent '{name}' names unknown model profile '{agent.ModelProfile}'.");
        Assert.Equal(ExpectedModelProfiles[name], agent.ModelProfile);
    }

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void TheAgentDeclaresTheTieredRoster(string name)
    {
        Assert.Equal("tiered", Agent(name).WorkerRoster);
    }

    [Fact]
    public void TheThreeAgentsShareOneBodyAndDifferOnlyInNameDescriptionAndModelProfile()
    {
        SquadAgent[] agents = DeveloperAgents.Select(Agent).ToArray();

        Assert.Single(agents
            .Select(agent => agent.InstructionBody.Replace(agent.Name, "<name>", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal));
        Assert.Equal(3, agents.Select(agent => agent.Description).Distinct(StringComparer.Ordinal).Count());

        SquadAgent first = agents[0];
        foreach (SquadAgent agent in agents)
        {
            Assert.Contains($"agent={agent.Name}", agent.InstructionBody, StringComparison.Ordinal);
            Assert.Equal(first.CapabilityProfile, agent.CapabilityProfile);
            Assert.Equal(first.CopilotTools, agent.CopilotTools);
            Assert.Equal(first.DelegatesTo, agent.DelegatesTo);
            Assert.Equal(first.Fallback, agent.Fallback);
            Assert.Equal(first.Aliases, agent.Aliases);
            Assert.Equal(first.WorkerRoster, agent.WorkerRoster);
        }
    }

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void TheBodyNamesNoTechnologyLibraryCommandOrSkill(string name)
    {
        string body = Agent(name).InstructionBody;

        Assert.DoesNotMatch(TechnologyWord, body);
        Assert.DoesNotMatch(LanguageCommand, body);
        foreach (string skill in SkillNames)
        {
            Assert.DoesNotContain(skill, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void TheBodyDoesNotFetchTheStandardAsItsFirstStep(string name)
    {
        string body = Agent(name).InstructionBody;

        Assert.DoesNotContain("-coding-standard", body, StringComparison.Ordinal);
        Assert.Contains("If `<left-out>` lists a section you need", body, StringComparison.Ordinal);
        Assert.Contains("docs_get_coding_standard_for_technology", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    public void TheBodyTellsTheAgentWhatToDoWhenItsPromptHoldsOnlyATaskId(string name)
    {
        string body = Agent(name).InstructionBody;

        Assert.Contains("If your prompt is only a task id", body, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs packet <task id>", body, StringComparison.Ordinal);
        Assert.Contains("Never work from a task id alone", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAgentThatExistedBeforeIsStillPresent()
    {
        HashSet<string> present = Source.Agents
            .Select(agent => agent.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string name in ExistingAgents)
        {
            Assert.Contains(name, present);
        }
    }

    [Fact]
    public void TheFullBundleCarriesTheThreeAgents()
    {
        IReadOnlyList<string> bundleAgents = Source.Bundle.AgentNames;

        foreach (string name in DeveloperAgents)
        {
            Assert.Contains(name, bundleAgents);
        }
    }
}
