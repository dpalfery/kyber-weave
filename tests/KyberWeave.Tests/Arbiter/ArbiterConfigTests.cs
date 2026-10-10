using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.2 (config): the <c>arbiter:</c> section, its defaults,
/// host overrides by id, and the <c>KW-ARB-CONFIG-001/-005/-009/-010</c> diagnostics.
/// RED: <c>KyberWeaveConfig.Arbiter</c> and <c>ArbiterConfigLoader</c> do not exist yet.
/// </summary>
public sealed class ArbiterConfigTests
{
    internal static readonly string[] ExpectedRuleIds =
    [
        "KW-ARB-PLAN-001",
        "KW-ARB-SCOPE-001",
        "KW-ARB-SCOPE-002",
        "KW-ARB-READY-001",
        "KW-ARB-OWNER-001",
        "KW-ARB-OWNER-002",
        "KW-ARB-MODE-001",
        "KW-ARB-ROSTER-001",
        "KW-ARB-DIFF-001",
        "KW-ARB-PLANNER-001",
        "KW-ARB-PLANNER-DIFF-001",
        "KW-ARB-READONLY-001",
        "KW-ARB-LENS-001",
        "KW-ARB-QUOTE-001",
        "KW-ARB-PREEX-001",
        "KW-ARB-CLAIM-001",
        "KW-ARB-GATE-CORROBORATED-001",
        "KW-ARB-GATE-001",
    ];

    [Fact]
    public void Defaults_DisabledNoneProviderAndAllShippedRulesEnabled()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults;

