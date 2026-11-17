using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>KW-ARB-MODE-002</c> exempts a task whose files are all test files, read by the machine fact
/// <c>plan.task.files-all-tests</c> from the task's own file list. The retired roster's clause on
/// <c>test-dev</c> is gone; a <c>test-dev</c> dispatch still answers <c>not-required</c> because its
/// task's files are test files.
/// </summary>
public sealed class ArbiterTestsOnlyTaskTests
{
    private const string RuleId = "KW-ARB-MODE-002";

    private static ArbiterRule Rule() =>
        Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));

    private static ArbiterFactSet Bag(params string[] files) =>
        new ArbiterFactSet()
            .With(TriggerFactBuilder.FilesAllTestsFact, null, ArbiterFactLabel.Derived)
            .With("plan.task.files", files.ToList(), ArbiterFactLabel.Derived);

    private static ArbiterFactSet TestFirstReturn(bool? allTests, string target = "csharp-dev") =>
        new ArbiterFactSet()
            .With("plan.task", "T3", ArbiterFactLabel.Derived)
            .With("plan.development-mode", "test-first", ArbiterFactLabel.Derived)
            .With("delegation.target", target, ArbiterFactLabel.Derived)
            .With("delegation.target-profile", "worker", ArbiterFactLabel.Derived)
            .With("gate.report.failing-run-first", false, ArbiterFactLabel.Derived)
            .With(TriggerFactBuilder.FilesAllTestsFact, allTests, ArbiterFactLabel.Derived);

    [Fact]
    public void EveryFileMatchingTheTestPatternsSetsTheFactTrue()
    {
        ArbiterFactSet facts = TriggerFactBuilder.WithTestsOnlyTaskFact(
            Bag(
                "tests/KyberWeave.Tests/Arbiter/ArbiterTestsOnlyTaskTests.cs",
                "dash/web/src/thing.spec.ts"),
            ArbiterConfig.ProductDefaults.TestFilePatterns);

        Assert.True(facts.TryGet(TriggerFactBuilder.FilesAllTestsFact, out ArbiterFact? fact));
        Assert.True(fact.Value is true);
    }

    [Fact]
    public void OneSourceFileSetsTheFactFalse()
    {
        ArbiterFactSet facts = TriggerFactBuilder.WithTestsOnlyTaskFact(
            Bag(
                "tests/KyberWeave.Tests/Arbiter/ArbiterTestsOnlyTaskTests.cs",
                "src/KyberWeave.Domain/Arbiter/Rules/ArbiterRule.cs"),
            ArbiterConfig.ProductDefaults.TestFilePatterns);

        Assert.True(facts.TryGet(TriggerFactBuilder.FilesAllTestsFact, out ArbiterFact? fact));
        Assert.True(fact.Value is false);
    }

    /// <summary>A task that names no files leaves the fact absent, so the rule reads its other clauses.</summary>
    [Fact]
    public void ATaskWithNoFilesLeavesTheFactAbsent()
    {
        ArbiterFactSet facts = TriggerFactBuilder.WithTestsOnlyTaskFact(
            Bag(), ArbiterConfig.ProductDefaults.TestFilePatterns);

        Assert.True(facts.TryGet(TriggerFactBuilder.FilesAllTestsFact, out ArbiterFact? fact));
        Assert.Null(fact.Value);
    }

    [Fact]
    public void TheMachineFactDecidesTheAnswer()
    {
        Assert.Equal("not-required", RuleEngine.Decide(Rule(), TestFirstReturn(true)));
        Assert.Equal("no-failing-run", RuleEngine.Decide(Rule(), TestFirstReturn(false)));
        Assert.Equal("no-failing-run", RuleEngine.Decide(Rule(), TestFirstReturn(null)));
    }

    /// <summary>
    /// The old roster's <c>test-dev</c> dispatch still answers <c>not-required</c>: its task's files
    /// are test files, so the machine fact is true.
    /// </summary>
    [Fact]
    public void ATestDevDispatchUnderTheOldRosterStillAnswersNotRequired()
    {
        Assert.Equal("not-required", RuleEngine.Decide(Rule(), TestFirstReturn(true, "test-dev")));
    }

    /// <summary>
    /// The agent name alone no longer exempts: with no fact for its task's files, a <c>test-dev</c>
    /// dispatch is judged by the failing-run fact like any other target.
    /// </summary>
    [Fact]
    public void TheAgentNameAloneNoLongerExempts()
    {
        Assert.Equal("no-failing-run", RuleEngine.Decide(Rule(), TestFirstReturn(null, "test-dev")));
    }
}
