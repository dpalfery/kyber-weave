using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
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
    private const string GateReportRule = "KW-ARB-GATE-REPORT-001";

    private static readonly string ShippedRulesPath = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core", "Arbiter", "Rules", "default-rules.yml");

    /// <summary>
    /// The eighteen rules the file ships today, pinned so the new one joins without renumbering any.
    /// Rule ids are permanent: suppressions, baselines and code-scanning alerts key on them.
    /// </summary>
    private static readonly string[] ShippedBeforeThisSlice =
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

    /// <summary>The shipped rule, or a failure that names the id it should have.</summary>
    private static ArbiterRule Rule()
    {
        return Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
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
    /// Nothing is known about a report — no facts at all — so the digest's claim is unverified. D29
    /// makes that escalation, not a pass: the case the rule exists for is the report a worker cannot
    /// produce.
    /// </summary>
    [Fact]
    public void AReturnWithNoVerifiableReportEscalatesTheDispatch()
    {
        Assert.Equal(RuleEffects.Escalate, EffectFor(new ArbiterFactSet()));
    }

    [Fact]
    public void AnAgreeingReportAllowsTheDispatch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-agree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string reportPath = Path.Combine(tempDir, "gates.json");
            GateReport report = new(
                GateReport.CurrentSchema,
                [new GateResult("build", true, 0, "pass")]);
            File.WriteAllText(reportPath, ReviewJson.Write(report));

            string returned = $"TASK_GATE: PASS report={reportPath}";
            bool? agrees = GateReportFacts.Agrees(returned, tempDir);
            Assert.True(agrees);

            ArbiterFactSet facts = new ArbiterFactSet()
                .With(GateReportFacts.AgreesFact, agrees, ArbiterFactLabel.Derived);

            Assert.Equal(RuleEffects.Allow, EffectFor(facts));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ADisagreeingReportEscalatesTheDispatch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-disagree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string reportPath = Path.Combine(tempDir, "gates.json");
            GateReport report = new(
                GateReport.CurrentSchema,
                [new GateResult("build", true, 1, "failed")]);
            File.WriteAllText(reportPath, ReviewJson.Write(report));

            string returned = $"TASK_GATE: PASS report={reportPath}";
            bool? agrees = GateReportFacts.Agrees(returned, tempDir);
            Assert.False(agrees);

            ArbiterFactSet facts = new ArbiterFactSet()
                .With(GateReportFacts.AgreesFact, agrees, ArbiterFactLabel.Derived);

            Assert.Equal(RuleEffects.Escalate, EffectFor(facts));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AppendedClaimsWithFailDoNotCorroboratePass()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string passReport = Path.Combine(tempDir, "pass.json");
            GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
            File.WriteAllText(passReport, ReviewJson.Write(pass));

            string failReport = Path.Combine(tempDir, "fail.json");
            GateReport fail = new(GateReport.CurrentSchema, [new GateResult("build", true, 1, "failed")]);
            File.WriteAllText(failReport, ReviewJson.Write(fail));

            string returned = $"TASK_GATE: FAIL report={failReport}\nTASK_GATE: PASS report={passReport}";
            bool? agrees = GateReportFacts.Agrees(returned, tempDir);
            Assert.False(agrees);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AbsoluteReportPathOutsideRepositoryRootIsRefused()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-path-" + Guid.NewGuid().ToString("N"));
        string outsideDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(outsideDir);
        try
        {
            string outsideReport = Path.Combine(outsideDir, "gates.json");
            GateReport pass = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "pass")]);
            File.WriteAllText(outsideReport, ReviewJson.Write(pass));

            string returned = $"TASK_GATE: PASS report={outsideReport}";
            bool? agrees = GateReportFacts.Agrees(returned, tempDir);
            Assert.Null(agrees);
        }
        finally
        {
            Directory.Delete(tempDir, true);
            Directory.Delete(outsideDir, true);
        }
    }

    [Fact]
    public void AnEmptyOrPrecedingMarkerLineDoesNotAbortClaimExtraction()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-gate-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string reportPath = Path.Combine(tempDir, "gates.json");
            GateReport report = new(
                GateReport.CurrentSchema,
                [new GateResult("build", true, 0, "pass")]);
            File.WriteAllText(reportPath, ReviewJson.Write(report));

            string returned = $"Example: TASK_GATE:\nQuoted: TASK_GATE: PASS\nTASK_GATE: PASS report={reportPath}";
            bool? agrees = GateReportFacts.Agrees(returned, tempDir);
            Assert.True(agrees);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Exactly one answer may allow the dispatch, because a rule that allows on more than one answer
    /// lets a disagreement through with it. The disagreeing answers must escalate.
    /// </summary>
    [Fact]
    public void OnlyTheAgreeingReportAllowsAndDisagreementEscalates()
    {
        ArbiterRule rule = Rule();

        KeyValuePair<string, string> allow =
            Assert.Single(rule.Effects, entry => string.Equals(entry.Value, RuleEffects.Allow, StringComparison.Ordinal));
        Assert.Contains(allow.Key, rule.Answers);

        string[] escalating =
        [
            .. rule.Answers.Where(answer => string.Equals(rule.Effects[answer], RuleEffects.Escalate, StringComparison.Ordinal))
        ];
        Assert.True(
            escalating.Length >= 2,
            $"'{GateReportRule}' declares {escalating.Length} escalating answer(s). D29 names two: a missing " +
            "report and a report that disagrees with the claim.");
    }

    /// <summary>
    /// The rule file is the shipped contract, and its header is part of it: a reader checking how many
    /// rules ship must not have to count them. The new rule is added and the header updated to match,
    /// with no existing id renumbered.
    /// </summary>
    [Fact]
    public void TheShippedFileCountsTheNewRuleAndRenumbersNone()
    {
        Assert.True(File.Exists(ShippedRulesPath), $"the shipped rules file must live at '{ShippedRulesPath}'.");
        string[] lines = File.ReadAllLines(ShippedRulesPath);

        string[] ids =
        [
            .. lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- id: ", StringComparison.Ordinal))
                .Select(line => line["- id: ".Length..].Trim())
        ];

        int declared = DeclaredRuleCount(lines[0]);
        Assert.True(
            declared == ids.Length,
            $"The header declares {declared} shipped Arbiter rules and the file declares {ids.Length}. " +
            "Update the count in the first line with the rule.");
        Assert.Contains(GateReportRule, ids);
        Assert.All(ShippedBeforeThisSlice, id => Assert.Contains(id, ids));
        Assert.True(
            ids.Length >= ShippedBeforeThisSlice.Length + 1,
            $"The file ships {ids.Length} rules; '{GateReportRule}' is added to the {ShippedBeforeThisSlice.Length} " +
            "this tree already ships.");
    }

    /// <summary>
    /// The header's own count, read from its first line: "The 18 shipped Arbiter rules. ...".
    /// </summary>
    private static int DeclaredRuleCount(string headerLine)
    {
        string[] words = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int shipped = Array.IndexOf(words, "shipped");
        Assert.True(
            shipped > 0,
            $"The first line of the shipped rules file must state how many rules it holds. Read: '{headerLine}'.");
        string declared = words[shipped - 1];
        Assert.True(
            int.TryParse(declared, out int count),
            $"The first line of the shipped rules file must state its count as a number. Read: '{declared}'.");
        return count;
    }
}
