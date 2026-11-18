using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Corpus;
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
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private const string DispatchSession = "sess-1";

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repository = new();
    private int _dispatchSequence;

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

    /// <summary>
    /// Every spelling that reaches `az` without putting it first is refused: a compound command, a subshell, an
    /// absolute path, a quoted program, and a wrapper whose own option stops the form.
    /// </summary>
    [Theory]
    [InlineData("echo hi; az vm delete --name db -g rg")]
    [InlineData("echo hi\naz vm delete --name db -g rg")]
    [InlineData("true; az vm delete --name db -g rg")]
    [InlineData("FOO=1; az vm delete --name db -g rg")]
    [InlineData("(az vm delete --name db -g rg)")]
    [InlineData("! az vm delete --name db -g rg")]
    [InlineData("/usr/bin/az vm delete --name db -g rg")]
    [InlineData("\"az\" vm delete --name db -g rg")]
    [InlineData("sudo -u root az vm delete --name db -g rg")]
    [InlineData("timeout 10 az vm delete --name db -g rg")]
    [InlineData("az vm list $(az vm delete -g rg)")]
    public void AMutatingAzureCommandBehindAShellPrefixOrSubshellIsRefused(string command)
    {
        TechnologyRepository(_repository.Path, "azure");

        string answer = Guard("claude", Command("claude", command));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("azure", "az group delete list")]
    [InlineData("azure", "az vm delete --name list")]
    [InlineData("aws", "aws s3 rm s3://b/list")]
    [InlineData("aws", "aws ec2 terminate-instances")]
    public void AMutatingVerbBeforeTheFirstFlagIsRefusedEvenWhenAReadFormAlsoMatches(string technology, string command)
    {
        TechnologyRepository(_repository.Path, technology);

        string answer = Guard("claude", Command("claude", command));

        Assert.Contains("mutating", Reason("claude", answer), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("az vm list")]
    [InlineData("az vm list -o table")]
    [InlineData("az vm show myVM -g myRG")]
    [InlineData("az group list --query \"[].name\"")]
    [InlineData("az network vnet list -o table")]
    [InlineData("az vm list-sizes --location westeurope")]
    [InlineData("az storage account show -n mystorage -g rg")]
    public void AListedAzureReadWithItsArgumentsIsAllowed(string command)
    {
        TechnologyRepository(_repository.Path, "azure");

        Assert.Empty(Guard("claude", Command("claude", command)));
    }

    [Theory]
    [InlineData("aws s3 ls s3://bucket")]
    [InlineData("aws ec2 describe-instances --region eu-west-1")]
    [InlineData("aws sts get-caller-identity")]
    public void AListedAwsReadWithItsArgumentsIsAllowed(string command)
    {
        TechnologyRepository(_repository.Path, "aws");

        Assert.Empty(Guard("claude", Command("claude", command)));
    }

    [Fact]
    public void ACommandWithNoWorkingDirectoryIsRefusedBecauseTheListCannotBeRead()
    {
        TechnologyRepository(_repository.Path, "azure");

        string answer = Guard("claude", CommandWithoutWorkingDirectory("claude", "az vm delete --name db -g rg"));

        Assert.Contains("working directory", Reason("claude", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable configuration is refused for the reason the missing directory is: a list the guard cannot read
    /// is one it cannot apply, so the load failure must not widen what a read-only dispatch may run.
    /// </summary>
    [Fact]
    public void ACommandInARepositoryWhoseConfigurationCannotBeReadIsRefused()
    {
        TechnologyRepository(_repository.Path, "azure");
        File.WriteAllText(
            Path.Combine(_repository.Path, ".kyber-weave", "kyber-weave.yml"),
            "ontology: [unclosed\n");

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.Contains("configuration", Reason("claude", answer), StringComparison.Ordinal);
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

    /// <summary>
    /// A read-only dispatch that names its technology is bound by that technology's list: the mutating command
    /// is refused under <c>azure</c>.
    /// </summary>
    [Fact]
    public void APlatformResearcherBoundToTheDispatchedTechnologyIsRefusedByThatTechnologysList()
    {
        TwoTechnologyRepository(_repository.Path, "azure", "aws");
        WriteDispatchTechnology("azure");

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// A technology folder's allow-list binds only a task of that technology: an <c>aws</c> researcher is not
    /// judged by the <c>azure</c> list, even though the host declares both.
    /// </summary>
    [Fact]
    public void APlatformResearcherBoundToAnotherTechnologyIsNotJudgedByTheAzureList()
    {
        TwoTechnologyRepository(_repository.Path, "azure", "aws");
        WriteDispatchTechnology("aws");

        Assert.Empty(Guard("claude", Command("claude", "az login")));
    }

    /// <summary>
    /// A dispatch that names no technology is fail-closed: every declared technology's list still binds the
    /// read-only agent, so a technology scoped to its own tasks never turns a refusal into an allow.
    /// </summary>
    [Fact]
    public void APlatformResearcherWhoseDispatchNamesNoTechnologyIsRefusedByEveryDeclaredList()
    {
        TwoTechnologyRepository(_repository.Path, "azure", "aws");
        WriteDispatchTechnology(technology: null);

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// A child technology's dispatch is bound by its parent's allow-list too, as every technology folder rule is.
    /// </summary>
    [Fact]
    public void APlatformResearcherBoundToAChildTechnologyIsRefusedByItsParentsList()
    {
        ChildOfAzureRepository(_repository.Path);
        WriteDispatchTechnology("child");

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// An undeclared technology is a typo or a folder the host does not have, so it cannot name an allow-list:
    /// every declared list still binds the read-only agent.
    /// </summary>
    [Fact]
    public void APlatformResearcherWhoseDispatchNamesAnUndeclaredTechnologyIsRefusedByEveryDeclaredList()
    {
        TwoTechnologyRepository(_repository.Path, "azure", "aws");
        WriteDispatchTechnology("azuree");

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two dispatches of the same agent in one session cannot be told apart from the tool call, so the guard
    /// falls back to every declared list rather than guess which technology the call belongs to.
    /// </summary>
    [Fact]
    public void TwoConcurrentDispatchesWithDifferentTechnologiesFallBackToEveryDeclaredList()
    {
        TwoTechnologyRepository(_repository.Path, "azure", "aws");
        WriteDispatchTechnology("azure");
        WriteDispatchTechnology("aws");

        // `az login` is outside azure's list and names no aws tool: only the fail-closed union refuses it.
        string answer = Guard("claude", Command("claude", "az login"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
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

        AssertListedInTheRulesPart("azure", ["list*", "show*"]);
        AssertListedInTheRulesPart("aws", ["describe-*", "list-*", "get-*", "ls"]);
    }

    private static void AssertListedInTheRulesPart(string technology, string[] forms)
    {
        string rules = RulesPartOfThePrompt(technology);
        foreach (string form in forms)
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

        string target = Path.Combine(root, "docs", "standards", technology);
        Directory.CreateDirectory(target);
        CopyShippedStandards(technology, target);
    }

    /// <summary>
    /// A technology with no shipped standards folder fails at the copy, as the directory read this replaced did,
    /// instead of leaving an empty folder for the guard to grade.
    /// </summary>
    [Fact]
    public void CopyingAStandardsFolderThatIsNotShippedFailsLoudly()
    {
        Assert.Throws<DirectoryNotFoundException>(() => CopyShippedStandards(
            "no-such-technology",
            Path.Combine(_repository.Path, "docs", "standards", "no-such-technology")));
    }

    /// <summary>Copies the shipped standards folder for one technology into a scratch repository, byte for byte.</summary>
    private static void CopyShippedStandards(string technology, string target)
    {
        string source = $"products/kyber-squad/standards/{technology}";

        // A corpus listing reports a missing directory as empty, so a misspelt technology would copy nothing and leave
        // the guard grading an empty folder; the directory read this replaced raised instead, and so does this.
        if (!Corpus.DirectoryExists(source))
            throw new DirectoryNotFoundException($"The shipped standards folder is missing: '{source}'.");

        foreach (string file in Corpus.EnumerateFiles(source, "*", recursive: false))
        {
            File.WriteAllBytes(Path.Combine(target, Path.GetFileName(file)), Corpus.ReadBytes(file));
        }
    }

    /// <summary>
    /// A repository that declares several technologies and holds each one's shipped standards folder, so every
    /// declared technology's rules file is loaded for one guard event that names no technology.
    /// </summary>
    private static void TwoTechnologyRepository(string root, params string[] technologies)
    {
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs\n  technologies: [{string.Join(", ", technologies)}]\n\narbiter:\n  enabled: true\n");

        foreach (string technology in technologies)
        {
            string target = Path.Combine(root, "docs", "standards", technology);
            Directory.CreateDirectory(target);
            CopyShippedStandards(technology, target);
        }
    }

    /// <summary>
    /// A repository that declares <c>child</c>, whose standard builds on the shipped <c>azure</c> folder, so the
    /// parent's allow-list is loaded for a task in the child.
    /// </summary>
    private static void ChildOfAzureRepository(string root)
    {
        TechnologyRepository(root, "azure");
        File.WriteAllText(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\n  technologies: [child]\n\narbiter:\n  enabled: true\n");

        string directory = Path.Combine(root, "docs", "standards", "child");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "child.standards.md"),
            "---\nid: standards/child\nbuilds-on: azure\n---\n\n# child\n");
    }

    /// <summary>
    /// Writes the dispatch pre event a worker's tool call belongs to: the platform researcher, in the same
    /// session as the call, naming <paramref name="technology"/> or no technology.
    /// </summary>
    private void WriteDispatchTechnology(string? technology)
    {
        string directory = Path.Combine(_repository.Path, "artifacts", "arbiter");
        Directory.CreateDirectory(directory);
        object dispatch = technology is null
            ? new
            {
                schema = "kyber-arbiter.ledger/v1",
                id = $"pre-{_dispatchSequence++}",
                at = "2026-11-17T09:00:00.0000000Z",
                source = "hook",
                harness = "claude",
                session = DispatchSession,
                phase = "pre",
                target = "platform-researcher",
                trigger = "delegate",
            }
            : new
            {
                schema = "kyber-arbiter.ledger/v1",
                id = $"pre-{_dispatchSequence++}",
                at = "2026-11-17T09:00:00.0000000Z",
                source = "hook",
                harness = "claude",
                session = DispatchSession,
                phase = "pre",
                target = "platform-researcher",
                trigger = "delegate",
                headers = new { technology },
            };
        File.AppendAllText(
            Path.Combine(directory, "ledger.jsonl"),
            JsonSerializer.Serialize(dispatch) + Environment.NewLine);
    }

    /// <summary>
    /// The guard decides the dispatch's technology from the ledger its injected state factory opens: it asks that
    /// factory for the project ledger, and the ledger it reads is the one the factory hands back. A guard that read the
    /// files itself would never call the factory, so this fails on the pre-refactor wiring.
    /// </summary>
    [Fact]
    public void TheGuardOpensTheProjectLedgerThroughTheInjectedStateFactory()
    {
        TechnologyRepository(_repository.Path, "azure");
        RecordingArbiterStateFactory state = new(
            new FileArbiterStateFactory(new FakeArbiterLogStore(), new PhysicalFileSystem(), new PhysicalFileSystem()));

        _ = Guard(GuardOver(state), "claude", Command("claude", "az vm list -o table"));

        Assert.Contains(
            Path.Combine(_repository.Path, "artifacts", "arbiter"),
            state.OpenedLedgers,
            StringComparer.Ordinal);
    }

    /// <summary>The worker guard over the application's registrations, with the ledger opened through <paramref name="state"/>.</summary>
    private static IWorkerGuard GuardOver(IArbiterStateFactory state)
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(state)
            .AddArbiterHost()
            .BuildServiceProvider();
        return services.GetRequiredService<IWorkerGuard>();
    }

    /// <summary>A state factory that records every directory the guard opens a ledger for.</summary>
    private sealed class RecordingArbiterStateFactory(IArbiterStateFactory inner) : IArbiterStateFactory
    {
        /// <summary>The directories the guard asked for a ledger.</summary>
        public List<string> OpenedLedgers { get; } = [];

        /// <inheritdoc />
        public IArbiterLedger OpenLedger(string arbiterDirectory)
        {
            OpenedLedgers.Add(arbiterDirectory);
            return inner.OpenLedger(arbiterDirectory);
        }

        /// <inheritdoc />
        public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => inner.OpenDecisionLog(arbiterDirectory);

        /// <inheritdoc />
        public IArbiterRunStore OpenRunStore(
            string arbiterDirectory,
            ArbiterStoreOptions? options = null,
            Func<DateTimeOffset>? clock = null) =>
            inner.OpenRunStore(arbiterDirectory, options, clock);
    }

    private string Guard(IWorkerGuard guard, string harness, string json)
    {
        using StringWriter stdout = new();
        guard.Run(
            harness,
            json,
            stdout,
            TextWriter.Null,
            _home.Path,
            () => Start,
            _ => ArbiterDispatchBudgetConfig.Defaults);
        return stdout.ToString();
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
                session_id = DispatchSession,
                hook_event_name = "PreToolUse",
                agent_id = "agent-1",
                agent_type = agent,
                tool_name = "Bash",
                tool_input = new { command },
            })
            : JsonSerializer.Serialize(new
            {
                cwd = _repository.Path,
                sessionId = DispatchSession,
                agentId = "agent-1",
                parentAgentId = "root",
                agentType = agent,
                toolName = "bash",
                toolArgs = JsonSerializer.Serialize(new { command }),
            });

    /// <summary>A Claude Code tool call with no <c>cwd</c>, which a malformed event or a proxy might send.</summary>
    private string CommandWithoutWorkingDirectory(string harness, string command) =>
        JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            agent_id = "agent-1",
            agent_type = "platform-researcher",
            tool_name = "Bash",
            tool_input = new { command },
        });

    private static string Reason(string harness, string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        return string.Equals(harness, "claude", StringComparison.Ordinal)
            ? root.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!
            : root.GetProperty("permissionDecisionReason").GetString()!;
    }

    /// <summary>The <c>technology-rules</c> part of a prompt built for <paramref name="technology"/> from the shipped folder.</summary>
    private static string RulesPartOfThePrompt(string technology)
    {
        using DocFixture fixture = new();
        fixture.Write("6-Docs/standards/shared-rules.md", ShippedStandardsFile("shared-rules.md"));
        foreach (string name in new[] { $"{technology}.standards.md", $"{technology}.rules.md", $"{technology}.skills.md" })
        {
            fixture.Write($"6-Docs/standards/{technology}/{name}", ShippedStandardsFile($"{technology}/{name}"));
        }

        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(
            $"ontology:\n  docs-root: 6-Docs\n  technologies: [{technology}]\n");
        IServiceProvider provider = DocsTestCommands.Services();
        DocumentIndex index = DocumentIndex.Build(
            RealDocumentLoader.Create(fixture.Root, config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        TaskPromptService service = new(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IFileSystem>(),
            index.StandardsFor);

        TaskPromptResult result = service.Build(new TaskPromptRequest(fixture.Root, TaskId: null, Technology: technology), config);

        Assert.True(result.Built, result.Refusal);
        const string openTag = "<technology-rules>";
        int open = result.Prompt.IndexOf(openTag, StringComparison.Ordinal);
        int close = result.Prompt.IndexOf("</technology-rules>", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "The prompt carries a technology-rules part.");
        return result.Prompt[(open + openTag.Length)..close];
    }

    private static string ShippedStandardsFile(string relativePath) =>
        Corpus.ReadText($"products/kyber-squad/standards/{relativePath}");
}
