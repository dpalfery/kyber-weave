using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 4.2: the Arbiter reads a host's role folders over the shipped ones, adds the rules of the
/// task's technology and its parent, and applies the host's <c>arbiter.rules</c> settings last. A
/// folder file that cannot be read or holds an invalid rule is reported by <c>arbiter validate</c>
/// and the shipped rules are used for that folder.
/// </summary>
public sealed class ArbiterRuleFolderLoadingTests : IDisposable
{
    private const string HostScopeRule = """
        rules:
          - id: KW-ARB-SCOPE-001
            trigger: delegate
            question: Does the host's own scope question hold?
            answers: [in-task, beyond-task]
            decide:
              - when: { fact: plan.task, exists: true }
                answer: in-task
            effects: { in-task: allow, beyond-task: escalate }
        """;

    private const string ParentReadRule = """
        rules:
          - id: typescript-read-001
            trigger: delegate
            question: Is this dispatch a read of a TypeScript module?
            answers: [yes, no]
            decide:
              - when: { fact: delegation.prompt, matches: "*module*" }
                answer: yes
            effects: { yes: allow, no: allow }
        """;

    private const string ChildReadRule = """
        rules:
          - id: react-read-001
            trigger: delegate
            question: Is this dispatch a read of a React component?
            answers: [yes, no]
            decide:
              - when: { fact: delegation.prompt, matches: "*component*" }
                answer: yes
            effects: { yes: allow, no: allow }
        """;

    private const string SharedIdParentRule = """
        rules:
          - id: host-shared-001
            trigger: delegate
            question: Does the parent's shared question hold?
            answers: [yes, no]
            effects: { yes: allow, no: allow }
        """;

