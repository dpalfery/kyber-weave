using System.Security;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Every catch around a file read, stat or enumeration answers "could not be read" for the whole set of file-system
/// failures (<see cref="FileFailure"/>), not a hand-picked part of it. Each guard is driven here by a failure outside
/// the old IOException / UnauthorizedAccessException pair, so reverting any one of them fails its test.
/// </summary>
public sealed class FileFailureGuardTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheFilterTakesEveryFileSystemFailureAndNothingElse()
    {
        Exception[] failures =
        [
            new IOException(), new UnauthorizedAccessException(), new SecurityException(),
            new ArgumentException("path"), new NotSupportedException(), new PathTooLongException(),
        ];

        Assert.All(failures, failure => Assert.True(FileFailure.Is(failure), failure.GetType().Name));
        Assert.False(FileFailure.Is(new InvalidOperationException()));
        Assert.False(FileFailure.Is(new InvalidDataException()));
    }

    [Fact]
    public async Task ASweepThatCannotReadASiblingStillStoresTheItem()
    {
        string arbiter = Path.Combine(_temp.Path, "arbiter");
        Directory.CreateDirectory(Path.Combine(arbiter, "stores", "review-sibling"));
        ArbiterRunStore store = new(
            arbiter,
            options: null,
            clock: () => DateTimeOffset.UtcNow,
            newestItemTime: _ => throw new SecurityException("denied"));

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "a finding");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        Assert.Equal(["a finding"], store.ReadFindings("RUN-A"));
    }

    [Fact]
    public void ALogPathTheFileSystemCannotTakeReadsAsUnreadableNotAsACrash()
    {
        LogTail tail = ArbiterLogFiles.ReadTail(Path.Combine(_temp.Path, "bad\0name.jsonl"), 1024, keepUnterminatedFinalLine: true);

        Assert.Empty(tail.Lines);
        Assert.NotNull(tail.Unreadable);
    }

    [Fact]
    public void AStoreWhoseDirectoryHoldsAnUnusablePathReadsAsEmpty()
    {
        Assert.Empty(ArbiterRunStore.ReadItems(Path.Combine(_temp.Path, "bad\0name.jsonl"), 1024));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("agent.md")]
    public void AHookSourceWhoseReadIsRefusedDoesNotFire(string name)
    {
        bool fires = ClaudeHookCoverage.Fires(
            Path.Combine(_temp.Path, name), "PreToolUse", "Bash", _ => throw new SecurityException("denied"));

        Assert.False(fires);
    }
}
