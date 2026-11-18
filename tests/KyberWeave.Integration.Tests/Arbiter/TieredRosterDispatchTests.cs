using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The tiered roster must be dispatchable through the Arbiter's own path, <c>KW-ARB-ROSTER-001</c>. The second
/// end-to-end pass refused <c>dev-medium</c> with <c>not-listed</c> while the deployed conductor listed it, and
/// allowed <c>csharp-dev</c> while the same conductor's rendered delegation map did not.
/// </summary>
/// <remarks>
/// Each case runs the production hook path: the OpenCode plugin adapter, the real hook command, the real
/// configuration loader, the real classifier and fact builder, and the shipped rule. Every other rule is
/// disabled by id, so the verdict these tests read is the roster rule's alone; the same isolation the other
/// real-engine tests use.
/// </remarks>
public sealed class TieredRosterDispatchTests : IDisposable
{
    private const string RosterRule = "KW-ARB-ROSTER-001";

    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    /// <summary>Writes the host configuration for one roster, with every rule but the roster rule disabled.</summary>
    private void WriteConfig(string roster)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".kyber-weave"));
        string disabled = string.Join(
            "\n",
            ArbiterConfigDefaults.Product.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal)
                .Where(id => !string.Equals(id, RosterRule, StringComparison.Ordinal))
                .Select(id => $"    - id: {id}\n      enabled: false"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"),
            $"worker-roster: {roster}\n\n" +
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n" +
            "  rules:\n" +
            disabled + "\n");
    }

    /// <summary>Dispatches one implementation task to <paramref name="target"/> through the OpenCode hook path.</summary>
    private string Dispatch(string target)
    {
        ArbiterHookDecisionEngine engine = ArbiterTestServices.Engine(
            providerFactory: _ => new ArbiterEvaluatorFixtures.ScriptedProvider(),
            homeDirectory: () => _home.Path);
        HookCommand command = ArbiterTestServices.Command(
            new HarnessAdapterRegistry(PluginHookAdapters.All(engine)),
            ArbiterTestServices.LoadHostConfig,
            () => "decision-tiered-roster-1");

        JsonObject envelope = new()
        {
            ["schema"] = PluginEnvelope.SchemaV1,
            ["harness"] = "opencode",
            ["phase"] = "before",
            ["tool"] = "subagent",
            ["call-id"] = "d-1.1",
            ["session"] = "ses-tiered-1",
            ["cwd"] = _repo.Path,
            ["args"] = new JsonObject
            {
                ["description"] = "Implement task 1.1",
                ["prompt"] = "Task 1.1: add Slug.Make and its tests.",
                ["agent"] = target,
            },
        };

        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("opencode", "conductor", envelope.ToJsonString(), stdout, log);
        Assert.Equal(0, exit);
        return stdout.ToString();
    }

    /// <summary>
    /// The five agents a tiered conductor dispatches: the three developer tiers and the two roster-specific agents
    /// that replaced <c>azure-reader</c> and <c>github-devops</c>. Each must be allowed.
    /// </summary>
    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("dev-hard")]
    [InlineData("platform-researcher")]
    [InlineData("devops")]
    public void ATieredConductorDispatchToItsRosterIsAllowed(string target)
    {
        WriteConfig(WorkerRosters.Tiered);

        string output = Dispatch(target);

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("allow", document.RootElement.GetProperty("decision").GetString());
    }

    /// <summary>
    /// A technology developer is not on a tiered conductor's rendered roster, so the rule refuses it and says which
    /// tier the task belongs on. With no plan task recorded, the task reads as medium, so the tier is <c>dev-medium</c>.
    /// </summary>
    [Fact]
    public void ATieredConductorDispatchToATechnologyDeveloperIsRefusedWithTheTierNamed()
    {
        WriteConfig(WorkerRosters.Tiered);

        string output = Dispatch("csharp-dev");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal("block", root.GetProperty("decision").GetString());
        string reason = root.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
        Assert.Contains(RosterRule, reason, StringComparison.Ordinal);
        Assert.Contains("worker-roster: technology", reason, StringComparison.Ordinal);
        Assert.Contains("dev-medium", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A conductor whose embedded catalog predates the developer tiers still receives them, because the roster comes
    /// from the host configuration: that stale snapshot is what refused <c>dev-medium</c> in the second pass. The
    /// technology developers the roster replaced are dropped from the same list.
    /// </summary>
    [Fact]
    public void AStaleCatalogGainsTheTiersAndDropsTheReplacedTechnologyDevelopers()
    {
        ArbiterSquadCatalog stale = Catalog(
            ("conductor", "orchestrator", null,
                ["architect", "csharp-dev", "azure-reader", "github-devops", "docs-dev", "test-dev"]),
            ("architect", "architect", null, ["research-agent"]),
            ("csharp-dev", "worker", WorkerRosters.Technology, []),
            ("test-dev", "worker", WorkerRosters.Technology, []),
            ("azure-reader", "platform-reader", WorkerRosters.Technology, []),
            ("github-devops", "publishing-worker", WorkerRosters.Technology, []),
            ("docs-dev", "documentation", null, []),
            ("research-agent", "investigator", null, []));
        ArbiterSquadAgent conductor = stale.Agents["conductor"];

        IReadOnlyList<string> tiered = RosterDelegatesTo.For(conductor, WorkerRosters.Tiered, stale);

        // The whole roster the second pass needed, from a catalog written before any of it existed: the three tiers,
        // and the replacements for the two removed specialist agents.
        Assert.Equal(
            ["architect", "platform-researcher", "devops", "docs-dev", "dev-easy", "dev-medium", "dev-hard"],
            tiered);
        Assert.Equal(conductor.DelegatesTo, RosterDelegatesTo.For(conductor, WorkerRosters.Technology, stale));
    }

    /// <summary>A caller that dispatches no developer gains no tier: the roster replaces a developer roster, not a persona.</summary>
    [Fact]
    public void ACallerThatDispatchesNoDeveloperGainsNoTier()
    {
        ArbiterSquadCatalog stale = Catalog(
            ("architect", "architect", null, ["research-agent", "azure-reader"]),
            ("research-agent", "investigator", null, []),
            ("azure-reader", "platform-reader", WorkerRosters.Technology, []));
        ArbiterSquadAgent architect = stale.Agents["architect"];

        IReadOnlyList<string> tiered = RosterDelegatesTo.For(architect, WorkerRosters.Tiered, stale);

        // The removed platform reader is replaced by the tiered researcher; no developer tier is added.
        Assert.Equal(["research-agent", "platform-researcher"], tiered);
    }

    private static ArbiterSquadCatalog Catalog(
        params (string Name, string Profile, string? Roster, string[] DelegatesTo)[] agents)
    {
        Dictionary<string, ArbiterSquadAgent> byName = new(StringComparer.Ordinal);
        foreach ((string name, string profile, string? roster, string[] delegatesTo) in agents)
        {
            byName[name] = new ArbiterSquadAgent(
                name,
                $"{name} agent",
                delegatesTo,
                profile,
                ArbiterSquadCatalog.TargetClassFor(profile),
                roster);
        }

        return new ArbiterSquadCatalog(byName, new Dictionary<string, ArbiterSquadLens>(StringComparer.Ordinal));
    }
}
