using Xunit;
using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests;

/// <summary>
/// Pins D7's no-orphan rule for the bounded stdio session: whatever path the session takes, disposing it leaves no
/// child behind. The real child is a trivial <c>/bin/cat</c> echo or a <c>/bin/sleep</c>, both bounded by the session
/// budget, and the run that proves it goes through <c>scripts/proc-guard.sh run --</c>.
/// </summary>
/// <remarks>
/// The session's public surface (constructor, <c>SendLine</c>, <c>ReadLine</c>, <c>ProcessId</c>) is not fixed by the
/// contract. This test assumes that shape and is the seam the implementation must match or the digest must amend.
/// POSIX only: the child paths are absolute and the test runs on macOS and Linux.
/// </remarks>
public sealed class StdioSessionTests
{
    /// <summary>
    /// A session that exchanged one line, or one that hit its budget waiting, leaves no child process after Dispose.
    /// </summary>
    /// <remarks>
    /// The budget path is the one that leaks in practice: a stuck server returns no line, the read times out, and a
    /// <c>Dispose</c> that only closes stdin leaves it running. Both paths must reach the kill.
    /// </remarks>
    [Theory]
    [InlineData("exchange")]
    [InlineData("budget-exhausted")]
    public void StdioSession_DisposeOnEveryPath_LeavesNoChild(string path)
    {
        int processId;
        using (StdioSession session = path == "exchange"
                   ? new StdioSession("/bin/cat", [], TimeSpan.FromSeconds(10))
                   : new StdioSession("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(500)))
        {
            processId = session.ProcessId;
            if (path == "exchange")
            {
                session.SendLine("{\"jsonrpc\":\"2.0\",\"id\":1}");
                Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":1}", session.ReadLine());
            }
            else
            {
                Assert.Throws<TimeoutException>(() => session.ReadLine());
            }
        }

        AssertChildGone(processId);
    }

    /// <summary>
    /// Two lines delivered in one read are returned as two lines. A bounded reader that keeps only the first
    /// terminator it sees would drop the remainder of the chunk, and the session is the only reader of the stream.
    /// </summary>
    [Fact]
    public void StdioSession_TwoLinesInOneRead_ReturnsBoth()
    {
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10));

        session.SendLine("first");
        session.SendLine("second");

        Assert.Equal("first", session.ReadLine());
        Assert.Equal("second", session.ReadLine());
    }

    /// <summary>
    /// A child that writes more than the session's line cap without a terminator fails the read instead of growing
    /// the buffer until the process runs out of memory. The budget bounds time, not size.
    /// </summary>
    /// <remarks>
    /// The child is a shell that writes two million characters with no newline. A session that only bounded elapsed
    /// time would wait for a terminator that never comes while the buffer doubled, which is the failure mode this
    /// pins; the cap turns it into an <see cref="InvalidDataException"/> the caller can classify.
    /// </remarks>
    [Fact]
    public void StdioSession_LineOverCap_ThrowsAndLeavesNoChild()
    {
        int processId;
        using (StdioSession session = new(
                   "/bin/sh",
                   ["-c", "head -c 2000000 /dev/zero | tr '\\0' 'a'"],
                   TimeSpan.FromSeconds(30)))
        {
            processId = session.ProcessId;
            Assert.Throws<InvalidDataException>(() => session.ReadLine());
        }

        AssertChildGone(processId);
    }

    /// <summary>
    /// A child whose stderr line exceeds the session's cap is drained without keeping that line, and the next
    /// complete line still becomes the first error line.
    /// </summary>
    /// <remarks>
    /// The stderr drain exists so a child that writes to stderr never blocks on a full pipe. Draining it with an
    /// unbounded line reader defeats that purpose: a child that never writes a terminator grows the reader's buffer
    /// until the process runs out of memory, which is exactly what the stdout path already refuses to do. The
    /// over-cap line is discarded rather than truncated — it is not a diagnosis — and the drain continues so the
    /// child keeps flowing and a later real line is still reported. The hostile line is terminated here so the
    /// following line is a line of its own; an unterminated one never becomes a line at all.
    /// </remarks>
    [Fact]
    public void StdioSession_StderrLineOverCap_IsDiscardedAndTheNextLineIsReported()
    {
        using StdioSession session = new(
            "/bin/sh",
            ["-c", "head -c 2000000 /dev/zero | tr '\\0' 'a' >&2; printf '\\nboom\\n' >&2"],
            TimeSpan.FromSeconds(30));

        string? firstErrorLine = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (firstErrorLine is null && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            firstErrorLine = session.FirstErrorLine;
            if (firstErrorLine is null)
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }

        Assert.Equal("boom", firstErrorLine);
    }

    /// <summary>
    /// Reading the first error line after the session was disposed returns null instead of throwing. The stderr drain
    /// is still pending when <c>Dispose</c> closes the stream under it, so its task can fault.
    /// </summary>
    /// <remarks>
    /// <see cref="Task.IsCompleted"/> is true for a faulted task, and <see cref="Task{TResult}.Result"/> rethrows its
    /// exception, so a completed-with-fault drain would surface as an <c>AggregateException</c> at a caller that only
    /// wanted a hint for a failure message.
    /// </remarks>
    [Fact]
    public void StdioSession_FirstErrorLineAfterDispose_IsNull()
    {
        StdioSession session = new("/bin/sleep", ["30"], TimeSpan.FromSeconds(10));
        session.Dispose();

        Assert.Null(session.FirstErrorLine);
    }

    /// <summary>Passes when the pid no longer exists, or the process has exited and been reaped.</summary>
    private static void AssertChildGone(int processId)
    {
        try
        {
            using Process child = Process.GetProcessById(processId);
            Assert.True(child.HasExited, $"The session left child {processId} running after Dispose.");
        }
        catch (ArgumentException)
        {
            // No such process: the child is gone.
        }
    }
}
