using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-GATE-REPORT-001</c> (decision D29): a worker's dispatch claims what the worker will do,
/// and the digest that comes back must agree with it. A missing report escalates, and so does a
/// report line that disagrees with the claimed verdict; a report that matches is allowed through.
/// </summary>
/// <remarks>
/// <para>
/// The rule runs on <c>delegate.returned</c>, the trigger that fires when a dispatched worker's text
/// comes back. It is the only enforcement behind a <c>TASK_GATE:</c> line that no gate run produced:
/// <c>GateReportFacts</c> serves the review council today and nothing read <c>report=</c> at task end.
/// </para>
/// <para>
/// The rule is evaluated through <see cref="RuleEngine" /> one rule at a time, as the plan-read rule
/// tests do, because the other shipped delegate rules answer from facts this fixture does not supply
/// and their escalations would hide which rule answered.
/// </para>
/// </remarks>
public sealed class ArbiterGateReportRuleTests
{
    private readonly InMemoryArbiterState _state = new();

    private const string GateReportRule = "KW-ARB-GATE-REPORT-001";

    /// <summary>The shipped rule, or a failure that names the id it should have.</summary>
    private static ArbiterRule Rule()
    {
        return Assert.Single(
            ArbiterConfigDefaults.Product.Rules,
            rule => string.Equals(rule.Id, GateReportRule, StringComparison.Ordinal));
    }

    /// <summary>The effect the rule produces for a fact bag, resolved through its own answer table.</summary>
    private static string EffectFor(ArbiterFactSet facts)
    {
        ArbiterRule rule = Rule();
        string answer = RuleEngine.Decide(rule, facts);

        Assert.True(
            rule.Effects.TryGetValue(answer, out string? effect),
            $"'{GateReportRule}' answered '{answer}', which maps to no effect. A return the rule cannot " +
            "corroborate must reach a declared answer that escalates, never an undecided one.");
        return effect;
    }

    /// <summary>
    /// The trigger is the whole point: <c>delegate.returned</c> is where the worker's digest exists.
    /// A rule on a pre-dispatch trigger has nothing to compare the claim with.
    /// </summary>
    [Fact]
    public void TheShippedRuleRunsOnTheDelegateReturnedTrigger()
    {
        Assert.Equal("delegate.returned", Rule().Trigger);
    }

