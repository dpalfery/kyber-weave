using KyberWeave.Core.Processes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>The asynchronous session reads must return lines, fail at once when exhausted, and keep one read in flight.</summary>
public sealed class StdioSessionAsyncTests
{
    [Fact]
    public async Task ReadLineAsyncReturnsTheLineTheChildWrote()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System);

        session.SendLine("first");
        string line = await session.ReadLineAsync();

        Assert.Equal("first", line);
    }

    [Fact]
    public async Task ReadLineAsyncReturnsBothLinesDeliveredInOneRead()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System);

        session.SendLine("first");
        session.SendLine("second");

        Assert.Equal("first", await session.ReadLineAsync());
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
    public async Task ReadLineAsyncReportsATimeout()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(500), TimeProvider.System);

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() => session.ReadLineAsync());

        Assert.Contains("did not answer", failure.Message, StringComparison.Ordinal);
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
    public async Task ReadLineAsyncReportsEndOfStream()
    {
        SkipUnlessPosix();
        using StdioSession session = new("/bin/sh", ["-c", "exit 0"], TimeSpan.FromSeconds(10), TimeProvider.System);

        EndOfStreamException failure = await Assert.ThrowsAsync<EndOfStreamException>(() => session.ReadLineAsync());

        Assert.Contains("closed its standard output", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLineAsyncReportsTheLineCapFailure()
    {
        SkipUnlessPosix();
        string[] arguments = ["-c", "head -c 2000000 /dev/zero | tr '\\0' 'a'"];
        using StdioSession session = new("/bin/sh", arguments, TimeSpan.FromSeconds(30), TimeProvider.System);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadLineAsync());

        Assert.Contains("characters without ending the line", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForFirstErrorLineAsyncReturnsTheFirstErrorLine()
    {
        SkipUnlessPosix();
        string[] arguments = ["-c", "printf 'boom\\n' >&2"];
        using StdioSession session = new("/bin/sh", arguments, TimeSpan.FromSeconds(10), TimeProvider.System);

        string? line = await session.WaitForFirstErrorLineAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("boom", line);
    }

    /// <summary>The children are absolute POSIX paths; other platforms have no equivalent in this test.</summary>
    private static void SkipUnlessPosix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("The session tests start /bin children, which exist only on Linux and macOS.");
        }
    }
}
