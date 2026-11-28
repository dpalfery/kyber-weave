using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Arbiter;
using System.ComponentModel;
using System.Reflection;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 17.1: <c>kyber-weave-arbiter serve</c> is a stdio MCP server for the
/// D4 fallback. It declares five tools with routing descriptions, leads every response
/// with the provenance line, resolves its root as flag, then environment, then working
/// directory, answers <c>arbiter_evaluate_agent_dispatch_or_return</c> with the hook's own outcome for the same
/// event, and never reports the key. Stdout belongs to the transport.
/// RED: <c>ArbiterTools</c> and the serve surface do not exist yet.
/// </summary>
public sealed class ArbiterMcpPackagingTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private sealed class RecordingEngine : IContextualHookDecisionEngine
    {
        public HookOutcome Outcome { get; set; } = new(HookOutcomeKind.Allow);

        public List<(string Harness, string Caller, string? Target, string Prompt)> PreDispatches { get; } = [];

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            Outcome;

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            Outcome;

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null)
        {
            PreDispatches.Add((harness, caller, target, prompt));
            return Outcome;
        }

        public HookOutcome DecidePostDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null) =>
            Outcome;
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel,
            string? deliveredPrompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(
                harness, caller, target, prompt, config, context, toolCallId, sessionId));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(
                harness, caller, target, prompt, toolOutput, config, context, toolCallId, sessionId));
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfigDefaults.Product.Rules } };

    private static KyberWeaveConfig RemoteConfig() =>
        new()
        {
            Arbiter = new ArbiterConfig
            {
                Enabled = true,
                Rules = ArbiterConfigDefaults.Product.Rules,
                Provider = new ArbiterProviderConfig(
                    ArbiterProviderKind.Systemone, "https://typesafe.example.test/v1", "jev-test", 3000),
            },
        };

    private ArbiterServeContext Context(string root, RecordingEngine engine, bool keyResolved = false) =>
        new(root, engine, TextWriter.Null, _ => EnabledConfig(), (_, _) => Task.FromResult(keyResolved));

    /// <summary>
    /// Answers the shipped <c>KW-ARB-CLAIM-001</c> question in call order, so a test
    /// stages which finding escalates: <c>supports</c> is <c>verify</c> for a refutation.
    /// </summary>
    private sealed class ScriptedClaimProvider(params string[] answers) : IArbiterProvider
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> byRule = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                byRule[rule.Id] = string.Equals(rule.Id, "KW-ARB-CLAIM-001", StringComparison.Ordinal)
                    ? NextAnswer()
                    : "undecidable";
            }

            return Task.FromResult(new ArbiterStep1BatchResult(byRule, "scripted", null, RequestSent: true));
        }

        private string NextAnswer() => answers[Math.Min(_calls++, answers.Length - 1)];
    }

    private ArbiterTools RealEngineTools(string root, IArbiterProvider provider) =>
        _host.Tools(new ArbiterServeContext(
            root,
            _host.Engine(_ => provider, homeDirectory: () => root),
            TextWriter.Null,
            _ => EnabledConfig(),
            (_, _) => Task.FromResult(false)));

    private string RecordedLines(string fileName) =>
        string.Join("\n", _host.State.Log.LinesOf(_host.ArbiterDirectory, fileName));

    private static string Finding(string id) =>
        $"- id: {id}\n" +
        "  severity: major\n" +
        "  file: src/Auth.cs\n" +
        "  line: 42\n" +
        "  excerpt: key := r.Query().Get(\"key\")\n" +
        "  claim: The key reaches the log.\n" +
        "  evidence: it is interpolated\n" +
        "  failure_scenario: secrets leak";

    private static string BadFindings(string kind) => kind switch
    {
        "slash" => Finding("security/key/in-argv"),
        "comma" => Finding("security/key-in-argv,correctness/off-by-one"),
        "empty" => "- id:\n  severity: major\n  claim: The key reaches the log.",
        "duplicate" => Finding("security/key-in-argv") + "\n" + Finding("security/key-in-argv"),
        _ => "severity: major\n  claim: The key reaches the log.",
    };

    private static string FullPath(string path) => Path.GetFullPath(path);

    [Theory]
    [InlineData("arbiter_evaluate_agent_dispatch_or_return", false)]
    [InlineData("arbiter_list_effective_rules_by_trigger", true)]
    [InlineData("arbiter_check_configuration_and_key_readiness", true)]
    public void ThreeToolsDeclareTheirAnnotations(string toolName, bool readOnly)
    {
        McpServerToolAttribute attribute = ToolMethod(toolName).GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(readOnly, attribute.ReadOnly);
        Assert.False(attribute.OpenWorld, $"{toolName} runs against a local root and must not claim an open world.");
    }

    [Fact]
    public void TheServeSurfaceDeclaresExactlyTheFiveArbiterTools()
    {
        string[] names = typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "arbiter_check_configuration_and_key_readiness",
                "arbiter_classify_bot_review_comment",
                "arbiter_evaluate_agent_dispatch_or_return",
                "arbiter_list_effective_rules_by_trigger",
                "arbiter_select_relevant_document_sections",
            ],
            names);
    }

    [Theory]
    [InlineData("arbiter_evaluate_agent_dispatch_or_return")]
    [InlineData("arbiter_list_effective_rules_by_trigger")]
    [InlineData("arbiter_check_configuration_and_key_readiness")]
    public void ToolDescriptionsScoreAsRoutingMetadata(string toolName)
    {
        DescriptionScore score = ScoreDescription(toolName);

        Assert.Equal(35, score.Components.Single(c => c.Name == "Trigger clause").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Specific opening").Points);
        Assert.Equal(15, score.Components.Single(c => c.Name == "Trigger keywords").Points);
    }

    [Fact]
    public void ExactlyOneArbiterToolCarriesANegativeBoundary()
    {
        string[] tools = ["arbiter_evaluate_agent_dispatch_or_return", "arbiter_list_effective_rules_by_trigger", "arbiter_check_configuration_and_key_readiness"];
        string[] bounded = tools
            .Where(name => ScoreDescription(name).Components
                .Single(c => c.Name == "Negative boundary").Points > 0)
            .ToArray();

        Assert.Single(bounded);
    }

    [Fact]
    public async Task EveryResponseLeadsWithTheProvenanceLine()
    {
        string root = _host.RepoRoot;
        ArbiterTools tools = _host.Tools(Context(root, new RecordingEngine()));
        string prefix = $"provenance: root={FullPath(root)} ";

        Assert.StartsWith(prefix, tools.Rules(), StringComparison.Ordinal);
        Assert.StartsWith(prefix, await tools.StatusAsync(), StringComparison.Ordinal);
        Assert.StartsWith(
            prefix,
            await tools.EvaluateAsync("lens.spawn", new ArbiterFacts { Target = "review-lens", Lens = ["security"] }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RootResolutionPrefersTheFlagThenTheEnvironmentThenTheWorkingDirectory()
    {
        string flagRoot = _host.InWork("flag-root");
        string envRoot = _host.InWork("env-root");
        string workingDirectory = _host.InWork("working-directory");
        foreach (string directory in new[] { flagRoot, envRoot, workingDirectory })
        {
            _host.Files.CreateDirectory(directory);
        }

        Assert.Equal(
            FullPath(flagRoot),
            _host.Get<ArbiterRootResolver>().Resolve(["--repo-root", flagRoot], workingDirectory, envRoot));
        Assert.Equal(
            FullPath(envRoot),
            _host.Get<ArbiterRootResolver>().Resolve([], workingDirectory, envRoot));
        Assert.Equal(
            FullPath(workingDirectory),
            _host.Get<ArbiterRootResolver>().Resolve([], workingDirectory, null));
    }

    [Fact]
    public async Task EvaluateReturnsTheOutcomeTheHookEngineReturnsForTheSameEvent()
    {
        string root = _host.RepoRoot;
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Deny, "ENVELOPE-BODY") };
        ArbiterTools tools = _host.Tools(Context(root, engine));

        string response = await tools.EvaluateAsync(
            "lens.spawn",
            new ArbiterFacts { Target = "review-lens", Lens = ["security"], Prompt = "Review the diff." });

        Assert.Contains("ENVELOPE-BODY", response, StringComparison.Ordinal);
        (string harness, string caller, string? target, string prompt) = Assert.Single(engine.PreDispatches);
        Assert.Equal("code-reviewer", caller);
        Assert.Equal("review-lens", target);
        // The tool synthesizes the routing header block the hook path would see.
        Assert.StartsWith("KYBER-ARBITER: true\nLENS: security", prompt, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(harness));
    }

    [Fact]
    public async Task Evaluate_ClassifiesAFallbackRefutationCallWithFindingOnly()
    {
        // The shipped contract tells a fallback code-reviewer to pass `finding` and
        // never names `target` or the trigger header, so the tool must synthesize the
        // routing block the hook path would see (review 20.1, Major 2).
        string root = _host.RepoRoot;
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = _host.Tools(Context(root, engine));

        const string finding =
            "- id: security/key-in-argv\n" +
            "  severity: major\n" +
            "  file: src/Auth.cs\n" +
            "  line: 42\n" +
            "  excerpt: key := r.Query().Get(\"key\")\n" +
            "  claim: The key reaches the log.\n" +
            "  evidence: it is interpolated\n" +
            "  failure_scenario: secrets leak";
        string response = await tools.EvaluateAsync("refute.spawn", new ArbiterFacts { Finding = finding });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        (string harness, string caller, string? target, string prompt) = Assert.Single(engine.PreDispatches);
        Assert.Equal("code-reviewer", caller);
        Assert.Equal("review-lens", target);
        Assert.StartsWith("KYBER-ARBITER: true", prompt, StringComparison.Ordinal);
        Assert.Contains("REFUTE: security/key-in-argv", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_ClassifiesAFallbackLensCallWithoutTarget()
    {
        string root = _host.RepoRoot;
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = _host.Tools(Context(root, engine));

        string response = await tools.EvaluateAsync("lens.spawn", new ArbiterFacts { Lens = ["security", "test-adequacy"] });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        Assert.Equal(2, engine.PreDispatches.Count);
        Assert.All(engine.PreDispatches, entry =>
        {
            Assert.Equal("code-reviewer", entry.Caller);
            Assert.Equal("review-lens", entry.Target);
            Assert.StartsWith("KYBER-ARBITER: true", entry.Prompt, StringComparison.Ordinal);
        });
        Assert.Contains("LENS: security", engine.PreDispatches[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("LENS: test-adequacy", engine.PreDispatches[1].Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lens.spawn")]
    [InlineData("refute.spawn")]
    public async Task Evaluate_WithoutLensOrFinding_SaysWhatToPass(string trigger)
    {
        string root = _host.RepoRoot;
        RecordingEngine engine = new();
        ArbiterTools tools = _host.Tools(Context(root, engine));

        string response = await tools.EvaluateAsync(trigger, new ArbiterFacts { Target = "review-lens" });

        Assert.Contains("outcome: error", response, StringComparison.Ordinal);
        Assert.Contains("lens", response, StringComparison.Ordinal);
        Assert.Contains("finding", response, StringComparison.Ordinal);
        Assert.Empty(engine.PreDispatches);
    }

    [Fact]
    public async Task Evaluate_RefutationCallThroughTheRealEngine_DecidesAndRecords()
    {
        string root = _host.RepoRoot;
        _ = _host.WriteRepoConfig("arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
        ArbiterHookDecisionEngine realEngine = _host.Engine(homeDirectory: () => root);
        ArbiterTools tools = _host.Tools(new ArbiterServeContext(
            root, realEngine, TextWriter.Null, _ => EnabledConfig(), (_, _) => Task.FromResult(false)));

        const string finding =
            "- id: security/key-in-argv\n" +
            "  severity: major\n" +
            "  file: src/Auth.cs\n" +
            "  line: 42\n" +
            "  excerpt: key := r.Query().Get(\"key\")\n" +
            "  claim: The key reaches the log.\n" +
            "  evidence: it is interpolated\n" +
            "  failure_scenario: secrets leak";
        string response = await tools.EvaluateAsync("refute.spawn", new ArbiterFacts { Finding = finding });

        // The real evaluator re-classifies from the synthesized headers and records:
        // the decision log holds the refute.spawn decision, the ledger its headers.
        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        string decisions = RecordedLines(DecisionLog.FileName);
        Assert.Contains("refute.spawn", decisions, StringComparison.Ordinal);
        string ledger = RecordedLines(InFlightLedger.FileName);
        Assert.Contains("\"refute\":\"security/key-in-argv\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefuteBatch_TwoFindingsBothAllowed_ReturnsAllowAndDecidesEachFinding()
    {
        string root = _host.RepoRoot;
        ScriptedClaimProvider provider = new("contradicts");
        ArbiterTools tools = RealEngineTools(root, provider);

        string response = await tools.EvaluateAsync(
            "refute.spawn",
            new ArbiterFacts { Finding = Finding("security/key-in-argv") + "\n" + Finding("correctness/off-by-one") });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls);
        string ledger = RecordedLines(InFlightLedger.FileName);
        Assert.Contains("\"refute\":\"security/key-in-argv\"", ledger, StringComparison.Ordinal);
        Assert.Contains("\"refute\":\"correctness/off-by-one\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefuteBatch_SecondFindingEscalates_NamesThatFindingInTheOutcome()
    {
        string root = _host.RepoRoot;
        ScriptedClaimProvider provider = new("contradicts", "supports");
        ArbiterTools tools = RealEngineTools(root, provider);

        string response = await tools.EvaluateAsync(
            "refute.spawn",
            new ArbiterFacts { Finding = Finding("security/key-in-argv") + "\n" + Finding("correctness/off-by-one") });

        Assert.DoesNotContain("outcome: allow", response, StringComparison.Ordinal);
        Assert.Contains("finding: correctness/off-by-one", response, StringComparison.Ordinal);
        Assert.DoesNotContain("finding: security/key-in-argv", response, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task RefuteBatch_ThreeFindings_ThirdEscalatesAndNamesItself()
    {
        string root = _host.RepoRoot;
        ScriptedClaimProvider provider = new("contradicts", "contradicts", "supports");
        ArbiterTools tools = RealEngineTools(root, provider);

        string response = await tools.EvaluateAsync(
            "refute.spawn",
            new ArbiterFacts
            {
                Finding = Finding("security/key-in-argv") + "\n"
                    + Finding("correctness/off-by-one") + "\n"
                    + Finding("test-adequacy/no-null-case"),
            });

        Assert.DoesNotContain("outcome: allow", response, StringComparison.Ordinal);
        Assert.Contains("finding: test-adequacy/no-null-case", response, StringComparison.Ordinal);
        Assert.Equal(3, provider.Calls);
    }

    [Theory]
    [InlineData("slash")]
    [InlineData("comma")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("no-id")]
    public async Task RefuteBatch_InvalidFindingIds_FailWithWhatToPassAndDecideNothing(string kind)
    {
        string root = _host.RepoRoot;
        ScriptedClaimProvider provider = new("contradicts");
        ArbiterTools tools = RealEngineTools(root, provider);

        string response = await tools.EvaluateAsync("refute.spawn", new ArbiterFacts { Finding = BadFindings(kind) });

        Assert.Contains("outcome: error", response, StringComparison.Ordinal);
        Assert.Contains("lens/slug", response, StringComparison.Ordinal);
        Assert.Equal(0, provider.Calls);
        Assert.False(_host.Files.DirectoryExists(Path.Combine(root, "artifacts")));
        Assert.Empty(_host.State.Log.AppendedPaths);
    }

    [Fact]
    public async Task EvaluateAllowsWhenTheHookEngineAllows()
    {
        string root = _host.RepoRoot;
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = _host.Tools(Context(root, engine));

        string response = await tools.EvaluateAsync(
            "lens.spawn",
            new ArbiterFacts { Target = "review-lens", Lens = ["security"] });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateRefusesATriggerTheFactsDoNotClassifyAs()
    {
        string root = _host.RepoRoot;
        RecordingEngine engine = new();
        ArbiterTools tools = _host.Tools(Context(root, engine));

        string response = await tools.EvaluateAsync(
            "delegate",
            new ArbiterFacts { Target = "review-lens", Lens = ["security"] });

        Assert.Contains("outcome: error", response, StringComparison.Ordinal);
        Assert.Empty(engine.PreDispatches);
    }

    [Fact]
    public async Task StatusReportsTheRootRuleSetAndKeyStateButNeverTheKey()
    {
        string root = _host.RepoRoot;
        ArbiterTools tools = _host.Tools(new ArbiterServeContext(
            root, new RecordingEngine(), TextWriter.Null, _ => RemoteConfig(), (_, _) => Task.FromResult(true)));

        string status = await tools.StatusAsync();

        Assert.Contains("rule-count: ", status, StringComparison.Ordinal);
        Assert.Contains("rule-set: ", status, StringComparison.Ordinal);
        Assert.Contains("key: found", status, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsTheEffectiveProviderAfterTheUserOverride()
    {
        // The hook resolves the key against the user override, so status must report the
        // provider and origin the key is resolved for, not the repository's (review 20.1, c).
        string root = _host.RepoRoot;
        string home = _host.InHome("override-home");
        _ = _host.Write(
            ArbiterUserSettings.GetPath(home),
            "provider:\n  model: nimble-override\n  endpoint: https://override.example.test/v2\n");
        ArbiterTools tools = _host.Tools(new ArbiterServeContext(
            root,
            new RecordingEngine(),
            TextWriter.Null,
            _ => RemoteConfig(),
            (_, _) => Task.FromResult(true),
            config => new FileArbiterUserSettings(_host.Files).ApplyTo(config.Arbiter.Provider, home)));

        string status = await tools.StatusAsync();

        Assert.Contains("model=nimble-override", status, StringComparison.Ordinal);
        Assert.Contains("origin=https://override.example.test", status, StringComparison.Ordinal);
        Assert.DoesNotContain("typesafe.example.test", status, StringComparison.Ordinal);
        Assert.DoesNotContain("jev-test", status, StringComparison.Ordinal);
    }

    private static MethodInfo ToolMethod(string toolName)
    {
        MethodInfo? method = typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Assert.True(method is not null, $"ArbiterTools must expose an MCP tool named '{toolName}'.");
        return method!;
    }

    private static DescriptionScore ScoreDescription(string toolName)
    {
        DescriptionAttribute? description = ToolMethod(toolName).GetCustomAttribute<DescriptionAttribute>();
        Assert.True(
            description is not null,
            $"The '{toolName}' tool must carry a [Description] written as routing metadata.");

        return DescriptionScorer.Score(new Skill
        {
            SkillFilePath = $"/tmp/{toolName}/SKILL.md",
            DirectoryPath = $"/tmp/{toolName}",
            Frontmatter = new SkillFrontmatter
            {
                Name = toolName,
                Description = description!.Description
            },
            RawFrontmatter = $"name: {toolName}\ndescription: {description!.Description}",
            InstructionsBody = "# Instructions\nEvaluate and read Arbiter decisions."
        });
    }
}
