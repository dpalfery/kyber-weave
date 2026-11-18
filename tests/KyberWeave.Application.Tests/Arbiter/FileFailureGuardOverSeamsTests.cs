using System.Security;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The run store and the hook coverage check answer "could not be read" for the whole set of file-system failures,
/// not a hand-picked part of it. Each guard is driven here by a failure outside the old IOException /
/// UnauthorizedAccessException pair, so reverting any one of them fails its test.
/// </summary>
public sealed class FileFailureGuardOverSeamsTests
{
    private static readonly string Arbiter = ArbiterTestRoots.Under("repo", "arbiter");

    private readonly InMemoryFileSystem _files = new();
    private readonly InMemoryArbiterState _state;

    public FileFailureGuardOverSeamsTests()
    {
        _state = new InMemoryArbiterState(_files);
    }

    [Fact]
    public async Task AStoresDirectoryThatCannotBeListedStillStoresTheItem()
    {
        string stores = Path.Combine(Arbiter, "stores");
        ArbiterRunStore store = _state.RunStore(Arbiter);
        Assert.Equal(ArbiterStoreStatus.Stored, await store.AppendFindingAsync("RUN-A", "first"));

        // The store directory can be entered but not enumerated, so the sweep's listing throws.
        _files.DenyListing(stores);

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "second");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        Assert.Equal(["first", "second"], store.ReadFindings("RUN-A"));
    }

    [Fact]
    public async Task ASweepThatCannotReadASiblingStillStoresTheItem()
    {
        _files.CreateDirectory(Path.Combine(Arbiter, "stores", "review-sibling"));
        ArbiterRunStore store = _state.RunStore(
            Arbiter,
            options: null,
            clock: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
            newestItemTime: _ => throw new SecurityException("denied"));

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "a finding");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        Assert.Equal(["a finding"], store.ReadFindings("RUN-A"));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("agent.md")]
    public void AHookSourceWhoseReadIsRefusedDoesNotFire(string name)
    {
        bool fires = new ClaudeHookCoverage(_files).Fires(
            Path.Combine(Arbiter, name), "PreToolUse", "Bash", _ => throw new SecurityException("denied"));

        Assert.False(fires);
    }
}
