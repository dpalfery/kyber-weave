using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Core.Arbiter.Plans;
using System.Runtime.Versioning;
using System.Security;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Contracts.Repositories;
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
    private static readonly ArbiterLogFiles Logs = new();
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
    public void ABadReadLimitIsAProgrammingErrorNotAFileFailure()
    {
        string path = Path.Combine(_temp.Path, "any.txt");
        File.WriteAllText(path, "x");

        ArgumentOutOfRangeException negative = Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileReader.ReadAllText(path, -1));
        ArgumentOutOfRangeException tooLarge = Assert.Throws<ArgumentOutOfRangeException>(
            () => BoundedFileReader.ReadAllText(path, BoundedFileReader.MaxLimitBytes + 1));

        Assert.False(FileFailure.Is(negative));
        Assert.False(FileFailure.Is(tooLarge));
        Assert.True(FileFailure.Is(new ArgumentNullException("path")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AStoresDirectoryThatCannotBeListedStillStoresTheItem()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows or for a privileged user, so an unlistable directory cannot be simulated.");

        string arbiter = Path.Combine(_temp.Path, "arbiter");
        string stores = Path.Combine(arbiter, "stores");
        ArbiterRunStore store = RealArbiterState.RunStore(arbiter);
        Assert.Equal(ArbiterStoreStatus.Stored, await store.AppendFindingAsync("RUN-A", "first"));

        // Execute without read: the store directory can be entered but not enumerated, so the sweep's listing throws.
        File.SetUnixFileMode(stores, UnixFileMode.UserExecute);
        try
        {
            ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "second");

            Assert.Equal(ArbiterStoreStatus.Stored, status);
        }
        finally
        {
            File.SetUnixFileMode(stores, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(["first", "second"], store.ReadFindings("RUN-A"));
    }

    [Fact]
    public async Task ASweepThatCannotReadASiblingStillStoresTheItem()
    {
        string arbiter = Path.Combine(_temp.Path, "arbiter");
        Directory.CreateDirectory(Path.Combine(arbiter, "stores", "review-sibling"));
        ArbiterRunStore store = RealArbiterState.RunStore(
            arbiter,
            options: null,
            clock: TimeProvider.System,
            newestItemTime: _ => throw new SecurityException("denied"));

        ArbiterStoreStatus status = await store.AppendFindingAsync("RUN-A", "a finding");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
        Assert.Equal(["a finding"], store.ReadFindings("RUN-A"));
    }

    [Fact]
    public void ALogPathTheFileSystemCannotTakeReadsAsUnreadableNotAsACrash()
    {
        LogTail tail = Logs.ReadTail(Path.Combine(_temp.Path, "bad\0name.jsonl"), 1024, keepUnterminatedFinalLine: true);

        Assert.Empty(tail.Lines);
        Assert.NotNull(tail.Unreadable);
    }

    [Fact]
    public void AStoreWhoseDirectoryHoldsAnUnusablePathReadsAsEmpty()
    {
        Assert.Empty(ArbiterRunStore.ReadItems(Path.Combine(_temp.Path, "bad\0name.jsonl"), 1024, new ArbiterLogFiles()));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("agent.md")]
    public void AHookSourceWhoseReadIsRefusedDoesNotFire(string name)
    {
        bool fires = RealClaudeHookCoverage.Create().Fires(
            Path.Combine(_temp.Path, name), "PreToolUse", "Bash", _ => throw new SecurityException("denied"));

        Assert.False(fires);
    }
}
