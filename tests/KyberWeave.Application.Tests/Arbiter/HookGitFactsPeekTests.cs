using KyberWeave.Arbiter.Hooks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// How long the hook waits for a snapshot entry to appear, decided on a time provider that finishes every pause the
/// moment it starts, so no test sleeps.
/// </summary>
public sealed class HookGitFactsPeekTests
{
    private readonly FakeTimeProvider _fake = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AnUnchangedStoreResolvesAbsentAfterTwoReadsAndNoPause()
    {
        TimeProvider clock = new SelfAdvancing(_fake);
        int reads = 0;

        string? found = HookGitFacts.PeekUntilSettled<string>(
            clock,
            () =>
            {
                reads++;
                return "same document";
            },
            _ => null,
            TimeSpan.FromMinutes(5));

        Assert.Null(found);
        Assert.Equal(2, reads);
        Assert.Equal(TimeSpan.Zero, _fake.GetUtcNow() - new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AChangedStoreIsReadAgainAfterATwentyMillisecondPauseAndPairs()
    {
        TimeProvider clock = new SelfAdvancing(_fake);
        Queue<string> documents = new(["before the save", "after the save"]);

        string? found = HookGitFacts.PeekUntilSettled(
            clock,
            documents.Dequeue,
            text => text == "after the save" ? text : null,
            TimeSpan.FromMinutes(5));

        Assert.Equal("after the save", found);
        Assert.Equal(TimeSpan.FromMilliseconds(20), _fake.GetUtcNow() - new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AStoreThatKeepsChangingStopsOnceTheBudgetIsSpent()
    {
        TimeProvider clock = new SelfAdvancing(_fake);
        int reads = 0;

        string? found = HookGitFacts.PeekUntilSettled<string>(
            clock,
            () => (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
            TimeSpan.FromMilliseconds(100));

        Assert.Null(found);
        Assert.Equal(6, reads);
    }

    [Fact]
    public void AZeroBudgetReadsOnceAndStops()
    {
        TimeProvider clock = new SelfAdvancing(_fake);
        int reads = 0;

        string? found = HookGitFacts.PeekUntilSettled<string>(
            clock,
            () => (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
            TimeSpan.Zero);

        Assert.Null(found);
        Assert.Equal(1, reads);
    }

    /// <summary>A clock whose timers fire when they are created, moving the fake time forward by the delay asked for.</summary>
    private sealed class SelfAdvancing(FakeTimeProvider inner) : TimeProvider
    {
        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            inner.Advance(dueTime);
            callback(state);
            return new FiredTimer();
        }
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
