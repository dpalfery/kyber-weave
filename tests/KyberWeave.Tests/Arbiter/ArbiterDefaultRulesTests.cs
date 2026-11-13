using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.2 (shipped rules): the shipped rules in the embedded
/// <c>default-rules.yml</c> equal the catalogue, validate with zero errors, and carry
/// the <c>OWNER-001</c> map, the <c>LENS-001</c> path table and the tuned thresholds.
/// Also covers <c>KW-ARB-CONFIG-002/-003/-004/-006/-007/-008</c>.
/// RED: the embedded rules resource and <c>RuleValidator</c> do not exist yet.
/// </summary>
/// <remarks>
/// The catalogue below is ordered, so it pins two things at once: the ids, which are
/// permanent, and their position in the file, which keeps a new rule out of the middle of
/// <c>rules:</c> where a reader would take it for a renumbering. A new id is therefore
/// appended, never inserted.
/// </remarks>
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
        "KW-ARB-ROSTER-001",
        "KW-ARB-RUN-001",
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
        "KW-ARB-PLANREAD-001",
        "KW-ARB-GATE-REPORT-001",
        "KW-ARB-LENS-002",
        "KW-ARB-DIFFICULTY-001",
        "KW-ARB-LADDER-001",
        "KW-ARB-NITPICK-001",
        "KW-ARB-NITPICK-002",
        "KW-ARB-DUP-001",
        "KW-ARB-TRIAGE-001",
        "KW-ARB-INTAKE-001",
        "KW-ARB-DOCIMPACT-001",
        "KW-ARB-FAILURE-001",
        "KW-ARB-TRIM-001",
        "KW-ARB-COMMENT-001",
        "KW-ARB-CHECKOUT-001",
        "KW-ARB-MODE-002",
    ];

    [Fact]
    public void ShippedRules_EqualTheCatalogue()
    {
        Assert.Equal(ExpectedRuleIds, ArbiterConfig.ProductDefaults.Rules.Select(rule => rule.Id).Distinct());
    }

    /// <summary>
    /// The shortcut (task 3.7) and same-failure (task 3.9) rules ship in shadow: an upgrade withholds no worker
    /// and gives no advisory until the calibration has been run. Failure this guards: an enforced default
    /// denies investigations on the first upgrade.
    /// </summary>
    [Fact]
    public void TheShortcutAndSameFailureRulesShipInShadow()
    {
        string[] newIds = ["KW-ARB-TRIAGE-001", "KW-ARB-INTAKE-001", "KW-ARB-DOCIMPACT-001", "KW-ARB-FAILURE-001"];

        Assert.All(newIds, id => Assert.True(Shipped(id).Shadow, id));
    }

    /// <summary>
    /// The section-trimming (task 3.11) and bot-comment triage (task 3.13) rules ship in shadow: they only log what they
    /// would have done until calibration is read. Failure this guards: an enforced default trims a worker's context or
    /// classifies a real review comment as a nitpick before anyone has measured the rule.
    /// </summary>
    [Fact]
    public void TheSectionTrimAndCommentRulesShipInShadow()
    {
        string[] newIds = ["KW-ARB-TRIM-001", "KW-ARB-COMMENT-001"];

        Assert.All(newIds, id => Assert.True(Shipped(id).Shadow, id));
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
    public void Mode002_IsDecidedFromMachineFactsAloneAndLogsAMissingFailingRunWithoutStopping()
    {
        ArbiterRule mode = ArbiterConfig.ProductDefaults.Rules.Single(r => r.Id == "KW-ARB-MODE-002");

        Assert.Equal("delegate.returned", mode.Trigger);
        Assert.Equal(["no-task", "not-required", "failing-run-first", "no-failing-run"], mode.Answers);
        // No model question: the proof is two reports on disk, so an unreadable fact is undecidable and allows (ADR 0072).
        Assert.Null(mode.Ask);
        // A missing failing run is recorded, not a stop: a worker cannot produce one after implementing (ADR 0076).
        Assert.All(mode.Effects.Values, effect => Assert.Equal("allow", effect));
        Assert.Equal("no-failing-run", mode.Decide![^1].Answer);
    }

    [Fact]
    public void Mode001_IsRetiredAndOwner001NoLongerHandsTestFirstTasksToTestDev()
    {
        Assert.DoesNotContain(ArbiterConfig.ProductDefaults.Rules, r => r.Id == "KW-ARB-MODE-001");
        Assert.DoesNotContain("ledger.red-evidence", ArbiterTriggerCatalog.FactsByTrigger["delegate"]);

        ArbiterRule owner = ArbiterConfig.ProductDefaults.Rules.Single(r => r.Id == "KW-ARB-OWNER-001");
        // The one step-0 owner answer is the target named among the task's skills; test-dev is not owner of every test-first task.
        Assert.Single(owner.Decide!, clause => clause.Answer == "owner");
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
        // The no-task guard leads, then the Skills-label owner clause; the file-kind fallback answers no-mapping last.
        // not-owner reads plan prose, so only the model question answers it (ADR 0072).
        Assert.Equal("no-task", owner.Decide[0].Answer);
        Assert.Equal("owner", owner.Decide[1].Answer);
        Assert.Equal("no-mapping", owner.Decide[^1].Answer);
        Assert.DoesNotContain(owner.Decide, clause => string.Equals(clause.Answer, "not-owner", StringComparison.Ordinal));
        Assert.NotNull(owner.Ask);
        Assert.Contains("not-owner", owner.Ask.Criteria!.Keys);
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

    /// <summary>
    /// <c>doctor</c> stays quiet for <c>jev-1.13-free</c> only when every enabled step-1
    /// rule names it. A check scoped to one rule id stays green when a later step-1 rule
    /// omits the free id and <c>KW-ARB-CONFIG-007</c> warns for that rule.
    /// </summary>
    [Fact]
    public void Jev113Free_ProducesNoUntunedModelDiagnostic()
    {
        List<Diagnostic> diagnostics = RuleValidator.CheckModelTuning(WithModel("jev-1.13-free")).ToList();
        Assert.Empty(diagnostics);

        foreach (ArbiterRule rule in ArbiterConfig.ProductDefaults.Rules)
        {
            if (!rule.Enabled || rule.Ask is null)
            {
                continue;
            }

            Assert.Contains("jev-1.13-free", rule.Ask.TunedFor ?? []);
        }
    }

    /// <summary>
    /// The measured ids stay quiet in <c>doctor</c> only when every enabled step-1 rule
    /// names them. The <c>typesafe/</c> ids are OpenRouter's names for the same releases.
    /// </summary>
    [Theory]
    [InlineData("jev-1.14.0")]
    [InlineData("typesafe/jev-1.14")]
    [InlineData("typesafe/jev-1.13")]
    public void MeasuredJevIds_ProduceNoUntunedModelDiagnostic(string model)
    {
        List<Diagnostic> diagnostics = RuleValidator.CheckModelTuning(WithModel(model)).ToList();
        Assert.Empty(diagnostics);

        foreach (ArbiterRule rule in ArbiterConfig.ProductDefaults.Rules)
        {
            if (rule.Enabled && rule.Ask is not null)
            {
                Assert.Contains(model, rule.Ask.TunedFor ?? []);
            }
        }
    }

    /// <summary>A state too large for the smallest budget still fits a Jev release on either route.</summary>
    [Theory]
    [InlineData("jev-1.14.0", false)]
    [InlineData("typesafe/jev-1.14", false)]
    [InlineData("typesafe/jev-1.13", false)]
    [InlineData("typesafe/jev-router", true)]
    public void StateBudget_GivesJevReleasesTheJevBudget(string model, bool overBudget)
    {
        Assert.Equal(overBudget, StateBudget.IsOverBudget(model, 10_000, 100, 10_100, 30_300, 1));
    }

    /// <summary>
    /// <c>jev-1.13.0</c> stays a supported pin after the default moves to 1.14.
    /// </summary>
    [Fact]
    public void Jev113_StaysTunedAfterTheDefaultMoves()
    {
        Assert.Empty(RuleValidator.CheckModelTuning(WithModel("jev-1.13.0")));
    }

    /// <summary>
    /// <c>jev-1.13-free</c> is the free id of the shipped <c>jev-1.13.0</c> threshold.
    /// <c>KW-ARB-CLAIM-001</c> accepts it at the same floor.
    /// </summary>
    [Fact]
    public void Claim001_AcceptsJev113FreeAtTheJev113Threshold()
    {
        AssertChoiceAcceptsFreeModel("KW-ARB-CLAIM-001", 0.9);
    }

    /// <summary>
    /// <c>jev-1.13-free</c> shares the shipped <c>jev-1.13.0</c> floor on <c>KW-ARB-SCOPE-002</c>.
    /// </summary>
    [Fact]
    public void Scope002_AcceptsJev113FreeAtTheJev113Threshold()
    {
        AssertChoiceAcceptsFreeModel("KW-ARB-SCOPE-002", 0.8);
    }

    /// <summary>
    /// <c>jev-1.13-free</c> shares the shipped <c>jev-1.13.0</c> floor on <c>KW-ARB-OWNER-002</c>.
    /// </summary>
    [Fact]
    public void Owner002_AcceptsJev113FreeAtTheJev113Threshold()
    {
        AssertChoiceAcceptsFreeModel("KW-ARB-OWNER-002", 0.8);
    }

    /// <summary>
    /// <c>jev-1.13-free</c> shares the shipped <c>jev-1.13.0</c> ceiling on <c>KW-ARB-LENS-001</c>.
    /// </summary>
    [Fact]
    public void Lens001_AcceptsJev113FreeAtTheJev113Threshold()
    {
        ArbiterRule lens = Shipped("KW-ARB-LENS-001");
        Assert.NotNull(lens.Ask);
        Assert.Equal("noul", lens.Ask.Type);
        Assert.Null(lens.Ask.ConfidenceAtLeast);
        Assert.Equal(0.1, lens.Ask.ProbabilityBelow);
        Assert.Contains("jev-1.13.0", lens.Ask.TunedFor ?? []);
        Assert.Contains("jev-1.13-free", lens.Ask.TunedFor ?? []);
        AssertNoUntunedWarning("KW-ARB-LENS-001");
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

    private static ArbiterConfig WithModel(string model) =>
        ArbiterConfig.ProductDefaults with
        {
            Provider = new ArbiterProviderConfig(
                ArbiterProviderKind.Systemone, "https://api.typesafe.ai/v1", model, 3000),
        };

    private static void AssertChoiceAcceptsFreeModel(string ruleId, double confidenceAtLeast)
    {
        ArbiterRule rule = Shipped(ruleId);
        Assert.NotNull(rule.Ask);
        Assert.Equal("choice", rule.Ask.Type);
        Assert.Equal(confidenceAtLeast, rule.Ask.ConfidenceAtLeast);
        Assert.Null(rule.Ask.ProbabilityBelow);
        Assert.Contains("jev-1.13.0", rule.Ask.TunedFor ?? []);
        Assert.Contains("jev-1.13-free", rule.Ask.TunedFor ?? []);
        AssertNoUntunedWarning(ruleId);
    }

    private static void AssertNoUntunedWarning(string ruleId)
    {
        List<Diagnostic> diagnostics = RuleValidator.CheckModelTuning(WithModel("jev-1.13-free")).ToList();
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Code == RuleValidator.UntunedModel && diagnostic.Subject == ruleId);
    }

    private static ArbiterConfig ReplaceRule(ArbiterRule replacement) =>
        ArbiterConfig.ProductDefaults with
        {
            Rules = ArbiterConfig.ProductDefaults.Rules
                .Select(rule => string.Equals(rule.Id, replacement.Id, StringComparison.Ordinal) ? replacement : rule)
                .ToList(),
        };
}
