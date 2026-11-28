using KyberWeave.Core.Processes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// The asynchronous session reads must keep the synchronous member's results, exceptions and one-read-in-flight
/// invariant. Each case runs both members against the same kind of real child.
/// </summary>
public sealed class StdioSessionAsyncTests
{
    [Fact]
    public async Task ReadLineAsyncReturnsTheSameLineAsReadLine()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System);

        session.SendLine("first");
        string sync = ReadLineSynchronously(session);
        session.SendLine("second");
        string async = await session.ReadLineAsync();

        Assert.Equal("first", sync);
        Assert.Equal("second", async);
    }

    [Fact]
    public async Task ReadLineAsyncReturnsBothLinesDeliveredInOneReadLikeReadLine()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System);

        session.SendLine("first");
        session.SendLine("second");

        Assert.Equal("first", ReadLineSynchronously(session));
        Assert.Equal("second", await session.ReadLineAsync());
    }

    [Fact]
    public async Task ReadLineAsyncFailsAtOnceWhenTheBudgetIsAlreadyExhausted()
    {
        SkipUnlessPosix();
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), time);
        time.Advance(TimeSpan.FromSeconds(11));

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() => session.ReadLineAsync());

        Assert.Contains("exhausted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLineAsyncReportsTheSameTimeoutAsReadLine()
    {
        SkipUnlessPosix();
        using StdioSession syncSession = new("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(500), TimeProvider.System);
        using StdioSession asyncSession = new("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(500), TimeProvider.System);

        TimeoutException sync = Assert.Throws<TimeoutException>(() => ReadLineSynchronously(syncSession));
        TimeoutException async = await Assert.ThrowsAsync<TimeoutException>(() => asyncSession.ReadLineAsync());

        Assert.Equal(sync.Message, async.Message);
    }

    [Fact]
    public async Task ReadLineAsyncKeepsOneReadInFlightAfterATimeout()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(300), TimeProvider.System);

        _ = await Assert.ThrowsAsync<TimeoutException>(() => session.ReadLineAsync());
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadLineAsync());
    }

    [Fact]
    public async Task ReadLineAsyncReportsTheSameEndOfStreamAsReadLine()
    {
        SkipUnlessPosix();
        using StdioSession syncSession = new("/bin/sh", ["-c", "exit 0"], TimeSpan.FromSeconds(10), TimeProvider.System);
        using StdioSession asyncSession = new("/bin/sh", ["-c", "exit 0"], TimeSpan.FromSeconds(10), TimeProvider.System);

        EndOfStreamException sync = Assert.Throws<EndOfStreamException>(() => ReadLineSynchronously(syncSession));
        EndOfStreamException async = await Assert.ThrowsAsync<EndOfStreamException>(() => asyncSession.ReadLineAsync());

        Assert.Equal(sync.Message, async.Message);
    }

    [Fact]
    public async Task ReadLineAsyncReportsTheSameLineCapFailureAsReadLine()
    {
        SkipUnlessPosix();
        string[] arguments = ["-c", "head -c 2000000 /dev/zero | tr '\\0' 'a'"];
        using StdioSession syncSession = new("/bin/sh", arguments, TimeSpan.FromSeconds(30), TimeProvider.System);
        using StdioSession asyncSession = new("/bin/sh", arguments, TimeSpan.FromSeconds(30), TimeProvider.System);

        InvalidDataException sync = Assert.Throws<InvalidDataException>(() => ReadLineSynchronously(syncSession));
        InvalidDataException async = await Assert.ThrowsAsync<InvalidDataException>(() => asyncSession.ReadLineAsync());

        Assert.Equal(sync.Message, async.Message);
    }

    [Fact]
    public async Task WaitForFirstErrorLineAsyncReturnsTheSameLineAsTheSyncWait()
    {
        SkipUnlessPosix();
        string[] arguments = ["-c", "printf 'boom\\n' >&2"];
        using StdioSession syncSession = new("/bin/sh", arguments, TimeSpan.FromSeconds(10), TimeProvider.System);
        using StdioSession asyncSession = new("/bin/sh", arguments, TimeSpan.FromSeconds(10), TimeProvider.System);

        string? sync = WaitForFirstErrorLineSynchronously(syncSession, TimeSpan.FromSeconds(10));
        string? async = await asyncSession.WaitForFirstErrorLineAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(sync, async);
        Assert.Equal("boom", async);
    }

    // The sync member is the behaviour under comparison. The helpers keep the calls out of async methods,
    // where the analyzer would otherwise demand the async twin for the synchronous half.
    private static string ReadLineSynchronously(StdioSession session) => session.ReadLine();

    private static string? WaitForFirstErrorLineSynchronously(StdioSession session, TimeSpan wait) =>
        session.WaitForFirstErrorLine(wait);

    /// <summary>The children are absolute POSIX paths; other platforms have no equivalent in this test.</summary>
    private static void SkipUnlessPosix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("The session tests start /bin children, which exist only on Linux and macOS.");
        }
    }
}