    private const string SharedIdChildRule = """
        rules:
          - id: host-shared-001
            trigger: delegate
            question: Does the child's shared question hold?
            answers: [yes, no]
            effects: { yes: allow, no: allow }
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Root => _temp.Path;

    [Fact]
    public void TheShippedRuleSetLoadsUnchangedWhenTheHostHasNoFolderOfItsOwn()
    {
        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Assert.Empty(findings);
        AssertShippedRules(rules);
        string[] ids = [.. rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal)];
        Assert.Equal(21, ids.Count(id => RoleOf(id) == "conductor"));
        Assert.Equal(2, ids.Count(id => RoleOf(id) == "architect"));
        Assert.Equal(13, ids.Count(id => RoleOf(id) == "reviewer"));
    }

    [Fact]
    public void AHostRoleFolderReplacesTheShippedRuleOfTheSameId()
    {
        string path = WriteFolder("conductor", HostScopeRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic notice = Assert.Single(findings);
        Assert.Equal(RuleValidator.ShippedRuleOverride, notice.Code);
        Assert.Equal(Severity.Info, notice.Severity);
        Assert.Equal("KW-ARB-SCOPE-001", notice.Subject);
        Assert.Equal(path, notice.FilePath);
        ArbiterRule replaced = Assert.Single(rules, rule => rule.Id == "KW-ARB-SCOPE-001");
        Assert.Equal("Does the host's own scope question hold?", replaced.Question);
        Assert.Equal(["in-task", "beyond-task"], replaced.Answers);
        Assert.Equal(ArbiterConfig.ProductDefaults.Rules.Count, rules.Count);
    }

    [Fact]
    public void ADisabledReplacementOfAShippedRuleIsReportedAndTakesEffect()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: KW-ARB-SCOPE-002
                trigger: delegate
                question: Does the delegation ask for work the plan task does not describe?
                answers: [yes, no]
                effects: { yes: allow, no: escalate }
                enabled: false
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic notice = Assert.Single(findings);
        Assert.Equal(RuleValidator.ShippedRuleOverride, notice.Code);
        Assert.Equal("KW-ARB-SCOPE-002", notice.Subject);
        Assert.Equal(path, notice.FilePath);
        Assert.False(Assert.Single(rules, rule => rule.Id == "KW-ARB-SCOPE-002").Enabled);
    }

    [Fact]
    public void AFolderRuleWithTheReservedPrefixIsRefused()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: KW-ARB-HOST-ONLY-001
                trigger: delegate
                question: Is this dispatch a host-only read?
                answers: [yes, no]
                effects: { yes: allow, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.DuplicateOrReservedId);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        Assert.Equal("KW-ARB-HOST-ONLY-001", finding.Subject);
        AssertShippedRules(rules);
    }

    [Fact]
    public void AFolderRuleMappingAShippedOnlyEffectIsRefused()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: host-effect-001
                trigger: delegate
                question: Is this dispatch a host-only read?
                answers: [yes, no]
                effects: { yes: keep, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.SavingsValueInvalid);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        AssertShippedRules(rules);
    }

    [Fact]
    public void AFolderRuleSettingForEachAtRuleLevelIsRefused()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: host-foreach-001
                trigger: delegate
                question: Is this dispatch a host-only read?
                answers: [yes, no]
                for-each: plan.task.files
                effects: { yes: allow, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.SavingsValueInvalid);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        AssertShippedRules(rules);
    }

    [Fact]
    public void ADuplicateIdInOneFileIsReportedAndTheLastDeclarationWins()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: host-dup-001
                trigger: delegate
                question: Is this the first declaration?
                answers: [yes, no]
                effects: { yes: allow, no: allow }
              - id: host-dup-001
                trigger: delegate
                question: Is this the second declaration?
                answers: [yes, no]
                effects: { yes: allow, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.DuplicateFolderRule);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("host-dup-001", finding.Subject);
        Assert.Equal(path, finding.FilePath);
        Assert.Equal(
            "Is this the second declaration?",
            Assert.Single(rules, rule => rule.Id == "host-dup-001").Question);
    }

    [Fact]
    public void AnUnknownTopLevelKeyIsReportedAndTheFileStillLoadsItsRules()
    {
        string path = WriteFolder("conductor", """
            rule:
              - id: host-typo-001
            rules:
              - id: host-ok-001
                trigger: delegate
                question: Is this dispatch a host read?
                answers: [yes, no]
                effects: { yes: allow, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.UnknownRuleFolderKey);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("rule", finding.Subject);
        Assert.Equal(path, finding.FilePath);
        Assert.Contains(rules, rule => rule.Id == "host-ok-001");
    }

    [Fact]
    public void AnUnknownRuleKeyIsReportedAndTheRuleStillLoads()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: host-unknown-key-001
                trigger: delegate
                question: Is this dispatch a host read?
                answers: [yes, no]
                sevrity: high
                effects: { yes: allow, no: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.UnknownRuleFolderKey);
        Assert.Equal("sevrity", finding.Subject);
        Assert.Equal(path, finding.FilePath);
        Assert.Contains(rules, rule => rule.Id == "host-unknown-key-001");
    }

    [Fact]
    public void TestFilePatternsAndLensPathsInAFolderFileAreKept()
    {
        WriteFolder("conductor", """
            test-file-patterns: ["**/*.checks.cs"]
            lens-paths:
              - lens: supportability
                not-applicable-when-all-match: ["docs/**"]
            rules:
              - id: host-ok-002
                trigger: delegate
                question: Is this dispatch a host read?
                answers: [yes, no]
                effects: { yes: allow, no: allow }
            """);

        ArbiterConfig config = LoadConfig("arbiter:\n  enabled: true\n", technologies: []);

        Assert.Contains("**/*.checks.cs", config.TestFilePatterns);
        Assert.Contains(config.LensPaths, entry => entry.Lens == "supportability");
        Assert.DoesNotContain(config.RuleFolderFindings, item => item.Code == RuleValidator.UnknownRuleFolderKey);
    }

    [Fact]
    public void AParentsRulesLoadBeforeItsChildsEvenWhenTheHostListsOnlyTheChild()
    {
        WriteStandard("typescript", buildsOn: null);
        WriteFolder("typescript", ParentReadRule);
        WriteStandard("react", buildsOn: "typescript");
        WriteFolder("react", ChildReadRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: ["react"]);

        Assert.Empty(findings);
        List<string> ids = [.. rules.Select(rule => rule.Id)];
        Assert.Contains("typescript-read-001", ids);
        Assert.Contains("react-read-001", ids);
        Assert.True(
            ids.IndexOf("typescript-read-001") < ids.IndexOf("react-read-001"),
            "The parent's rules must be loaded before the child's.");
    }

    [Fact]
    public void AChildsRuleWinsOnAnIdTheParentDeclaresToo()
    {
        WriteStandard("typescript", buildsOn: null);
        WriteFolder("typescript", SharedIdParentRule);
        WriteStandard("react", buildsOn: "typescript");
        WriteFolder("react", SharedIdChildRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: ["react"]);

        Assert.Empty(findings);
        Assert.Equal(
            "Does the child's shared question hold?",
            Assert.Single(rules, rule => rule.Id == "host-shared-001").Question);
    }

    [Fact]
    public void AMissingParentIsAFindingAndTheChildsRulesStillLoad()
    {
        WriteStandard("react", buildsOn: "ghost");
        WriteFolder("react", ChildReadRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: ["react"]);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.MissingRuleFolderParent);
        Assert.Equal("ghost", finding.Subject);
        Assert.EndsWith("react.standards.md", finding.FilePath, StringComparison.Ordinal);
        Assert.Contains(rules, rule => rule.Id == "react-read-001");
    }

    [Fact]
    public void ACycleInBuildsOnIsAFindingAndEachFolderLoadsOnce()
    {
        WriteStandard("alpha", buildsOn: "beta");
        WriteFolder("alpha", ParentReadRule);
        WriteStandard("beta", buildsOn: "alpha");
        WriteFolder("beta", ChildReadRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: ["alpha"]);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.RuleFolderCycle);
        Assert.EndsWith("beta.standards.md", finding.FilePath, StringComparison.Ordinal);
        Assert.Single(rules, rule => rule.Id == "typescript-read-001");
        Assert.Single(rules, rule => rule.Id == "react-read-001");
    }

    [Fact]
    public void AnUnreadableFolderFileIsReportedAndTheShippedRulesAreUsed()
    {
        string path = Path.Combine(Root, "docs", "standards", "conductor", "conductor.arbiter-rules.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) = Load(
            "arbiter:\n  enabled: true\n",
            technologies: [],
            candidate => string.Equals(candidate, path, StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("permission denied")
                : ReadTestFile(candidate));

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.RuleFolderInvalid);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        Assert.Contains("permission denied", finding.Message, StringComparison.Ordinal);
        AssertShippedRules(rules);
    }

    [Fact]
    public void ANullRuleEntryIsReportedAndTheRepositoryConfigurationStillLoads()
    {
        string path = WriteFolder("conductor", "rules:\n  -\n");
        WriteConfig("ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n");

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Root);

        Diagnostic finding = Assert.Single(
            config.Arbiter.RuleFolderFindings,
            item => item.Code == RuleValidator.RuleFolderInvalid);
        Assert.Equal(path, finding.FilePath);
        AssertShippedRules(config.Arbiter.Rules);
    }

    [Fact]
    public void TheHostsArbiterRulesSettingsApplyAfterTheFolders()
    {
        WriteFolder("conductor", HostScopeRule);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) = Load(
            """
            arbiter:
              enabled: true
              rules:
                - id: KW-ARB-SCOPE-001
                  shadow: true
                - id: KW-ARB-SCOPE-002
                  enabled: false
            """,
            technologies: []);

        Assert.Single(findings, item => item.Code == RuleValidator.ShippedRuleOverride);
        Assert.True(Assert.Single(rules, rule => rule.Id == "KW-ARB-SCOPE-001").Shadow);
        Assert.Equal("Does the host's own scope question hold?", rules.Single(rule => rule.Id == "KW-ARB-SCOPE-001").Question);
        Assert.False(Assert.Single(rules, rule => rule.Id == "KW-ARB-SCOPE-002").Enabled);
    }

    [Fact]
    public void AFileThatDoesNotParseIsReportedAndTheShippedRulesAreUsed()
    {
        string path = WriteFolder("conductor", "rules:\n  - id: [unclosed\n");

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Code == RuleValidator.RuleFolderInvalid);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        AssertShippedRules(rules);
    }

    [Fact]
    public void AFileNamingAnUnknownTriggerIsReportedAndTheShippedRulesAreUsed()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: KW-ARB-SCOPE-001
                trigger: no-such-trigger
                question: Is this dispatch within scope?
                answers: [yes]
                decide:
                  - when: { fact: plan.task, exists: true }
                    answer: yes
                effects: { yes: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Subject == "KW-ARB-SCOPE-001");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        Assert.Contains("no-such-trigger", finding.Message, StringComparison.Ordinal);
        AssertShippedRules(rules);
    }

    [Fact]
    public void AFileNamingAnUnknownFactIsReportedAndTheShippedRulesAreUsed()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: KW-ARB-SCOPE-001
                trigger: delegate
                question: Is this dispatch within scope?
                answers: [yes]
                decide:
                  - when: { fact: no.such.fact, exists: true }
                    answer: yes
                effects: { yes: allow }
            """);

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: true\n", technologies: []);

        Diagnostic finding = Assert.Single(findings, item => item.Subject == "KW-ARB-SCOPE-001");
        Assert.Equal(RuleValidator.UnknownFact, finding.Code);
        Assert.Equal(path, finding.FilePath);
        Assert.Contains("no.such.fact", finding.Message, StringComparison.Ordinal);
        AssertShippedRules(rules);
    }

    [Fact]
    public void ArbiterValidateReportsABadFolderFileAndKeepsTheShippedRule()
    {
        string path = WriteFolder("conductor", """
            rules:
              - id: KW-ARB-SCOPE-001
                trigger: delegate
                question: Is this dispatch within scope?
                answers: [yes]
                decide:
                  - when: { fact: no.such.fact, exists: true }
                    answer: yes
                effects: { yes: allow }
            """);
        WriteConfig("ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n");

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Root);
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.Validate(config.Arbiter, KyberWeaveConfigLoader.FindConfigPath(Root));

        Diagnostic finding = Assert.Single(diagnostics, item => item.Subject == "KW-ARB-SCOPE-001");
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(path, finding.FilePath);
        Assert.Equal(
            "Is the work this dispatch asks for one of the plan's tasks?",
            config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-SCOPE-001").Question);
    }

    [Fact]
    public void AHostFolderAppliesWhenTheRepositoryConfigurationIsLoaded()
    {
        WriteFolder("conductor", HostScopeRule);
        WriteConfig("ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n");

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Root);

        Assert.Equal(
            "Does the host's own scope question hold?",
            config.Arbiter.Rules.Single(rule => rule.Id == "KW-ARB-SCOPE-001").Question);
    }

    [Fact]
    public void ADeclaredTechnologysFolderAppliesWhenTheRepositoryConfigurationIsLoaded()
    {
        WriteStandard("react", buildsOn: null);
        WriteFolder("react", ChildReadRule);
        WriteConfig("""
            ontology:
              docs-root: docs
              technologies: [react, typescript]
            arbiter:
              enabled: true
            """);

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Root);

        Assert.Contains(config.Arbiter.Rules, rule => rule.Id == "react-read-001");
    }

    [Fact]
    public void WithTheArbiterOffNoFolderIsReadAndTheRunWorks()
    {
        WriteFolder("conductor", "rules:\n  - id: [unclosed\n");

        (IReadOnlyList<ArbiterRule> rules, IReadOnlyList<Diagnostic> findings) =
            Load("arbiter:\n  enabled: false\n", technologies: ["react"]);

        Assert.Empty(findings);
        AssertShippedRules(rules);
    }

    /// <summary>
    /// Asserts the effective set is the shipped one: the same ids and triggers in the same order, and each rule's
    /// question, answers, enabled state and effects. The shadow flag is left out because
    /// <c>arbiter.model-targeting.mode</c> owns it for two rules and every merge may set it.
    /// </summary>
    private static void AssertShippedRules(IReadOnlyList<ArbiterRule> rules)
    {
        Assert.Equal(
            ArbiterConfig.ProductDefaults.Rules.Select(rule => (rule.Id, rule.Trigger)),
            rules.Select(rule => (rule.Id, rule.Trigger)));

        foreach (ArbiterRule shipped in ArbiterConfig.ProductDefaults.Rules)
        {
            ArbiterRule loaded = rules.Single(
                rule => string.Equals(rule.Id, shipped.Id, StringComparison.Ordinal)
                        && string.Equals(rule.Trigger, shipped.Trigger, StringComparison.Ordinal));
            Assert.Equal(shipped.Question, loaded.Question);
            Assert.Equal(shipped.Answers, loaded.Answers);
            Assert.Equal(shipped.Enabled, loaded.Enabled);
            Assert.Equal(
                shipped.Effects.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                loaded.Effects.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
    }

    /// <summary>Writes one scope folder's Arbiter rules file and returns its path.</summary>
    private string WriteFolder(string name, string body)
    {
        string directory = Path.Combine(Root, "docs", "standards", name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.arbiter-rules.yml");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>Writes one scope folder's standard, with an optional <c>builds-on</c> parent.</summary>
    private string WriteStandard(string name, string? buildsOn)
    {
        string directory = Path.Combine(Root, "docs", "standards", name);
        Directory.CreateDirectory(directory);
        string parent = buildsOn is null ? string.Empty : $"builds-on: {buildsOn}\n";
        string path = Path.Combine(directory, $"{name}.standards.md");
        File.WriteAllText(path, $"---\nid: standards/{name}\n{parent}---\n\n# {name}\n");
        return path;
    }

    private void WriteConfig(string yaml)
    {
        string directory = Path.Combine(Root, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml);
    }

    private (IReadOnlyList<ArbiterRule> Rules, IReadOnlyList<Diagnostic> Findings) Load(
        string yaml,
        IReadOnlyList<string> technologies,
        Func<string, string?>? readFile = null)
    {
        ArbiterConfig merged = LoadConfig(yaml, technologies, readFile);
        return (merged.Rules, merged.RuleFolderFindings);
    }

    private ArbiterConfig LoadConfig(
        string yaml,
        IReadOnlyList<string> technologies,
        Func<string, string?>? readFile = null)
    {
        ArbiterYamlSection? section = KyberWeaveYamlParser.Parse(yaml).Arbiter;
        return ArbiterConfigLoader.Merge(
            ArbiterConfig.ProductDefaults,
            section,
            new ArbiterConfigLoader.RuleFolderRequest(Root, ["docs"], technologies, readFile ?? ReadTestFile));
    }

    /// <summary>Reads a file for the loader; the domain layer takes this as a delegate so it touches no disk.</summary>
    private static string? ReadTestFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static string RoleOf(string id) => id switch
    {
        "KW-ARB-PLANNER-001" or "KW-ARB-PLANNER-DIFF-001" => "architect",
        "KW-ARB-LENS-001" or "KW-ARB-LENS-002" or "KW-ARB-QUOTE-001" or "KW-ARB-PREEX-001" or
            "KW-ARB-CLAIM-001" or "KW-ARB-GATE-CORROBORATED-001" or "KW-ARB-GATE-001" or
            "KW-ARB-NITPICK-001" or "KW-ARB-NITPICK-002" or "KW-ARB-DUP-001" or
            "KW-ARB-COMMENT-001" or "KW-ARB-SEVERITY-001" or "KW-ARB-OVERRIDE-001" => "reviewer",
        _ => "conductor",
    };
}
