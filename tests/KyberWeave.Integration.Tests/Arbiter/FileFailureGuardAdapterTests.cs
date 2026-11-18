using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Data.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The real log adapter and the run store over it read a path the file system cannot take as "unreadable", not as a
/// crash. The NUL in the name is refused by the operating system itself, so only the real adapter can prove it.
/// </summary>
public sealed class FileFailureGuardAdapterTests : IDisposable
{
    private static readonly ArbiterLogFiles Logs = new();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

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
}
