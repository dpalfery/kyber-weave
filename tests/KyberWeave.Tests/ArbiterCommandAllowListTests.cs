using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A technology's Arbiter rules file declares the command forms a read-only dispatch may run, and the
/// worker guard refuses every other command of that technology's tool on Claude Code and Copilot CLI.
/// On a harness with no worker guard nothing is refused, and the same forms are stated in the
/// technology's rules part of the task prompt. A technology that declares no list refuses nothing.
/// </summary>
public sealed class ArbiterCommandAllowListTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The forms <c>AZURE-READONLY-001</c> allows, stated in both azure.arbiter-rules.yml and azure.rules.md.</summary>
    private static readonly string[] AzureReadForms = ["az * list", "az * list-*", "az * show", "az * show-*"];

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repository = new();

    public void Dispose()
    {
        _home.Dispose();
        _repository.Dispose();
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot-cli")]
    public void AListedAzureReadIsAllowedAndAMutatingCommandIsRefusedOnBothGuardedHarnesses(string harness)
    {
        TechnologyRepository(_repository.Path, "azure");

        Assert.True(WorkerGuard.NeedsDecision(harness, Command(harness, "az vm list")));
        Assert.Empty(Guard(harness, Command(harness, "az vm list -o table")));

        string answer = Guard(harness, Command(harness, "az vm delete --name db --resource-group rg"));

        string reason = Reason(harness, answer);
        Assert.StartsWith("AZURE-READONLY-001", reason, StringComparison.Ordinal);
        Assert.Contains("az vm delete", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot-cli")]
    public void AShellRedirectThatWritesAFileIsRefusedForTheResearchAgent(string harness)
    {
        TechnologyRepository(_repository.Path, "azure");

        string answer = Guard(harness, Command(harness, "az vm list > resources.txt"));

        Assert.StartsWith("AZURE-READONLY-001", Reason(harness, answer), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandOutsideTheTechnologysToolIsNotJudgedByItsList()
    {
        TechnologyRepository(_repository.Path, "azure");

        Assert.Empty(Guard("claude", Command("claude", "codegraph explore \"worker guard\"")));
        Assert.Empty(Guard("copilot-cli", Command("copilot-cli", "kyber-weave docs packet 4.4")));
    }

    [Fact]
    public void ADevelopersCommandIsNotJudgedByTheTechnologyList()
    {
        TechnologyRepository(_repository.Path, "azure");

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db", agent: "dal-dev")));
    }

    [Fact]
    public void ATechnologyWithNoCommandRuleRefusesNothing()
    {
        TechnologyRepository(_repository.Path, technology: null);

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db")));
    }

    [Fact]
    public void TheListIsOffWhenTheProjectTurnsTheArbiterOff()
    {
        TechnologyRepository(_repository.Path, "azure", arbiterOff: true);

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db")));
    }

    [Fact]
    public void AHarnessWithNoWorkerGuardRefusesNothingAndTheListIsInThePromptRulesPart()
    {
        TechnologyRepository(_repository.Path, "azure");
        string mutating = Command("claude", "az vm delete --name db");

        Assert.False(WorkerGuard.NeedsDecision("codex-cli", mutating));
        Assert.Empty(Guard("codex-cli", mutating));

        string rules = RulesPartOfTheAzurePrompt();
        foreach (string form in AzureReadForms)
        {
            Assert.Contains(form, rules, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A repository that turns the Arbiter on, declares <paramref name="technology"/> and holds its shipped
    /// standards folder, so the folder's rules file is the one the worker guard reads.
    /// </summary>
    private static void TechnologyRepository(string root, string? technology, bool arbiterOff = false)
    {
        string technologies = technology is null ? string.Empty : $"\n  technologies: [{technology}]";
        string enabled = arbiterOff ? "false" : "true";
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs{technologies}\n\narbiter:\n  enabled: {enabled}\n");

        if (technology is null)
        {
            return;
        }

        string source = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "standards", technology);
        string target = Path.Combine(root, "docs", "standards", technology);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    private string Guard(string harness, string json)
    {
        using StringWriter stdout = new();
        TheGuard().Run(
            harness,
            json,
            stdout,
            TextWriter.Null,
            _home.Path,
            () => Start,
            _ => ArbiterDispatchBudgetConfig.Defaults);
        return stdout.ToString();
    }

    private static IWorkerGuard TheGuard() => ArbiterTestServices.Get<IWorkerGuard>();

    /// <summary>A Copilot CLI or Claude Code tool call from the platform researcher running <paramref name="command"/>.</summary>
    private string Command(string harness, string command, string agent = "platform-researcher") =>
        string.Equals(harness, "claude", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new
            {
                cwd = _repository.Path,
                hook_event_name = "PreToolUse",
                agent_id = "agent-1",
                agent_type = agent,
                tool_name = "Bash",
                tool_input = new { command },
            })
            : JsonSerializer.Serialize(new
            {
                cwd = _repository.Path,
                agentId = "agent-1",
                parentAgentId = "root",
                agentType = agent,
                toolName = "bash",
                toolArgs = JsonSerializer.Serialize(new { command }),
            });

    private static string Reason(string harness, string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        return string.Equals(harness, "claude", StringComparison.Ordinal)
            ? root.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!
            : root.GetProperty("permissionDecisionReason").GetString()!;
    }

    /// <summary>The <c>technology-rules</c> part of a prompt built for azure from the shipped folder.</summary>
    private static string RulesPartOfTheAzurePrompt()
    {
        using DocFixture fixture = new();
        fixture.Write("6-Docs/standards/shared-rules.md", ShippedStandardsFile("shared-rules.md"));
        foreach (string name in new[] { "azure.standards.md", "azure.rules.md", "azure.skills.md" })
        {
            fixture.Write($"6-Docs/standards/azure/{name}", ShippedStandardsFile($"azure/{name}"));
        }

        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml(
            "ontology:\n  docs-root: 6-Docs\n  technologies: [azure]\n");
        IServiceProvider provider = DocsTestCommands.Services();
        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(fixture.Root, config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        TaskPromptService service = new(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            index.StandardsFor);

        TaskPromptResult result = service.Build(new TaskPromptRequest(fixture.Root, TaskId: null, Technology: "azure"), config);

        Assert.True(result.Built, result.Refusal);
        const string openTag = "<technology-rules>";
        int open = result.Prompt.IndexOf(openTag, StringComparison.Ordinal);
        int close = result.Prompt.IndexOf("</technology-rules>", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "The prompt carries a technology-rules part.");
        return result.Prompt[(open + openTag.Length)..close];
    }

    private static string ShippedStandardsFile(string relativePath) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "standards", relativePath));
}
