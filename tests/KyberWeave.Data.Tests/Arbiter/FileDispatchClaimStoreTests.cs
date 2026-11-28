using KyberWeave.Arbiter.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The dispatch claims through the in-memory file system and a controlled clock.</summary>
public sealed class FileDispatchClaimStoreTests : IDisposable
{
    private const string Project = "/project";

    private readonly ParkObservingTimeProvider _time = new();
    private readonly InMemoryFileSystem _files;
    private readonly FileDispatchClaimStore _store;

    public FileDispatchClaimStoreTests()
    {
        _files = new InMemoryFileSystem(_time);
        _store = new FileDispatchClaimStore(_files, _time, new FakeIdGenerator());
    }

    public void Dispose()
    {
        _time.Dispose();
    }

    [Fact]
    public void TheFirstClaimWinsAndRecordsItsOwner()
    {
        DispatchClaimKey key = new("toolu_1", "pre");

        Assert.True(_store.TryClaim(Project, key, "project-hook"));

        Assert.Equal("project-hook", _store.ReadOwner(Project, key));
    }

    [Fact]
    public void ASecondClaimLosesAndLeavesTheFirstOwner()
    {
        DispatchClaimKey key = new("toolu_1", "pre");
        _ = _store.TryClaim(Project, key, "project-hook");

        Assert.False(_store.TryClaim(Project, key, "user-hook"));

        Assert.Equal("project-hook", _store.ReadOwner(Project, key));
    }

    [Fact]
    public void ThePhasesOfOneDispatchAreClaimedSeparately()
    {
        Assert.True(_store.TryClaim(Project, new DispatchClaimKey("toolu_1", "pre"), "a"));
        Assert.True(_store.TryClaim(Project, new DispatchClaimKey("toolu_1", "post"), "b"));
    }

    [Fact]
    public void AnIdThatIsNotASafeFileNameStillClaimsOnceAndReadsBack()
    {
        DispatchClaimKey key = new("../escape/id with spaces", "pre");

        Assert.True(_store.TryClaim(Project, key, "owner"));
        Assert.False(_store.TryClaim(Project, key, "other"));
        Assert.Equal("owner", _store.ReadOwner(Project, key));
    }

    [Fact]
    public void AnUnclaimedDispatchHasNoOwner() =>
        Assert.Null(_store.ReadOwner(Project, new DispatchClaimKey("toolu_1", "pre")));

    [Fact]
    public void AClaimOlderThanADayIsPrunedByTheNextClaim()
    {
        DispatchClaimKey old = new("toolu_old", "pre");
        _ = _store.TryClaim(Project, old, "owner");

        _time.Advance(TimeSpan.FromDays(2));
        _ = _store.TryClaim(Project, new DispatchClaimKey("toolu_new", "pre"), "owner");

        Assert.Null(_store.ReadOwner(Project, old));
    }

    [Fact]
    public void ADispatchWithoutAClaimStopsWaitingAfterTheWaitWindow()
    {
        DispatchClaimKey key = new("toolu_1", "pre");

        bool claimed = RunWaitAdvancingTime(key, beforeClaim: null);

        Assert.False(claimed);
    }

    [Fact]
    public void AClaimMadeWhileWaitingEndsTheWaitWithTrue()
    {
        DispatchClaimKey key = new("toolu_1", "pre");
        FileDispatchClaimStore other = new(_files, _time, new FakeIdGenerator(100));

        bool claimed = RunWaitAdvancingTime(key, beforeClaim: () => other.TryClaim(Project, key, "project-hook"));

        Assert.True(claimed);
    }

    private bool RunWaitAdvancingTime(DispatchClaimKey key, Func<bool>? beforeClaim) =>
        _time.RunAdvancingOnParks(
            () => _store.WaitForClaim(Project, key),
            TimeSpan.FromMilliseconds(50),
            parkNumber: 3,
            atPark: beforeClaim is null ? null : () => _ = beforeClaim());
}
