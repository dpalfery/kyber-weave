using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Tests.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

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

    private readonly InMemoryArbiterHost _host;
    private RecordingArbiterStateFactory? _recording;
    private int _dispatchSequence;

    public ArbiterCommandAllowListTests() =>
        _host = InMemoryArbiterHost.Create(services =>
            services.AddSingleton<IArbiterStateFactory>(_ => _recording = new RecordingArbiterStateFactory(_host!.State.Factory())));

    public void Dispose() => _host.Dispose();

    private string Repository => _host.WorkingDirectory;

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot-cli")]
    public void AListedAzureReadIsAllowedAndAMutatingCommandIsRefusedOnBothGuardedHarnesses(string harness)
    {
        TechnologyRepository(Repository, "azure");

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
        TechnologyRepository(Repository, "azure");

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
        TechnologyRepository(Repository, "azure");

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
        TechnologyRepository(Repository, technology);

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
        TechnologyRepository(Repository, "azure");

        Assert.Empty(Guard("claude", Command("claude", command)));
    }

    [Theory]
    [InlineData("aws s3 ls s3://bucket")]
    [InlineData("aws ec2 describe-instances --region eu-west-1")]
    [InlineData("aws sts get-caller-identity")]
    public void AListedAwsReadWithItsArgumentsIsAllowed(string command)
    {
        TechnologyRepository(Repository, "aws");

        Assert.Empty(Guard("claude", Command("claude", command)));
    }

    [Fact]
    public void ACommandWithNoWorkingDirectoryIsRefusedBecauseTheListCannotBeRead()
    {
        TechnologyRepository(Repository, "azure");

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
        TechnologyRepository(Repository, "azure");
        _ = _host.Write(
            Path.Combine(Repository, ".kyber-weave", "kyber-weave.yml"),
            "ontology: [unclosed\n");

        string answer = Guard("claude", Command("claude", "az vm delete --name db -g rg"));

        Assert.Contains("configuration", Reason("claude", answer), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandOutsideTheTechnologysToolIsNotJudgedByItsList()
    {
        TechnologyRepository(Repository, "azure");

        Assert.Empty(Guard("claude", Command("claude", "codegraph explore \"worker guard\"")));
        Assert.Empty(Guard("copilot-cli", Command("copilot-cli", "kyber-weave docs packet 4.4")));
    }

    [Fact]
    public void ADevelopersCommandIsNotJudgedByTheTechnologyList()
    {
        TechnologyRepository(Repository, "azure");

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db", agent: "dal-dev")));
    }

    /// <summary>
    /// A read-only dispatch that names its technology is bound by that technology's list: the mutating command
    /// is refused under <c>azure</c>.
    /// </summary>
    [Fact]
    public void APlatformResearcherBoundToTheDispatchedTechnologyIsRefusedByThatTechnologysList()
    {
        TwoTechnologyRepository(Repository, "azure", "aws");
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
        TwoTechnologyRepository(Repository, "azure", "aws");
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
        TwoTechnologyRepository(Repository, "azure", "aws");
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
        ChildOfAzureRepository(Repository);
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
        TwoTechnologyRepository(Repository, "azure", "aws");
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
        TwoTechnologyRepository(Repository, "azure", "aws");
        WriteDispatchTechnology("azure");
        WriteDispatchTechnology("aws");

        // `az login` is outside azure's list and names no aws tool: only the fail-closed union refuses it.
        string answer = Guard("claude", Command("claude", "az login"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("claude", answer), StringComparison.Ordinal);
    }

    [Fact]
    public void ATechnologyWithNoCommandRuleRefusesNothing()
    {
        TechnologyRepository(Repository, technology: null);

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db")));
    }

    [Fact]
    public void TheListIsOffWhenTheProjectTurnsTheArbiterOff()
    {
        TechnologyRepository(Repository, "azure", arbiterOff: true);

        Assert.Empty(Guard("claude", Command("claude", "az vm delete --name db")));
    }

    [Fact]
    public void AHarnessWithNoWorkerGuardRefusesNothing()
    {
        TechnologyRepository(Repository, "azure");
        string mutating = Command("claude", "az vm delete --name db");

        Assert.False(WorkerGuard.NeedsDecision("codex-cli", mutating));
        Assert.Empty(Guard("codex-cli", mutating));
    }

    [Fact]
    public void AFactoryChildResolvedToAReadOnlyTypeIsRefusedByTheTechnologyList()
    {
        TechnologyRepository(Repository, "azure");
        WriteDispatchTechnology("azure");
        FeedFactoryChild("platform-researcher");

        string answer = Guard("factory", FactoryCommand("az group delete --name db --resource-group rg"));

        Assert.StartsWith("AZURE-READONLY-001", Reason("factory", answer), StringComparison.Ordinal);
        Assert.Contains("az group delete", Reason("factory", answer), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Factory events the live probe recorded, with the ids replaced: the root session starts, its Task dispatch
    /// names the child's type, and the child session starts and names the parent it was called from.
    /// </summary>
    private void FeedFactoryChild(string type)
    {
        _ = Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = DispatchSession,
            hook_event_name = "SessionStart",
            source = "startup",
            cwd = Repository,
        }));
        _ = Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = DispatchSession,
            hook_event_name = "PreToolUse",
            tool_name = "Task",
            tool_input = new { subagent_type = type, description = "work", prompt = "do it" },
            cwd = Repository,
        }));
        _ = Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = "child-1",
            hook_event_name = "SessionStart",
            source = "startup",
            calling_session_id = DispatchSession,
            cwd = Repository,
        }));
    }

    private string FactoryCommand(string command) => JsonSerializer.Serialize(new
    {
        session_id = "child-1",
        hook_event_name = "PreToolUse",
        tool_name = "Execute",
        tool_input = new { command, summary = "run", riskLevel = "low" },
        cwd = Repository,
    });

    /// <summary>A repository that turns the Arbiter on, declares <paramref name="technology"/> and holds its shipped
    /// standards folder, so the folder's rules file is the one the worker guard reads.</summary>
    private void TechnologyRepository(string root, string? technology, bool arbiterOff = false)
    {
        string technologies = technology is null ? string.Empty : $"\n  technologies: [{technology}]";
        string enabled = arbiterOff ? "false" : "true";
        _ = _host.Write(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs{technologies}\n\narbiter:\n  enabled: {enabled}\n");

        if (technology is null)
        {
            return;
        }

        string target = Path.Combine(root, "docs", "standards", technology);
        _host.Files.CreateDirectory(target);
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
            Path.Combine(Repository, "docs", "standards", "no-such-technology")));
    }

    /// <summary>Copies the shipped standards folder for one technology into a scratch repository, byte for byte.</summary>
    private void CopyShippedStandards(string technology, string target)
    {
        string source = $"products/kyber-squad/standards/{technology}";

        // A corpus listing reports a missing directory as empty, so a misspelt technology would copy nothing and leave
        // the guard grading an empty folder; the directory read this replaced raised instead, and so does this.
        if (!Corpus.DirectoryExists(source))
            throw new DirectoryNotFoundException($"The shipped standards folder is missing: '{source}'.");

        foreach (string file in Corpus.EnumerateFiles(source, "*", recursive: false))
        {
            _host.Files.WriteAllBytes(Path.Combine(target, Path.GetFileName(file)), Corpus.ReadBytes(file));
        }
    }

    /// <summary>
    /// A repository that declares several technologies and holds each one's shipped standards folder, so every
    /// declared technology's rules file is loaded for one guard event that names no technology.
    /// </summary>
    private void TwoTechnologyRepository(string root, params string[] technologies)
    {
        _ = _host.Write(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs\n  technologies: [{string.Join(", ", technologies)}]\n\narbiter:\n  enabled: true\n");

        foreach (string technology in technologies)
        {
            string target = Path.Combine(root, "docs", "standards", technology);
            _host.Files.CreateDirectory(target);
            CopyShippedStandards(technology, target);
        }
    }

    /// <summary>
    /// A repository that declares <c>child</c>, whose standard builds on the shipped <c>azure</c> folder, so the
    /// parent's allow-list is loaded for a task in the child.
    /// </summary>
    private void ChildOfAzureRepository(string root)
    {
        TechnologyRepository(root, "azure");
        _ = _host.Write(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\n  technologies: [child]\n\narbiter:\n  enabled: true\n");

        string directory = Path.Combine(root, "docs", "standards", "child");
        _ = _host.Write(
            Path.Combine(directory, "child.standards.md"),
            "---\nid: standards/child\nbuilds-on: azure\n---\n\n# child\n");
    }

    /// <summary>
    /// Writes the dispatch pre event a worker's tool call belongs to: the platform researcher, in the same
    /// session as the call, naming <paramref name="technology"/> or no technology.
    /// </summary>
    private void WriteDispatchTechnology(string? technology)
    {
        string directory = Path.Combine(Repository, "artifacts", "arbiter");
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
        _host.State.Log.AppendLineAsync(directory, "ledger.jsonl", JsonSerializer.Serialize(dispatch), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// The guard decides the dispatch's technology from the ledger its injected state factory opens: it asks that
    /// factory for the project ledger, and the ledger it reads is the one the factory hands back. A guard that read the
    /// files itself would never call the factory, so this fails on the pre-refactor wiring.
    /// </summary>
    [Fact]
    public void TheGuardOpensTheProjectLedgerThroughTheInjectedStateFactory()
    {
        TechnologyRepository(Repository, "azure");

        _ = Guard("claude", Command("claude", "az vm list -o table"));

        Assert.Contains(
            Path.Combine(Repository, "artifacts", "arbiter"),
            Assert.IsType<RecordingArbiterStateFactory>(_recording).OpenedLedgers,
            StringComparer.Ordinal);
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
            TimeProvider? clock = null) =>
            inner.OpenRunStore(arbiterDirectory, options, clock);
    }

    private string Guard(string harness, string json)
    {
        using StringWriter stdout = new();
        _host.Get<IWorkerGuard>().Run(
            harness,
            json,
            stdout,
            TextWriter.Null,
            _host.HomeDirectory,
            () => Start,
            _ => ArbiterDispatchBudgetConfig.Defaults);
        return stdout.ToString();
    }

    /// <summary>A Copilot CLI or Claude Code tool call from the platform researcher running <paramref name="command"/>.</summary>
    private string Command(string harness, string command, string agent = "platform-researcher") =>
        string.Equals(harness, "claude", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new
            {
                cwd = Repository,
                session_id = DispatchSession,
                hook_event_name = "PreToolUse",
                agent_id = "agent-1",
                agent_type = agent,
                tool_name = "Bash",
                tool_input = new { command },
            })
            : JsonSerializer.Serialize(new
            {
                cwd = Repository,
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
        return harness is "claude" or "factory"
            ? root.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!
            : root.GetProperty("permissionDecisionReason").GetString()!;
    }
}
