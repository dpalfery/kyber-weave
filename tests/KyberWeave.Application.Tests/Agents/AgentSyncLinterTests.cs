using KyberWeave.Application.Agents;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Routing;
using KyberWeave.Core.Agents.Security;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Agents;

public class AgentSyncLinterTests
{
    private static readonly string Root = new InMemoryFileSystem().Root;

    private static AgentModel CreateAgent(string description, string role = "data-engineer", HarnessKind harness = HarnessKind.Claude)
    {
        return new AgentModel
        {
            RoleName = role,
            Harness = harness,
            FilePath = Path.Combine(Root, ".claude", "agents", $"{role}.md"),
            DirectoryPath = Path.Combine(Root, ".claude", "agents"),
            Description = description,
            InstructionsBody = "You are a data engineer."
        };
    }

    [Theory]
    [InlineData("Designs database schemas and generates migrations.")]
    [InlineData("Generates SQL queries, validates schema syntax, and connects to Postgres database.")]
    [InlineData("Handles user data synchronization across external CRM services.")]
    [InlineData("Processes incoming events from Kafka topics and writes aggregates.")]
    [InlineData("Calculates metrics for weekly developer velocity reports.")]
    [InlineData("Creates Kubernetes deployment manifests and Helm charts.")]
    [InlineData("Validates authentication tokens and manages OAuth session lifecycles.")]
    public void LintSetWhenAgentDescriptionIsActionOnlyEmitsKwAgentLint002Warning(string description)
    {
        AgentModel agent = CreateAgent(description);
        AgentSet agentSet = new AgentSet([agent]);

        DiagnosticReport report = AgentSyncLinter.LintSet(agentSet, Root, new InMemoryFileSystem().FileExists);

        Diagnostic? diagnostic = report.Items.FirstOrDefault(d => d.Code == "KW-AGENT-LINT-002");
        Assert.NotNull(diagnostic);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Equal("data-engineer", diagnostic.Subject);
    }

    [Theory]
    [InlineData("Use when designing SQL schemas, writing migrations, or optimizing database queries.")]
    [InlineData("Use when querying PostgreSQL databases.")]
    [InlineData("Use for triaging high-severity production alerts and incident response.")]
    [InlineData("Invoke when an automated security scan reports critical vulnerabilities.")]
    [InlineData("Trigger when pull request validation fails on CI pipeline steps.")]
    [InlineData("Apply when formatting markdown tables according to repo standards.")]
    [InlineData("Use this agent when analyzing memory leaks in .NET applications.")]
    public void LintSetWhenAgentDescriptionHasTriggerPhrasingDoesNotEmitKwAgentLint002(string description)
    {
        AgentModel agent = CreateAgent(description);
        AgentSet agentSet = new AgentSet([agent]);

        DiagnosticReport report = AgentSyncLinter.LintSet(agentSet, Root, new InMemoryFileSystem().FileExists);

        Assert.DoesNotContain(report.Items, d => d.Code == "KW-AGENT-LINT-002");
    }
}
