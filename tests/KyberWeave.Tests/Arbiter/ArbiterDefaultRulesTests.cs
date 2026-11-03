using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.2 (shipped rules): the 18 rules in the embedded
/// <c>default-rules.yml</c> equal the catalogue, validate with zero errors, and carry
/// the <c>OWNER-001</c> map, the <c>LENS-001</c> path table and the tuned thresholds.
/// Also covers <c>KW-ARB-CONFIG-002/-003/-004/-006/-007/-008</c>.
/// RED: the embedded rules resource and <c>RuleValidator</c> do not exist yet.
/// </summary>
public sealed class ArbiterDefaultRulesTests
{
    private static readonly string[] ExpectedRuleIds =
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
    public void ShippedRules_EqualTheCatalogue()
    {
        Assert.Equal(ExpectedRuleIds, ArbiterConfig.ProductDefaults.Rules.Select(rule => rule.Id).Distinct());
    }

    [Fact]
    public void ShippedRules_ValidateWithZeroErrors()
    {
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.Validate(ArbiterConfig.ProductDefaults);

        List<Diagnostic> errors = diagnostics.Where(diagnostic => diagnostic.Severity == Severity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void ProviderNone_WarnsThatModelRulesAreSwitchedOff()
    {
        Diagnostic config006 = Assert.Single(
            RuleValidator.Validate(ArbiterConfig.ProductDefaults),
            d => d.Code == RuleValidator.ModelRulesSwitchedOff);
        Assert.Equal(Severity.Warning, config006.Severity);
    }

    [Fact]
    public void EveryStep1Threshold_IsTunedForJev1130()
    {
        IReadOnlyList<ArbiterRule> step1 = ArbiterConfig.ProductDefaults.Rules
            .Where(rule => rule.Ask is not null)
            .ToList();

        Assert.NotEmpty(step1);
        Assert.All(step1, rule => Assert.Contains("jev-1.13.0", rule.Ask!.TunedFor ?? []));
    }

    [Fact]
    public void Scope001_And_Diff001_DeclareNoTaskFiles()
    {
        foreach (string id in new[] { "KW-ARB-SCOPE-001", "KW-ARB-DIFF-001" })
        {
            ArbiterRule rule = ArbiterConfig.ProductDefaults.Rules.Single(r => r.Id == id);
            Assert.Contains("no-task-files", rule.Answers);
            Assert.Equal("allow", rule.Effects["no-task-files"]);
        }
    }

    [Fact]
    public void Mode001_HasNoMissingContractAnswer()
    {
        ArbiterRule mode = ArbiterConfig.ProductDefaults.Rules.Single(r => r.Id == "KW-ARB-MODE-001");

        Assert.DoesNotContain("missing-contract", mode.Answers);
        Assert.Equal(["not-required", "present", "missing", "returns-unobservable"], mode.Answers);
    }

    [Fact]
    public void Owner001_MapMatchesTheSpecTableInOrder()
    {
        IReadOnlyList<ArbiterFileKindMapEntry> map = ArbiterConfig.ProductDefaults.OwnerFileKindMap;

        (string[] Patterns, string Owner)[] expected =
        [
            (["**/*.tsx", "**/*.jsx"], "react-dev"),
            (["**/src-tauri/**", "**/*.rs"], "tauri-dev"),
            (["**/*.py", "**/pyproject.toml", "**/requirements*.txt"], "python-dev"),
            ([".github/workflows/**", "**/Dockerfile"], "github-devops"),
            (["**/*.sql", "**/*.sqlproj"], "sql-database-architect"),
            (["**/Migrations/**", "**/*Repository.cs"], "dal-dev"),
            (["**/*.xaml"], "maui-dev"),
            (["**/Pulumi.yaml", "**/Pulumi.*.yaml"], "pulumi-dev"),
            (["tests/**", "**/*.test.*", "**/*.spec.*", "**/*Tests.cs"], "test-dev"),
            (["**/*.md"], "docs-dev"),
            (["**/*.cs"], "csharp-dev"),
        ];

        Assert.Equal(expected.Length, map.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Patterns, map[i].Patterns);
            Assert.Equal(expected[i].Owner, map[i].Owner);
        }
    }

    [Fact]
    public void Owner001_MapIsFirstMatchWins()
    {
        // tests/foo.md matches both tests/** (test-dev) and **/*.md (docs-dev):
        // the earlier row wins, which is what "first match wins" means.
        string owner = FirstMapOwner("tests/foo.md");

        Assert.Equal("test-dev", owner);
        Assert.Equal("react-dev", FirstMapOwner("src/ui/App.tsx"));
        Assert.Equal("csharp-dev", FirstMapOwner("src/Impl.cs"));
    }

    [Fact]
    public void Owner001_DecidesSkillsLabelBeforeFileKinds()
    {
        ArbiterRule owner = ArbiterConfig.ProductDefaults.Rules.Single(r => r.Id == "KW-ARB-OWNER-001");

        Assert.NotNull(owner.Decide);
        // The Skills-label owner clause leads; the file-kind fallback answers no-mapping last.
        Assert.Equal("owner", owner.Decide[0].Answer);
        Assert.Equal("no-mapping", owner.Decide[^1].Answer);
        Assert.Contains(owner.Decide, clause => string.Equals(clause.Answer, "not-owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Lens001_PathTableHoldsOnlyApplicabilityStatedConditions()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults;

        Assert.NotEmpty(config.LensPaths);
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        foreach (ArbiterLensPathEntry entry in config.LensPaths)
        {
            // Every tabled lens must ground its path condition in its own Applicability text:
            // the only path language any Applicability states is documentation-only.
            string applicability = catalog.Lenses[entry.Lens].Applicability;
            Assert.Contains("documentation", applicability, StringComparison.OrdinalIgnoreCase);
        }

        // Lenses whose Applicability states no path condition defer to step 1 instead.
        Assert.DoesNotContain(config.LensPaths, entry => entry.Lens == "security");
        Assert.DoesNotContain(config.LensPaths, entry => entry.Lens == "dependency-supply-chain");

        ArbiterRule lens = config.Rules.Single(r => r.Id == "KW-ARB-LENS-001");
        Assert.NotNull(lens.Ask);
        Assert.Equal("lens:", lens.Ask.InstructionsFrom);
        Assert.Equal(0.1, lens.Ask.ProbabilityBelow);
        Assert.Contains("jev-1.13.0", lens.Ask.TunedFor ?? []);
    }

    [Fact]
    public void UnknownFact_RaisesConfig002WithNearestHint()
    {
        ArbiterRule rule = new ArbiterRule(
            "HOST-TYPO-001",
            "delegate",
            "Typo question?",
            ["yes", "no"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["yes"] = "allow", ["no"] = "escalate" },
            [new RuleDecideClause(RulePredicate.Exists("plan.taks", true), "yes")]);

        ArbiterConfig config = ArbiterConfig.ProductDefaults with { Rules = [.. ArbiterConfig.ProductDefaults.Rules, rule] };

        Diagnostic config002 = Assert.Single(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.UnknownFact);
        Assert.Equal(Severity.Error, config002.Severity);
        Assert.Contains("plan.task", config002.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLens_RaisesConfig003WithNearestHint()
    {
        ArbiterRule rule = Shipped("KW-ARB-CLAIM-001") with
        {
            Ask = Shipped("KW-ARB-CLAIM-001").Ask! with { InstructionsFrom = "lens:securty" },
        };
        ArbiterConfig config = ReplaceRule(rule);

        Diagnostic config003 = Assert.Single(
            RuleValidator.Validate(config),
            d => d.Code == RuleValidator.UnknownLens);
        Assert.Equal(Severity.Error, config003.Severity);
        Assert.Contains("security", config003.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerWithoutEffect_RaisesConfig004()
    {
        ArbiterRule rule = Shipped("KW-ARB-SCOPE-001") with
        {
            Effects = new Dictionary<string, string>(Shipped("KW-ARB-SCOPE-001").Effects, StringComparer.Ordinal)
            {
                ["in-task-files"] = "allow",
            },
        };
        // Drop every other answer mapping so three answers have no effect.
        rule = rule with
        {
            Effects = new Dictionary<string, string>(StringComparer.Ordinal) { ["in-task-files"] = "allow" },
        };

        Diagnostic config004 = Assert.Single(
            RuleValidator.Validate(ReplaceRule(rule)),
            d => d.Code == RuleValidator.AnswerWithoutEffect);
        Assert.Equal(Severity.Error, config004.Severity);
    }

    [Fact]
    public void ForeignEffect_RaisesConfig008()
    {
        ArbiterRule rule = Shipped("KW-ARB-SCOPE-001") with
        {
            Effects = new Dictionary<string, string>(Shipped("KW-ARB-SCOPE-001").Effects, StringComparer.Ordinal)
            {
                ["in-task-files"] = "skip",
            },
        };

        Diagnostic config008 = Assert.Single(
            RuleValidator.Validate(ReplaceRule(rule)),
            d => d.Code == RuleValidator.ForeignEffect);
        Assert.Equal(Severity.Error, config008.Severity);
    }

    [Fact]
    public void UntunedModel_RaisesConfig007Warning()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, "https://api.typesafe.ai/v1", "nimble", 3000),
        };

        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.CheckModelTuning(config);

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(RuleValidator.UntunedModel, d.Code));
        Assert.All(diagnostics, d => Assert.Equal(Severity.Warning, d.Severity));
    }

    [Fact]
    public void TunedModel_PassesConfig007()
    {
        ArbiterConfig config = ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, "https://api.typesafe.ai/v1", "jev-1.13.0", 3000),
        };

        List<Diagnostic> remaining = RuleValidator.CheckModelTuning(config).ToList();
        Assert.Empty(remaining);
    }

    private static string FirstMapOwner(string path)
    {
        foreach (ArbiterFileKindMapEntry entry in ArbiterConfig.ProductDefaults.OwnerFileKindMap)
        {
            if (entry.Patterns.Any(pattern => PathGlob.IsMatch(pattern, path)))
                return entry.Owner;
        }

        return "no-mapping";
    }

    private static ArbiterRule Shipped(string id) =>
        ArbiterConfig.ProductDefaults.Rules.Single(rule => rule.Id == id);

    private static ArbiterConfig ReplaceRule(ArbiterRule replacement) =>
        ArbiterConfig.ProductDefaults with
        {
            Rules = ArbiterConfig.ProductDefaults.Rules
                .Select(rule => string.Equals(rule.Id, replacement.Id, StringComparison.Ordinal) ? replacement : rule)
                .ToList(),
        };
}
