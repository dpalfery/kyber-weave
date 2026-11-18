using System.Net;
using System.Net.Http.Headers;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The client's retry deadline and <c>Retry-After</c> date come from the clock it is given, so a retry the budget
/// allows or forbids is proven without waiting for it.
/// </summary>
public sealed class SystemOneClientClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 30, 0, TimeSpan.Zero);
    private static readonly Uri Endpoint = new("https://api.typesafe.ai/v1");

    private static HttpResponseMessage Busy(DateTimeOffset retryAt)
    {
        HttpResponseMessage busy = new((HttpStatusCode)429);
        busy.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
        return busy;
    }

    [Fact]
    public async Task ARetryAfterDateInsideTheBudgetIsWaitedOutAndRetried()
    {
        using QueuedHandler handler = new(Busy(Now.AddSeconds(1)), ArbiterProviderTestSupport.Answer());
        using SystemOneClient client = new(handler, Endpoint, ArbiterProviderTestSupport.Model, 5000, new ImmediateClock(Now));

        ArbiterStep1BatchResult result = await client.AskStep1Async([ArbiterProviderTestSupport.Rule()], ArbiterProviderTestSupport.Facts());

        Assert.Equal("within-task", result.AnswersByRule[ArbiterProviderTestSupport.RuleId]);
        Assert.Equal(2, handler.Authorizations.Count);
    }

    [Fact]
    public async Task ARetryAfterDatePastTheBudgetIsNotWaitedForAndNothingIsRetried()
    {
        using QueuedHandler handler = new(Busy(Now.AddSeconds(60)));
        using SystemOneClient client = new(handler, Endpoint, ArbiterProviderTestSupport.Model, 5000, new ImmediateClock(Now));

        ArbiterStep1BatchResult result = await client.AskStep1Async([ArbiterProviderTestSupport.Rule()], ArbiterProviderTestSupport.Facts());

        Assert.Equal(KyberWeave.Core.Arbiter.Rules.RuleEngine.Undecidable, result.AnswersByRule[ArbiterProviderTestSupport.RuleId]);
        Assert.True(result.RequestSent);
        _ = Assert.Single(handler.Authorizations);
    }
}
