using KyberDash.Data.Collector;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Unit tests for <see cref="ReportedSessionUsage"/>: reported figures too large to add up are not
/// measured, never a wrapped (negative) total.
/// </summary>
public class ReportedSessionUsageTests
{
    private const long Huge = 6_000_000_000_000_000_000L;

    [Fact]
    public void Resolve_CacheClassesWhoseCombinedInputOverflows_IsNotMeasured()
    {
        CollectedSession session = Session(
            new CollectedModelUsage(0, "gpt-5", TokenClass.CacheRead, Huge),
            new CollectedModelUsage(0, "gpt-5", TokenClass.CacheWrite, Huge),
            new CollectedModelUsage(0, "gpt-5", TokenClass.Output, 5L));

        Assert.Null(ReportedSessionUsage.Resolve(session, []));
        Assert.Null(ReportedSessionUsage.Price(session, [], pricing: null));
    }

    [Fact]
    public void Resolve_ModelsWhoseSummedClassOverflows_IsNotMeasured()
    {
        CollectedSession session = Session(
            new CollectedModelUsage(0, "gpt-5", TokenClass.Output, Huge),
            new CollectedModelUsage(0, "gpt-4o", TokenClass.Output, Huge));

        Assert.Null(ReportedSessionUsage.Resolve(session, []));
    }

    [Fact]
    public void Resolve_ReportedClasses_AreSummed()
    {
        CollectedSession session = Session(
            new CollectedModelUsage(0, "gpt-5", TokenClass.Input, 10L),
            new CollectedModelUsage(0, "gpt-5", TokenClass.CacheRead, 20L),
            new CollectedModelUsage(0, "gpt-4o", TokenClass.Output, 7L));

        ReportedSessionUsageTotals? totals = ReportedSessionUsage.Resolve(session, []);

        Assert.NotNull(totals);
        Assert.Equal(30L, totals.CombinedInput);
        Assert.Equal(7L, totals.Output);
    }

    private static CollectedSession Session(params CollectedModelUsage[] usage) =>
        new("sess-1", "droid", "sess-1", null, null, usage);
}
