using System.ComponentModel;
using System.Reflection;
using System.Xml.Linq;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Validation;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 17.1: <c>kyber-weave-arbiter serve</c> is a stdio MCP server for the
/// D4 fallback. It declares three tools with routing descriptions, leads every response
/// with the provenance line, resolves its root as flag, then environment, then working
/// directory, answers <c>arbiter_evaluate</c> with the hook's own outcome for the same
/// event, and never reports the key. Stdout belongs to the transport.
/// RED: <c>ArbiterTools</c> and the serve surface do not exist yet.
/// </summary>
public sealed class ArbiterMcpPackagingTests
{
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
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfig.ProductDefaults.Rules } };

    private static KyberWeaveConfig RemoteConfig() =>
        new()
        {
            Arbiter = new ArbiterConfig
            {
                Enabled = true,
                Rules = ArbiterConfig.ProductDefaults.Rules,
                Provider = new ArbiterProviderConfig(
                    ArbiterProviderKind.Systemone, "https://typesafe.example.test/v1", "jev-test", 3000),
            },
        };

    private static ArbiterServeContext Context(string root, RecordingEngine engine, bool keyResolved = false) =>
        new(root, engine, TextWriter.Null, _ => EnabledConfig(), _ => keyResolved);

    private static string FullPath(string path) => Path.GetFullPath(path);

    [Theory]
    [InlineData("arbiter_evaluate", false)]
    [InlineData("arbiter_rules", true)]
    [InlineData("arbiter_status", true)]
    public void ThreeToolsDeclareTheirAnnotations(string toolName, bool readOnly)
    {
        McpServerToolAttribute attribute = ToolMethod(toolName).GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(readOnly, attribute.ReadOnly);
        Assert.False(attribute.OpenWorld, $"{toolName} runs against a local root and must not claim an open world.");
    }

    [Fact]
    public void TheServeSurfaceDeclaresExactlyTheThreeArbiterTools()
    {
        string[] names = typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["arbiter_evaluate", "arbiter_rules", "arbiter_status"], names);
    }

    [Theory]
    [InlineData("arbiter_evaluate")]
    [InlineData("arbiter_rules")]
    [InlineData("arbiter_status")]
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
        string[] tools = ["arbiter_evaluate", "arbiter_rules", "arbiter_status"];
        string[] bounded = tools
            .Where(name => ScoreDescription(name).Components
                .Single(c => c.Name == "Negative boundary").Points > 0)
            .ToArray();

        Assert.Single(bounded);
    }

    [Fact]
    public void EveryResponseLeadsWithTheProvenanceLine()
    {
        using TempDirectory root = new();
        ArbiterTools tools = new(Context(root.Path, new RecordingEngine()));
        string prefix = $"provenance: root={FullPath(root.Path)} ";

        Assert.StartsWith(prefix, tools.Rules(), StringComparison.Ordinal);
        Assert.StartsWith(prefix, tools.Status(), StringComparison.Ordinal);
        Assert.StartsWith(
            prefix,
            tools.Evaluate("lens.spawn", new ArbiterFacts { Target = "review-lens", Lens = ["security"] }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RootResolutionPrefersTheFlagThenTheEnvironmentThenTheWorkingDirectory()
    {
        using TempDirectory flagRoot = new();
        using TempDirectory envRoot = new();
        using TempDirectory workingDirectory = new();

        Assert.Equal(
            FullPath(flagRoot.Path),
            ArbiterRootResolver.Resolve(["--repo-root", flagRoot.Path], workingDirectory.Path, envRoot.Path));
        Assert.Equal(
            FullPath(envRoot.Path),
            ArbiterRootResolver.Resolve([], workingDirectory.Path, envRoot.Path));
        Assert.Equal(
            FullPath(workingDirectory.Path),
            ArbiterRootResolver.Resolve([], workingDirectory.Path, null));
    }

    [Fact]
    public void EvaluateReturnsTheOutcomeTheHookEngineReturnsForTheSameEvent()
    {
        using TempDirectory root = new();
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Deny, "ENVELOPE-BODY") };
        ArbiterTools tools = new(Context(root.Path, engine));

        string response = tools.Evaluate(
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
    public void Evaluate_ClassifiesAFallbackRefutationCallWithFindingOnly()
    {
        // The shipped contract tells a fallback code-reviewer to pass `finding` and
        // never names `target` or the trigger header, so the tool must synthesize the
        // routing block the hook path would see (review 20.1, Major 2).
        using TempDirectory root = new();
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = new(Context(root.Path, engine));

        const string finding =
            "- id: security/key-in-argv\n" +
            "  severity: major\n" +
            "  file: src/Auth.cs\n" +
            "  line: 42\n" +
            "  excerpt: key := r.Query().Get(\"key\")\n" +
            "  claim: The key reaches the log.\n" +
            "  evidence: it is interpolated\n" +
            "  failure_scenario: secrets leak";
        string response = tools.Evaluate("refute.spawn", new ArbiterFacts { Finding = finding });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        (string harness, string caller, string? target, string prompt) = Assert.Single(engine.PreDispatches);
        Assert.Equal("code-reviewer", caller);
        Assert.Equal("review-lens", target);
        Assert.StartsWith("KYBER-ARBITER: true", prompt, StringComparison.Ordinal);
        Assert.Contains("REFUTE: security/key-in-argv", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ClassifiesAFallbackLensCallWithoutTarget()
    {
        using TempDirectory root = new();
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = new(Context(root.Path, engine));

        string response = tools.Evaluate("lens.spawn", new ArbiterFacts { Lens = ["security", "test-adequacy"] });

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
    public void Evaluate_WithoutLensOrFinding_SaysWhatToPass(string trigger)
    {
        using TempDirectory root = new();
        RecordingEngine engine = new();
        ArbiterTools tools = new(Context(root.Path, engine));

        string response = tools.Evaluate(trigger, new ArbiterFacts { Target = "review-lens" });

        Assert.Contains("outcome: error", response, StringComparison.Ordinal);
        Assert.Contains("lens", response, StringComparison.Ordinal);
        Assert.Contains("finding", response, StringComparison.Ordinal);
        Assert.Empty(engine.PreDispatches);
    }

    [Fact]
    public void Evaluate_RefutationCallThroughTheRealEngine_DecidesAndRecords()
    {
        using TempDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(root.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
        ArbiterHookDecisionEngine realEngine = new(homeDirectory: () => root.Path);
        ArbiterTools tools = new(new ArbiterServeContext(
            root.Path, realEngine, TextWriter.Null, _ => EnabledConfig(), _ => false));

        const string finding =
            "- id: security/key-in-argv\n" +
            "  severity: major\n" +
            "  file: src/Auth.cs\n" +
            "  line: 42\n" +
            "  excerpt: key := r.Query().Get(\"key\")\n" +
            "  claim: The key reaches the log.\n" +
            "  evidence: it is interpolated\n" +
            "  failure_scenario: secrets leak";
        string response = tools.Evaluate("refute.spawn", new ArbiterFacts { Finding = finding });

        // The real evaluator re-classifies from the synthesized headers and records:
        // the decision log holds the refute.spawn decision, the ledger its headers.
        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
        string decisions = File.ReadAllText(Path.Combine(root.Path, "artifacts", "arbiter", "decisions.jsonl"));
        Assert.Contains("refute.spawn", decisions, StringComparison.Ordinal);
        string ledger = File.ReadAllText(Path.Combine(root.Path, "artifacts", "arbiter", "ledger.jsonl"));
        Assert.Contains("\"refute\":\"security/key-in-argv\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateAllowsWhenTheHookEngineAllows()
    {
        using TempDirectory root = new();
        RecordingEngine engine = new() { Outcome = new HookOutcome(HookOutcomeKind.Allow) };
        ArbiterTools tools = new(Context(root.Path, engine));

        string response = tools.Evaluate(
            "lens.spawn",
            new ArbiterFacts { Target = "review-lens", Lens = ["security"] });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateRefusesATriggerTheFactsDoNotClassifyAs()
    {
        using TempDirectory root = new();
        RecordingEngine engine = new();
        ArbiterTools tools = new(Context(root.Path, engine));

        string response = tools.Evaluate(
            "delegate",
            new ArbiterFacts { Target = "review-lens", Lens = ["security"] });

        Assert.Contains("outcome: error", response, StringComparison.Ordinal);
        Assert.Empty(engine.PreDispatches);
    }

    [Fact]
    public void StatusReportsTheRootRuleSetAndKeyStateButNeverTheKey()
    {
        using TempDirectory root = new();
        ArbiterTools tools = new(new ArbiterServeContext(
            root.Path, new RecordingEngine(), TextWriter.Null, _ => RemoteConfig(), _ => true));

        string status = tools.Status();

        Assert.Contains("rule-count: ", status, StringComparison.Ordinal);
        Assert.Contains("rule-set: ", status, StringComparison.Ordinal);
        Assert.Contains("key: found", status, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", status, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusReportsTheEffectiveProviderAfterTheUserOverride()
    {
        // The hook resolves the key against the user override, so status must report the
        // provider and origin the key is resolved for, not the repository's (review 20.1, c).
        using TempDirectory root = new();
        using TempDirectory home = new();
        Directory.CreateDirectory(Path.GetDirectoryName(ArbiterUserSettings.GetPath(home.Path))!);
        File.WriteAllText(
            ArbiterUserSettings.GetPath(home.Path),
            "provider:\n  model: nimble-override\n  endpoint: https://override.example.test/v2\n");
        ArbiterTools tools = new(new ArbiterServeContext(
            root.Path,
            new RecordingEngine(),
            TextWriter.Null,
            _ => RemoteConfig(),
            _ => true,
            config => ArbiterUserSettings.ApplyTo(config.Arbiter.Provider, home.Path)));

        string status = tools.Status();

        Assert.Contains("model=nimble-override", status, StringComparison.Ordinal);
        Assert.Contains("origin=https://override.example.test", status, StringComparison.Ordinal);
        Assert.DoesNotContain("typesafe.example.test", status, StringComparison.Ordinal);
        Assert.DoesNotContain("jev-test", status, StringComparison.Ordinal);
    }

    [Fact]
    public void McpSourcesNeverWriteToStdout()
    {
        string directory = Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Arbiter", "Mcp");

        foreach (string file in Directory.GetFiles(directory, "*.cs"))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("Console.Write", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Console.Out", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArbiterProjectPinsTheMcpPackagesToTheVersionsTheMcpProjectUses()
    {
        XDocument doc = XDocument.Load(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Arbiter", "KyberWeave.Arbiter.csproj"));
        XNamespace ns = doc.Root!.Name.Namespace;

        Assert.Equal("2.2.0", PackageVersion(doc, ns, "ModelContextProtocol"));
        Assert.Equal("10.0.12", PackageVersion(doc, ns, "Microsoft.Extensions.Hosting"));
    }

    private static string? PackageVersion(XDocument doc, XNamespace ns, string name) =>
        doc.Descendants(ns + "PackageReference")
            .Where(element => string.Equals((string?)element.Attribute("Include"), name, StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Version"))
            .SingleOrDefault();

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
