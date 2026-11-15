using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-MODE-002</c>: in test-first mode one worker writes the tests, runs its task gate with <c>--red</c>,
/// implements, and runs the gate again. The proof is the two reports in the claimed report's folder, a failing-run
/// report older than the passing one, read by <see cref="GateReportFacts.FailingRunFirst" />. Every answer allows and
/// is logged; the answer is what the decision log records for monitoring, and an unreadable claim leaves it undecidable.
/// </summary>
public sealed class ArbiterMode002FailingRunFirstTests : IDisposable
{
    private const string RuleId = "KW-ARB-MODE-002";

    private readonly string _root = Directory.CreateTempSubdirectory("kw-mode-002-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static ArbiterRule Rule() =>
        Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));

    private string Write(string name, GateReport report, DateTime writtenAt)
    {
        string folder = Path.Combine(_root, ".agent_scratchpad", "T3");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, ReviewJson.Write(report));
        File.SetLastWriteTimeUtc(path, writtenAt);
        return path;
    }

    private static GateReport Failing() => new(
        GateReport.CurrentSchema,
        [
            new GateResult("format-whitespace", true, 0, "pass"),
            new GateResult("tests", true, 0, GateReportFacts.RedAcceptedSummaryPrefix + ": 2 tests failed on an assertion"),
        ]);

    private static GateReport Passing() => new(
        GateReport.CurrentSchema,
        [new GateResult("format-whitespace", true, 0, "pass"), new GateResult("tests", true, 0, "12 passed")]);

    private static readonly DateTime Earlier = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Earlier.AddMinutes(20);

    private static string Claim(string path) => $"STATUS: PASS\nTASK_GATE: PASS — agent=csharp-dev gates=2 failed=none skipped=0 report={path} runs=2";

    private static ArbiterFactSet TestFirstReturn(bool? failingRunFirst) =>
        new ArbiterFactSet()
            .With("plan.task", "T3", ArbiterFactLabel.Derived)
            .With("plan.development-mode", "test-first", ArbiterFactLabel.Derived)
            .With("delegation.target", "csharp-dev", ArbiterFactLabel.Derived)
            .With("delegation.target-profile", "worker", ArbiterFactLabel.Derived)
            .With(GateReportFacts.FailingRunFirstFact, failingRunFirst, ArbiterFactLabel.Derived);

    [Fact]
    public void AFailingRunReportOlderThanTheClaimedPassIsTheProof()
    {
        Write("task-gate-red.json", Failing(), Earlier);
        string pass = Write("task-gate.json", Passing(), Later);

        Assert.True(GateReportFacts.FailingRunFirst(Claim(pass), _root));
    }

    [Fact]
    public void AClaimedPassWithNoFailingRunBesideItIsAMachineFinding()
    {
        string pass = Write("task-gate.json", Passing(), Later);

        Assert.False(GateReportFacts.FailingRunFirst(Claim(pass), _root));
    }

    [Fact]
    public void AFailingRunWrittenAfterThePassDoesNotCount()
    {
        Write("task-gate-red.json", Failing(), Later);
        string pass = Write("task-gate.json", Passing(), Earlier);

        Assert.False(GateReportFacts.FailingRunFirst(Claim(pass), _root));
    }

    [Fact]
    public void AFailingRunReportClaimedAsThePassIsNotAPassingRun()
    {
        string red = Write("task-gate.json", Failing(), Later);

        Assert.False(GateReportFacts.FailingRunFirst(Claim(red), _root));
    }

    /// <summary>A rework dispatch adds a new passing run; the failing run of the task's first pass still precedes it.</summary>
    [Fact]
    public void AReworkPassStillHasTheTasksEarlierFailingRunBeforeIt()
    {
        Write("task-gate-red.json", Failing(), Earlier);
        Write("task-gate.json", Passing(), Earlier.AddMinutes(5));
        string rework = Write("task-gate-rework.json", Passing(), Later);

        Assert.True(GateReportFacts.FailingRunFirst(Claim(rework), _root));
    }

    [Theory]
    [InlineData("Done. Tests pass.")]
    [InlineData("TASK_GATE: NONE_DECLARED — agent=go-dev gates=0 failed=none skipped=0 report=<none> runs=1")]
    [InlineData("TASK_GATE: PASS — agent=csharp-dev report=.agent_scratchpad/T3/missing.json")]
    public void AClaimOrReportThatCannotBeReadLeavesTheFactAbsent(string returned)
    {
        Assert.Null(GateReportFacts.FailingRunFirst(returned, _root));
    }

    [Fact]
    public void AFailedGateClaimIsLeftToTheGateReportRule()
    {
        string failed = Write("task-gate.json", new GateReport(GateReport.CurrentSchema, [new GateResult("tests", true, 1, "1 failed")]), Later);

        Assert.Null(GateReportFacts.FailingRunFirst($"TASK_GATE: FAIL report={failed}", _root));
    }

    [Fact]
    public void TheMachineFactDecidesTheAnswerAndAMissingFailingRunIsLoggedNotStopped()
    {
        ArbiterRule rule = Rule();

        Assert.Equal("failing-run-first", RuleEngine.Decide(rule, TestFirstReturn(true)));
        Assert.Equal("no-failing-run", RuleEngine.Decide(rule, TestFirstReturn(false)));
        Assert.Equal(RuleEffects.Allow, rule.Effects["no-failing-run"]);
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(rule, TestFirstReturn(null)));
    }

    /// <summary>Each exemption the plan or the dispatch states allows before the fact is read, even when the fact is false.</summary>
    [Theory]
    [InlineData("plan.development-mode", "standard")]
    [InlineData("delegation.target", "test-dev")]
    [InlineData("delegation.target-profile", "documentation")]
    [InlineData("plan.test-contract.row", "Not applicable")]
    public void ATaskWithNoFailingRunOwedIsNotRequiredToShowOne(string fact, string value)
    {
        ArbiterFactSet facts = TestFirstReturn(false).With(fact, value, ArbiterFactLabel.Derived);

        Assert.Equal("not-required", RuleEngine.Decide(Rule(), facts));
        Assert.Equal(RuleEffects.Allow, Rule().Effects["not-required"]);
    }

    [Fact]
    public void AReturnWithNoPlanTaskIsNotJudged()
    {
        ArbiterFactSet facts = new ArbiterFactSet().With(GateReportFacts.FailingRunFirstFact, false, ArbiterFactLabel.Derived);

        Assert.Equal("no-task", RuleEngine.Decide(Rule(), facts));
    }

    [Fact]
    public void TheReturnTriggerAloneSuppliesTheFact()
    {
        Assert.Contains(GateReportFacts.FailingRunFirstFact, ArbiterTriggerCatalog.FactsByTrigger["delegate.returned"]);
        Assert.DoesNotContain(GateReportFacts.FailingRunFirstFact, ArbiterTriggerCatalog.FactsByTrigger["delegate"]);
    }
}
