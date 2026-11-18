using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The shared provider fake answers per rule, per call or by throwing, and records every request the code under
/// test made, so no test needs a provider double of its own.
/// </summary>
public sealed class FakeArbiterProviderTests
{
    private const string First = "KW-ARB-FIRST-001";
    private const string Second = "KW-ARB-SECOND-001";

    [Fact]
    public async Task AnswersTheNamedRuleAndLeavesTheOtherUnanswered()
    {
        FakeArbiterProvider provider = new()
        {
            Answers = { [First] = "yes" },
            Confidence = 0.9,
        };

        ArbiterStep1BatchResult result = await provider.AskStep1Async(
            [Rule(First, floor: 0.8), Rule(Second)],
            Facts());

        Assert.Equal("yes", result.AnswersByRule[First]);
        Assert.False(result.AnswersByRule.ContainsKey(Second));
        Assert.Equal(new ArbiterStep1Score(null, 0.9, 0.8), result.ScoresByRule?[First]);
        Assert.Equal([First, Second], provider.AskedRuleIds);
        Assert.Equal([2], provider.BatchSizes);
    }

    [Fact]
    public async Task OnlyAnAnswerTheTestGaveCarriesAScore()
    {
        FakeArbiterProvider provider = new()
        {
            Answers = { [First] = "yes" },
            DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
            Confidence = 0.95,
        };

        ArbiterStep1BatchResult result = await provider.AskStep1Async([Rule(First), Rule(Second)], Facts());

        Assert.Equal(0.95, result.ScoresByRule?[First].Confidence);
        Assert.False(
            result.ScoresByRule!.ContainsKey(Second),
            "a not-evaluated answer reports no confidence, not the provider's 0.95");
    }

    [Fact]
    public async Task AnswersAllAskedRulesWithOneAnswerAndRecordsTheFactsSent()
    {
        FakeArbiterProvider provider = FakeArbiterProvider.Answering("yes", confidence: 0.97);

        ArbiterStep1BatchResult result = await provider.AskStep1Async([Rule(First), Rule(Second)], Facts("T1"));

        Assert.Equal("yes", result.AnswersByRule[First]);
        Assert.Equal("yes", result.AnswersByRule[Second]);
        Assert.Equal(0.97, result.ScoresByRule?[Second].Confidence);
        Assert.Equal("T1", provider.FactsSent[0].Facts["delegation.task"].Value);
    }

    [Fact]
    public async Task AnswersEachCallFromItsOwnScriptAndRepeatsTheLastEntry()
    {
        FakeArbiterProvider provider = new()
        {
            Script =
            [
                _ => Batch((First, "yes")),
                _ => Batch((First, "no")),
            ],
        };

        Assert.Equal("yes", (await provider.AskStep1Async([Rule(First)], Facts())).AnswersByRule[First]);
        Assert.Equal("no", (await provider.AskStep1Async([Rule(First)], Facts())).AnswersByRule[First]);
        Assert.Equal("no", (await provider.AskStep1Async([Rule(First)], Facts())).AnswersByRule[First]);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal([0, 1, 2], provider.Calls.Select(call => call.Index));
    }

    [Fact]
    public async Task ThrowsForTheCallTheTestNamesAndAnswersTheOthers()
    {
        FakeArbiterProvider provider = new()
        {
            AnswerAsked = "yes",
            FaultWhen = call => call.Asks(Second) ? new HttpRequestException("provider is down") : null,
        };

        Assert.Equal("yes", (await provider.AskStep1Async([Rule(First)], Facts())).AnswersByRule[First]);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.AskStep1Async([Rule(First), Rule(Second)], Facts()));
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task NoneReportsNoRequestAndLeavesAskedRulesNotEvaluated()
    {
        FakeArbiterProvider provider = FakeArbiterProvider.None();

        ArbiterStep1BatchResult result = await provider.AskStep1Async([Rule(First)], Facts());

        Assert.Equal(ArbiterStep1BatchResult.NotEvaluated, result.AnswersByRule[First]);
        Assert.False(result.RequestSent);
        Assert.Null(result.Model);
    }

    private static ArbiterRule Rule(string id, double? floor = null) =>
        new(
            id,
            "delegate",
            "Question?",
            ["yes", "no"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["yes"] = "allow",
                ["no"] = "escalate",
            },
            Ask: new RuleAsk(
                "choice",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "delegation.task" },
                ConfidenceAtLeast: floor));

    private static ArbiterFactSet Facts(string task = "T1") =>
        new ArbiterFactSet().With("delegation.task", task, ArbiterFactLabel.Derived);

    private static ArbiterStep1BatchResult Batch(params (string RuleId, string Answer)[] answers) =>
        new(
            answers.ToDictionary(pair => pair.RuleId, pair => pair.Answer, StringComparer.Ordinal),
            "fake",
            null,
            RequestSent: true);
}
