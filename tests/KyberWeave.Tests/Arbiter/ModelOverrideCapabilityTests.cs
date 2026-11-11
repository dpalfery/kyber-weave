using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The model-override capability a harness adapter declares: whether the harness lets the Arbiter
/// set the model of a dispatched sub-agent, which field carries it, and which model aliases it
/// accepts. Only Claude declares one, so every other harness answers unsupported and no rule
/// ever assumes an override a harness cannot honour.
/// </summary>
/// <remarks>
/// The Claude declaration cites the sub-agents documentation it was read from, so a reviewer can
/// check the aliases against the source rather than against this file. The allow document with no
/// model token is pinned to the keys it carries today, so adding the override cannot change what
/// a dispatch without an override receives.
/// </remarks>
public sealed class ModelOverrideCapabilityTests
{
    private const string SubAgentsDocs = "https://code.claude.com/docs/en/sub-agents";

    /// <summary>A hook adapter whose declared capability is set by the test, to observe what the host copies.</summary>
    private sealed class RecordingAdapter : IHarnessHookAdapter
    {
        public HookContext? SeenContext { get; private set; }

        public string HarnessToken => "recording";

        public ModelOverrideCapability ModelOverride { get; } = new(
            true,
            "model",
            new HashSet<string>(StringComparer.Ordinal) { "opus" },
            "test declaration");

        public bool IsPassThrough(JsonElement payload, string? renderedCaller) => false;

        public string Handle(
            JsonElement payload,
            string rawJson,
            string? renderedCaller,
            KyberWeaveConfig config,
            HookContext context)
        {
            SeenContext = context;
            return string.Empty;
        }

        public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId) =>
            string.Empty;
    }

    /// <summary>A decision engine that always allows, so the Claude allow document is what the test reads.</summary>
    private sealed class AllowEngine : IHookDecisionEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);
    }

    public static IEnumerable<object[]> NonClaudeHarnessTokens()
    {
        return AllAdapters()
            .Select(adapter => adapter.HarnessToken)
            .Where(token => !string.Equals(token, ClaudeHookAdapter.Token, StringComparison.Ordinal))
            .Select(token => new object[] { token });
    }

    /// <summary>
    /// Claude is the one harness whose sub-agents take a model override, through the <c>model</c>
    /// field, accepting exactly four aliases.
    /// </summary>
    [Fact]
    public void ClaudeDeclaresModelOverrideFromTheSubAgentsDocs()
    {
        ModelOverrideCapability capability = new ClaudeHookAdapter(new AllowEngine()).ModelOverride;

        Assert.True(capability.Supported, "Claude sub-agents accept a model override.");
        Assert.Equal("model", capability.Field);
        IReadOnlySet<string> aliases = capability.AcceptedAliases
            ?? throw new Xunit.Sdk.XunitException("Claude must declare its accepted aliases.");
        Assert.Equal(
            new[] { "fable", "haiku", "opus", "sonnet" },
            aliases.Order(StringComparer.Ordinal));
        Assert.Contains(SubAgentsDocs, capability.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every command and plugin adapter other than Claude answers unsupported, so a rule that asks
    /// for a model override on those harnesses gets no override to apply.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonClaudeHarnessTokens))]
    public void EveryNonClaudeAdapterDeclaresNoModelOverride(string token)
    {
        IHarnessHookAdapter adapter = AllAdapters().Single(candidate => candidate.HarnessToken == token);

        Assert.Equal(ModelOverrideCapability.Unsupported, adapter.ModelOverride);
    }

    /// <summary>
    /// The theory is only meaningful if it covers the whole registry, so the non-Claude list must
    /// not be empty.
    /// </summary>
    [Fact]
    public void TheNonClaudeTheoryCoversTheRegistry()
    {
        Assert.NotEmpty(NonClaudeHarnessTokens());
    }

    /// <summary>
    /// The host copies the adapter's declaration into the context the adapter handles the event with,
    /// the same way it copies <c>ObservesReturns</c>.
    /// </summary>
    [Fact]
    public void HookCommandCopiesTheDeclarationIntoTheHookContext()
    {
        RecordingAdapter adapter = new();
        HookCommand command = new(
            new HarnessAdapterRegistry(new IHarnessHookAdapter[] { adapter }),
            _ => EnabledConfig(),
            () => "decision-model-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("recording", null, "{}", stdout, log);

        Assert.Equal(0, exit);
        HookContext seen = adapter.SeenContext
            ?? throw new Xunit.Sdk.XunitException("the host must hand the adapter a context.");
        Assert.Equal(adapter.ModelOverride, seen.ModelOverride);
    }

    /// <summary>
    /// The delegate trigger declares <c>harness.model-override</c>, the fact a rule reads, so a
    /// declared override reaches the fact bag the same way <c>harness.observes-returns</c> does.
    /// </summary>
    [Fact]
    public void TheDelegateTriggerDeclaresTheModelOverrideFact()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate", out IReadOnlySet<string>? declared));
        Assert.Contains("harness.model-override", declared ?? throw new Xunit.Sdk.XunitException("the delegate trigger must declare facts."));
    }

    /// <summary>
    /// An outcome carries no model token unless the decision sets one, so every existing outcome
    /// keeps its meaning.
    /// </summary>
    [Fact]
    public void AnOutcomeWithoutAModelTokenHasNoToken()
    {
        HookOutcome outcome = new(HookOutcomeKind.Allow);

        Assert.Null(outcome.ModelToken);
    }

    /// <summary>
    /// With no model token, Claude's allow document keeps the three keys it carries today:
    /// nothing is added to the document for a dispatch that has no override.
    /// </summary>
    [Fact]
    public void ANullModelTokenLeavesClaudesAllowDocumentUnchanged()
    {
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(new AllowEngine()),
            _ => EnabledConfig(),
            () => "decision-model-2");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(
            "claude",
            "conductor",
            File.ReadAllText(Path.Combine(
                KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
                "Fixtures", "arbiter-hooks", "claude", "pre-agent-dispatch.json")),
            stdout,
            log);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal(
            new[] { "hookEventName", "permissionDecision", "updatedInput" },
            output.EnumerateObject().Select(property => property.Name));
    }

    private static IReadOnlyList<IHarnessHookAdapter> AllAdapters()
    {
        IHookDecisionEngine engine = new ArbiterHookDecisionEngine();
        return [.. CommandHookAdapters.All(engine), .. PluginHookAdapters.All(engine)];
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };
}