        Assert.False(config.Enabled);
        Assert.Equal(ArbiterProviderKind.None, config.Provider.Kind);
        Assert.Equal("https://api.typesafe.ai/v1", config.Provider.Endpoint);
        Assert.Equal("jev-1.13.0", config.Provider.Model);
        Assert.Equal(3000, config.Provider.TimeoutMs);
        // 18 shipped rule ids; ROSTER-001 runs on two triggers, so it appears twice.
        Assert.Equal(ExpectedRuleIds, config.Rules.Select(rule => rule.Id).Distinct());
        Assert.Equal(ExpectedRuleIds.Length + 1, config.Rules.Count);
        Assert.All(config.Rules, rule => Assert.True(rule.Enabled));
    }

    [Fact]
    public void AbsentSection_KeepsProductDefaults()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("ontology:\n  docs-root: docs\n");

        Assert.False(config.Arbiter.Enabled);
        Assert.Equal(ArbiterProviderKind.None, config.Arbiter.Provider.Kind);
        Assert.Equal(ExpectedRuleIds, config.Arbiter.Rules.Select(rule => rule.Id).Distinct());
    }

    [Fact]
    public void Section_MergesEnabledAndProvider()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              enabled: true
              provider:
                kind: systemone
                endpoint: http://localhost:11434/v1
                model: nimble
                timeout-ms: 1500
            """);

        Assert.True(config.Arbiter.Enabled);
        Assert.Equal(ArbiterProviderKind.Systemone, config.Arbiter.Provider.Kind);
        Assert.Equal("http://localhost:11434/v1", config.Arbiter.Provider.Endpoint);
        Assert.Equal("nimble", config.Arbiter.Provider.Model);
        Assert.Equal(1500, config.Arbiter.Provider.TimeoutMs);
        Assert.Equal(ExpectedRuleIds, config.Arbiter.Rules.Select(rule => rule.Id).Distinct());
    }

    [Fact]
    public void ProviderFields_DefaultIndividually()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              provider:
                model: tev1
            """);

        Assert.Equal(ArbiterProviderKind.None, config.Arbiter.Provider.Kind);
        Assert.Equal("https://api.typesafe.ai/v1", config.Arbiter.Provider.Endpoint);
        Assert.Equal("tev1", config.Arbiter.Provider.Model);
        Assert.Equal(3000, config.Arbiter.Provider.TimeoutMs);
    }

    [Fact]
    public void HostOverride_TunesShippedRuleById()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              rules:
                - id: KW-ARB-SCOPE-002
                  confidence-at-least: 0.85
                - id: KW-ARB-OWNER-002
                  enabled: false
            """);

        Assert.Equal(ExpectedRuleIds, config.Arbiter.Rules.Select(rule => rule.Id).Distinct());

        ArbiterRule scope = config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-SCOPE-002");
        Assert.NotNull(scope.Ask);
        Assert.Equal(0.85, scope.Ask.ConfidenceAtLeast);
        // Untouched shipped rules keep their shipped thresholds.
        ArbiterRule claim = config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-CLAIM-001");
        Assert.NotNull(claim.Ask);
        Assert.Equal(0.9, claim.Ask.ConfidenceAtLeast);

        ArbiterRule owner = config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-OWNER-002");
        Assert.False(owner.Enabled);
        Assert.True(config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-SCOPE-001").Enabled);
    }

    [Fact]
    public void HostOverride_EffectsMergePerAnswer()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              rules:
                - id: KW-ARB-SCOPE-001
                  effects: { beyond-files: escalate }
            """);

        ArbiterRule scope = config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-SCOPE-001");
        Assert.Equal("escalate", scope.Effects["beyond-files"]);
        // Answers the host did not name keep their shipped effects: lists merge per answer.
        Assert.Equal("allow", scope.Effects["in-task-files"]);
        Assert.Equal("allow", scope.Effects["no-task-files"]);
        Assert.Equal("escalate", scope.Effects["not-in-plan"]);
    }

    [Fact]
    public void HostRule_WithFullShape_IsAppendedAfterShippedRules()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              rules:
                - id: HOST-MIGRATIONS-001
                  trigger: delegate
                  question: Does the task touch database migrations?
                  answers: [touches, clear]
                  decide:
                    - when: { fact: plan.task.files, intersects: ["src/**/Migrations/**"] }
                      answer: touches
                    - when: { fact: plan.task, exists: true }
                      answer: clear
                  effects: { touches: escalate, clear: allow }
            """);

        Assert.Equal(ExpectedRuleIds.Length + 2, config.Arbiter.Rules.Count);
        ArbiterRule host = config.Arbiter.Rules[^1];
        Assert.Equal("HOST-MIGRATIONS-001", host.Id);
        Assert.Equal("delegate", host.Trigger);
        Assert.Equal(2, host.Decide!.Count);
        Assert.Equal("escalate", host.Effects["touches"]);

        // The shipped rules validate with zero errors once a host rule joins them.
        IReadOnlyList<Diagnostic> errors = RuleValidator.Validate(config.Arbiter)
            .Where(diagnostic => diagnostic.Severity == Severity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void HostOverride_NonOverridableField_RaisesConfig005WithHostRuleHint()
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml("""
                arbiter:
                  rules:
                    - id: KW-ARB-SCOPE-002
                      trigger: delegate
                """));

        Assert.Contains("KW-ARB-CONFIG-005", exception.Message, StringComparison.Ordinal);
        Assert.Contains("host rule", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostRule_ReservedPrefix_RaisesConfig005()
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml("""
                arbiter:
                  rules:
                    - id: KW-ARB-HOST-001
                      trigger: delegate
                      question: A host rule hiding behind the shipped prefix?
                      answers: [yes, no]
                      effects: { yes: allow, no: escalate }
                """));

        Assert.Contains("KW-ARB-CONFIG-005", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostRule_DuplicateId_RaisesConfig005()
    {
        YamlDotNet.Core.YamlException exception = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => KyberWeaveConfigLoader.LoadFromYaml("""
                arbiter:
                  rules:
                    - id: HOST-DUP-001
                      trigger: delegate
                      question: First?
                      answers: [yes, no]
                      effects: { yes: allow, no: escalate }
                    - id: HOST-DUP-001
                      trigger: delegate
                      question: Second?
                      answers: [yes, no]
                      effects: { yes: allow, no: escalate }
                """));

        Assert.Contains("KW-ARB-CONFIG-005", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostEntry_MissingId_RaisesConfig001NamingTheFile()
    {
        const string path = "kyber-weave.yml";
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateHostEntries(
            [new ArbiterRuleYaml { Trigger = "delegate" }],
            new HashSet<string>(ExpectedRuleIds, StringComparer.Ordinal),
            path);

        Diagnostic config001 = Assert.Single(diagnostics, d => d.Code == RuleValidator.MalformedSection);
        Assert.Equal(Severity.Error, config001.Severity);
        Assert.Contains(path, config001.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void HostEntry_MissingQuestion_RaisesConfig001()
    {
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateHostEntries(
            [new ArbiterRuleYaml { Id = "HOST-NOQ-001", Trigger = "delegate" }],
            new HashSet<string>(ExpectedRuleIds, StringComparer.Ordinal));

        Assert.Contains(diagnostics, d => d.Code == RuleValidator.MalformedSection);
    }

    [Fact]
    public void UserOverride_WithNonProviderKey_RaisesConfig009()
    {
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateUserOverride(
            new ArbiterYamlSection { Enabled = true },
            "arbiter.yml");

        Diagnostic config009 = Assert.Single(diagnostics, d => d.Code == RuleValidator.UserOverrideKey);
        Assert.Equal(Severity.Error, config009.Severity);
        Assert.Contains("provider", config009.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserOverride_ProviderOnly_Passes()
    {
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateUserOverride(
            new ArbiterYamlSection
            {
                Provider = new ArbiterProviderYaml { Model = "nimble" },
            });

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PlainHttpRemoteEndpoint_RaisesConfig010()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, "http://example.com/v1", "jev-1.13.0", 3000),
        };

        Diagnostic config010 = Assert.Single(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.PlainHttpEndpoint);
        Assert.Equal(Severity.Error, config010.Severity);
    }

    [Fact]
    public void PlainHttpLoopbackEndpoint_PassesConfig010()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, "http://localhost:11434/v1", "nimble", 3000),
        };

        Assert.DoesNotContain(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.PlainHttpEndpoint);
    }

    [Theory]
    // The endpoint checker only inspects the first octet and the second label today, so a
    // hostname that merely starts with 127. counts as loopback. It must not: plain HTTP
    // off-host would be accepted with the key resolver declining to serve a key.
    [InlineData("http://127.0.0.1.evil.com/v1")]
    [InlineData("http://127.0.evil/v1")]
    [InlineData("http://127.1.2.3.4.5/v1")]
    [InlineData("http://localhost.evil.com/v1")]
    public void PlainHttpLookalikeLoopbackEndpoint_RaisesConfig010(string endpoint)
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, endpoint, "nimble", 3000),
        };

        Assert.Contains(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.PlainHttpEndpoint);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://LocalHost:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://127.1.2.3:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:11434/v1")]
    public void PlainHttpGenuineLoopbackEndpoint_PassesConfig010(string endpoint)
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, endpoint, "nimble", 3000),
        };

        Assert.DoesNotContain(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.PlainHttpEndpoint);
    }

    [Fact]
    public void Clone_PreservesArbiterSection()
    {
        KyberWeaveConfig configured = KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              enabled: true
            """);

        KyberWeaveConfig cloned = configured.Clone();

        Assert.True(cloned.Arbiter.Enabled);
        Assert.Equal(configured.Arbiter.Rules.Count, cloned.Arbiter.Rules.Count);
    }
}