    /// <summary>
    /// Nothing is known about a report — no facts at all — so the claim cannot be read. ADR 0072 makes that
    /// undecidable at step 0: with no report text it allows, and with a report the model is asked whether the
    /// worker ran the gate. Only a confident "it did not" escalates.
    /// </summary>
    [Fact]
    public void AReturnWithNoVerifiableReportIsLeftToTheModel()
    {
        Assert.Equal("no-report", RuleEngine.Decide(Rule(), new ArbiterFactSet()));
        Assert.Equal(RuleEffects.Allow, EffectFor(new ArbiterFactSet()));

        ArbiterFactSet withReport = new ArbiterFactSet()
            .With(GateReportFacts.ReturnedTailFact, "Done. Tests pass.", ArbiterFactLabel.Asserted);
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Rule(), withReport));
        Assert.Equal(RuleEffects.Escalate, Rule().Effects["missing"]);
        Assert.Equal(RuleEffects.Allow, Rule().Effects["ran-gate"]);
    }

    /// <summary>
    /// The documentation developer has no task gate, so its return carries no TASK_GATE claim by design. When the host
    /// declares no task gate for the returning agent, step 0 allows before any model question is asked.
    /// </summary>
    [Fact]
    public void AReturnFromAnAgentWithNoDeclaredTaskGateIsAllowedAtStepZero()
    {
        ArbiterFactSet facts = new ArbiterFactSet()
            .With(GateReportFacts.ReturnedTailFact, "Closeout done. No gate run.", ArbiterFactLabel.Asserted)
            .With("config.task-gates-declared", false, ArbiterFactLabel.Derived);

        Assert.Equal("no-gates-declared", RuleEngine.Decide(Rule(), facts));
        Assert.Equal(RuleEffects.Allow, EffectFor(facts));
    }

    [Fact]
    public void AReturnFromAnAgentWithADeclaredTaskGateStillReachesTheModelQuestion()
    {
        ArbiterFactSet facts = new ArbiterFactSet()
            .With(GateReportFacts.ReturnedTailFact, "Done. Tests pass.", ArbiterFactLabel.Asserted)
            .With("config.task-gates-declared", true, ArbiterFactLabel.Derived);

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Rule(), facts));
    }

    /// <summary>The fact is read from the host's <c>review.task-gates</c> for the agent that returned, not guessed.</summary>
    [Theory]
    [InlineData("dev", true)]
    [InlineData("docs-dev", false)]
    public void TheDeclaredTaskGateFactComesFromTheHostConfiguration(string returningAgent, bool declared)
    {
        ReviewTaskGate gate = new("unit", ["**"], null, ["dotnet", "test"], []);
        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Review = new ReviewConfig
            {
                TaskGates = new Dictionary<string, IReadOnlyList<ReviewTaskGate>>(StringComparer.Ordinal) { ["dev"] = [gate] }
            }
        };

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.returned", returningAgent, "KYBER-ARBITER: true\n\nDo the work.", "conductor",
            ArbiterCallerSources.Harness, config);

        Assert.True(facts.TryGet("config.task-gates-declared", out ArbiterFact? fact));
        Assert.Equal(declared, fact.Value);
    }

    /// <summary>
    /// A host with only technology-keyed gates must not read as having none: the fact resolves through the
    /// technology the dispatch names, by the same resolver the gate run uses.
    /// </summary>
    [Theory]
    [InlineData("csharp", true)]
    [InlineData("cobol", false)]
    public void TheDeclaredTaskGateFactUsesTheDispatchedTechnology(string technology, bool declared)
    {
        ReviewTaskGate gate = new("unit", ["**"], null, ["dotnet", "test"], []);
        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Review = new ReviewConfig
            {
                TechnologyGates = new Dictionary<string, IReadOnlyList<ReviewTaskGate>>(StringComparer.Ordinal)
                {
                    ["csharp"] = [gate]
                }
            }
        };

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.returned", "developer",
            $"KYBER-ARBITER: true\nTECHNOLOGY: {technology}\n\nDo the work.", "conductor",
            ArbiterCallerSources.Harness, config);

        Assert.True(facts.TryGet("config.task-gates-declared", out ArbiterFact? fact));
        Assert.Equal(declared, fact.Value);
    }

    /// <summary>
    /// A technology inherits its parent template's gates, as the packet, the gate run and the prompt all resolve
    /// them. The Arbiter's fact must use the same resolution, or a host with gates only on the parent reads as
    /// having none and the return's gate report goes unchecked.
    /// </summary>
    [Fact]
    public void TheDeclaredTaskGateFactUsesTheParentsTechnology()
    {
        InMemoryFileSystem files = _state.Files;
        string standard = Path.Combine(ArbiterTestRoots.Under("repo"), "docs", "standards", "react", "react.standards.md");
        files.CreateDirectory(Path.GetDirectoryName(standard)!);
        files.WriteAllText(
            standard,
            """
            ---
            builds-on: typescript
            ---

            # React standard
            """);
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml("""
            ontology:
              docs-root: docs
            """);
        ReviewTaskGate gate = new("unit", ["**"], null, ["dotnet", "test"], []);
        config = new KyberWeaveConfig
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = config.Ontology,
            Review = new ReviewConfig
            {
                TechnologyGates = new Dictionary<string, IReadOnlyList<ReviewTaskGate>>(StringComparer.Ordinal)
                {
                    ["typescript"] = [gate]
                }
            }
        };

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.returned", "developer",
            "KYBER-ARBITER: true\nTECHNOLOGY: react\n\nDo the work.", "conductor",
            ArbiterCallerSources.Harness, config, repoRoot: ArbiterTestRoots.Under("repo"), files: files);

        Assert.True(facts.TryGet("config.task-gates-declared", out ArbiterFact? fact));
        Assert.Equal(true, fact.Value);
    }

    /// <summary>
    /// A dispatch naming a technology whose standard this process may not read must still produce the fact.
    /// The build is fail-open (ADR 0072: errors allow), so the fact reports the gates it could resolve and
    /// the hook keeps going, instead of the permission failure escaping and stopping the dispatch.
    /// </summary>
    [Fact]
    public void TheDeclaredTaskGateFactSurvivesAStandardTheProcessMayNotRead()
    {
        InMemoryFileSystem files = _state.Files;
        string standard = Path.Combine(ArbiterTestRoots.Under("repo"), "docs", "standards", "react", "react.standards.md");
        files.CreateDirectory(Path.GetDirectoryName(standard)!);
        files.WriteAllText(
            standard,
            """
            ---
            builds-on: typescript
            ---

            # React standard
            """);
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml("""
            ontology:
              docs-root: docs
            """);
        ReviewTaskGate gate = new("unit", ["**"], null, ["dotnet", "test"], []);
        config = new KyberWeaveConfig
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = config.Ontology,
            Review = new ReviewConfig
            {
                TechnologyGates = new Dictionary<string, IReadOnlyList<ReviewTaskGate>>(StringComparer.Ordinal)
                {
                    ["typescript"] = [gate]
                }
            }
        };

        files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && operation.Path == standard,
            new UnauthorizedAccessException());

        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.returned", "developer",
            "KYBER-ARBITER: true\nTECHNOLOGY: react\n\nDo the work.", "conductor",
            ArbiterCallerSources.Harness, config, repoRoot: ArbiterTestRoots.Under("repo"), files: files);

        Assert.True(facts.TryGet("config.task-gates-declared", out ArbiterFact? fact));
        Assert.Equal(false, fact.Value);
    }

    [Fact]
    public void AnAgreeingReportAllowsTheDispatch()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-agree");
        _state.Files.CreateDirectory(tempDir);
        string reportPath = Path.Combine(tempDir, "gates.json");
        GateReport report = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(reportPath, ReviewJson.Write(report));

        string returned = $"TASK_GATE: PASS report={reportPath}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.True(agrees);

        ArbiterFactSet facts = new ArbiterFactSet()
            .With(GateReportFacts.AgreesFact, agrees, ArbiterFactLabel.Derived);

        Assert.Equal(RuleEffects.Allow, EffectFor(facts));
    }

    [Fact]
    public void ADisagreeingReportEscalatesTheDispatch()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-disagree");
        _state.Files.CreateDirectory(tempDir);
        string reportPath = Path.Combine(tempDir, "gates.json");
        GateReport report = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 1, "failed")]);
        _state.Files.WriteAllText(reportPath, ReviewJson.Write(report));

        string returned = $"TASK_GATE: PASS report={reportPath}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.False(agrees);

        ArbiterFactSet facts = new ArbiterFactSet()
            .With(GateReportFacts.AgreesFact, agrees, ArbiterFactLabel.Derived);

        Assert.Equal(RuleEffects.Escalate, EffectFor(facts));
    }

    [Fact]
    public void FieldOrderDoesNotAffectClaimParsing()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-order");
        _state.Files.CreateDirectory(tempDir);
        string passReport = Path.Combine(tempDir, "pass.json");
        GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(passReport, ReviewJson.Write(pass));

        string returned = $"TASK_GATE: report={passReport} PASS";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.True(agrees);
    }

    [Fact]
    public void SuccessfulRetryWithNewReportCorroboratesPass()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-retry");
        _state.Files.CreateDirectory(tempDir);
        string failReport = Path.Combine(tempDir, "fail.json");
        GateReport fail = new(GateReport.CurrentSchema, [new GateResult("build", true, 1, "failed")]);
        _state.Files.WriteAllText(failReport, ReviewJson.Write(fail));

        string passReport = Path.Combine(tempDir, "pass.json");
        GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(passReport, ReviewJson.Write(pass));

        string returned = $"TASK_GATE: FAIL report={failReport}\nTASK_GATE: PASS report={passReport}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.True(agrees);
    }

    [Fact]
    public void ConflictingStatusesOnSameReportPathDoNotCorroboratePass()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-same-report");
        _state.Files.CreateDirectory(tempDir);
        string report = Path.Combine(tempDir, "gates.json");
        GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(report, ReviewJson.Write(pass));

        string returned = $"TASK_GATE: FAIL report={report}\nTASK_GATE: PASS report={report}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.False(agrees);
    }

    [Fact]
    public void ConflictingStatusesOnSameReportPathWithUnnormalizedSpellingDoNotCorroboratePass()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-unnormalized");
        _state.Files.CreateDirectory(tempDir);
        string report = Path.Combine(tempDir, "gates.json");
        GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(report, ReviewJson.Write(pass));

        string unnormalized = Path.Combine(tempDir, ".", "gates.json");
        string returned = $"TASK_GATE: FAIL report={unnormalized}\nTASK_GATE: PASS report={report}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.False(agrees);
    }

    [Fact]
    public void AbsoluteReportPathOutsideRepositoryRootIsRefused()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-path");
        string outsideDir = ArbiterTestRoots.Under("kw-test-gate-outside");
        _state.Files.CreateDirectory(tempDir);
        _state.Files.CreateDirectory(outsideDir);
        string outsideReport = Path.Combine(outsideDir, "gates.json");
        GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(outsideReport, ReviewJson.Write(pass));

        string returned = $"TASK_GATE: PASS report={outsideReport}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.Null(agrees);
    }

    [Fact]
    public void AnEmptyOrPrecedingMarkerLineDoesNotAbortClaimExtraction()
    {
        string tempDir = ArbiterTestRoots.Under("kw-test-gate-multi");
        _state.Files.CreateDirectory(tempDir);
        string reportPath = Path.Combine(tempDir, "gates.json");
        GateReport report = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "pass")]);
        _state.Files.WriteAllText(reportPath, ReviewJson.Write(report));

        string returned = $"Example: TASK_GATE:\nQuoted: TASK_GATE: PASS\nTASK_GATE: PASS report={reportPath}";
        bool? agrees = _state.GateReports().Agrees(returned, tempDir);
        Assert.True(agrees);
    }

    /// <summary>
    /// The machine contradiction always escalates, and the model's "no gate run" answer escalates; step 0 itself
    /// can answer only verified, disagrees or no-report, so it never escalates on a line it could not read.
    /// </summary>
    [Fact]
    public void DisagreementAndAConfidentNoGateAnswerEscalate()
    {
        ArbiterRule rule = Rule();

        Assert.Equal(RuleEffects.Escalate, rule.Effects["disagrees"]);
        Assert.Equal(RuleEffects.Escalate, rule.Effects["missing"]);
        Assert.DoesNotContain(rule.Decide!, clause => string.Equals(clause.Answer, "missing", StringComparison.Ordinal));
        Assert.NotNull(rule.Ask);
        Assert.Contains("missing", rule.Ask.Criteria!.Keys);
    }
}
