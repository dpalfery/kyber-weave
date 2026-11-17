using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>platform-researcher</c> replaces <c>azure-reader</c> and <c>devops</c> replaces
/// <c>github-devops</c>. Neither body names the platform it works with: the request supplies the one
/// technology, and run start and delivery must accept <c>devops</c> exactly as they accept
/// <c>github-devops</c>.
/// </summary>
public sealed class PlatformAgentContentTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<SquadSource> Source = new(() => SquadSourceLoader.Load(ProductRoot));

    private static readonly Regex PlatformWord = new(
        @"\b(azure|aws|amazon|gcp|google cloud|kubernetes|k8s|github|gitlab|jira|bitbucket)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A bare <c>devops</c> token; <c>github-devops</c> does not count.</summary>
    private static readonly Regex BareDevops = new(
        @"(?<![\w-])devops(?![\w-])",
        RegexOptions.CultureInvariant);

    private static SquadAgent Agent(string name) =>
        Assert.Single(Source.Value.Agents, agent => agent.Name == name);

    [Fact]
    public void PlatformResearcherUsesThePlatformReaderProfileAndTheTieredRoster()
    {
        SquadAgent agent = Agent("platform-researcher");

        Assert.Equal("platform-reader", agent.CapabilityProfile);
        Assert.Equal("tiered", agent.WorkerRoster);
        Assert.Equal("fast", agent.ModelProfile);
        Assert.Equal(SquadInvocation.Subagent, agent.Invocation);
        Assert.Empty(agent.DelegatesTo);
    }

    [Fact]
    public void PlatformResearcherRunsCommandsAndCannotWritePublishOrDelegate()
    {
        SquadAgent agent = Agent("platform-researcher");
        SquadCapabilityProfile profile = Source.Value.CapabilityProfiles.Profiles[agent.CapabilityProfile];

        Assert.Equal(SquadPermissionDecision.Allow, profile.Permissions["process.execute"]);
        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["network.publish"]);
        Assert.Equal(SquadPermissionDecision.Deny, profile.Permissions["delegate"]);
        Assert.Contains(agent.CopilotTools, tool => tool.StartsWith("execute/", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.CopilotTools, tool => tool.StartsWith("edit/", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformResearcherNamesNoCloudOrPlatform()
    {
        string body = Agent("platform-researcher").InstructionBody;

        Assert.DoesNotMatch(PlatformWord, body);
    }

    [Fact]
    public void PlatformResearcherTakesItsOneTechnologyFromTheRequest()
    {
        string body = Agent("platform-researcher").InstructionBody;

        Assert.Contains("Take the technology from your request", body, StringComparison.Ordinal);
        Assert.Contains("Never assume one and never inspect a second one", body, StringComparison.Ordinal);
        Assert.Contains("<technology-rules>", body, StringComparison.Ordinal);
        Assert.Contains("<skills>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformResearcherReportsAndStopsWhenItsTechnologyHasNoFolder()
    {
        string body = Agent("platform-researcher").InstructionBody;

        Assert.Contains("If your request names a technology with no folder, report that and stop", body, StringComparison.Ordinal);
        Assert.Contains("Do not improvise commands", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformResearcherChangesNothingAndPrintsNoSecret()
    {
        string body = Agent("platform-researcher").InstructionBody;

        Assert.Contains("Change nothing, anywhere", body, StringComparison.Ordinal);
        Assert.Contains("Never print a secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DevopsUsesThePublishingWorkerProfileAndTheTieredRoster()
    {
        SquadAgent agent = Agent("devops");

        Assert.Equal("publishing-worker", agent.CapabilityProfile);
        Assert.Equal("tiered", agent.WorkerRoster);
        Assert.Equal("general", agent.ModelProfile);
    }

    [Fact]
    public void DevopsOwnsTheDeliveryDutiesAndNamesNoPlatform()
    {
        string body = Agent("devops").InstructionBody;

        Assert.DoesNotMatch(PlatformWord, body);
        Assert.Contains("TASK_GATE:", body, StringComparison.Ordinal);
        Assert.Contains("codegraph explore", body, StringComparison.Ordinal);
        foreach (string duty in new[] { "run start", "push", "pull request", "secret", "pipeline" })
        {
            Assert.Contains(duty, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DevopsTakesItsPlatformFromTheTaskOrTheHostConfiguration()
    {
        string body = Agent("devops").InstructionBody;

        Assert.Contains("The task or the host configuration names one platform", body, StringComparison.Ordinal);
        Assert.Contains("If neither names one, report that and stop", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAgentsAreInTheFullBundle()
    {
        Assert.Contains("devops", Source.Value.Bundle.AgentNames);
        Assert.Contains("platform-researcher", Source.Value.Bundle.AgentNames);
    }


    /// <summary>
    /// Run start gates on the capability profile, not on the agent name: a dispatch to <c>devops</c>
    /// is accepted exactly as one to <c>github-devops</c> because both hold <c>publishing-worker</c>,
    /// which is deliberately not a writer profile.
    /// </summary>
    [Fact]
    public void RunStartAcceptsADevopsDispatchAsItAcceptsGithubDevops()
    {
        Assert.Equal("publishing-worker", ArbiterSquadCatalog.LoadEmbedded().Agents["devops"].CapabilityProfile);
        Assert.False(RunWriterProfiles.IsWriter("publishing-worker"));
        Assert.False(RunStartEvidenceRules.IsWriterProfile("publishing-worker"));
        Assert.Matches(BareDevops, ArbiterRunStartRemedy.Text);
        Assert.Contains("github-devops", ArbiterRunStartRemedy.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryNamesDevopsWhereItNamesGithubDevops()
    {
        Assert.Matches(BareDevops, RunDelivery.DeliveryAgents);
        Assert.Contains("github-devops", RunDelivery.DeliveryAgents, StringComparison.Ordinal);
    }
}
