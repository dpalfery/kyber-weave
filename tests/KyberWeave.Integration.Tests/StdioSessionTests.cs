using Xunit;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// Pins D7's no-orphan rule for the bounded stdio session: whatever path the session takes, disposing it leaves no
/// child behind. The real child is a trivial <c>/bin/cat</c> echo or a <c>/bin/sleep</c>, both bounded by the session
/// budget, and disposing the session is what proves it: whichever path the test takes, <c>Dispose</c> leaves no child
/// behind and nothing is left for a later run to reap.
/// </summary>
/// <remarks>
/// The session's public surface (constructor, <c>SendLine</c>, <c>ReadLine</c>, <c>ProcessId</c>) is not fixed by the
/// contract. This test assumes that shape and is the seam the implementation must match or the digest must amend.
/// POSIX only: the child paths are absolute and the test runs on macOS and Linux.
/// </remarks>
public sealed class StdioSessionTests
{
    /// <summary>
    /// The session budget is measured on the clock the session was given: once that clock has moved past the budget,
    /// a read fails at once, without waiting on the child.
    /// </summary>
    [Fact]
    public void StdioSession_ClockPastBudget_ReadFailsWithoutWaiting()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), time);
        time.Advance(TimeSpan.FromSeconds(11));

        TimeoutException failure = Assert.Throws<TimeoutException>(() => session.ReadLine());

        Assert.Contains("exhausted", failure.Message, StringComparison.Ordinal);
    }

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
                   ? new StdioSession("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System)
                   : new StdioSession("/bin/sleep", ["600"], TimeSpan.FromMilliseconds(500), TimeProvider.System))
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
        using StdioSession session = new("/bin/cat", [], TimeSpan.FromSeconds(10), TimeProvider.System);

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
                   TimeSpan.FromSeconds(30), TimeProvider.System))
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
            TimeSpan.FromSeconds(30), TimeProvider.System);

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
        StdioSession session = new("/bin/sleep", ["30"], TimeSpan.FromSeconds(10), TimeProvider.System);
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

    /// <summary>
    /// A file another process still holds open for writing makes exec fail with ETXTBSY. The session retries that
    /// start, so a file freed within the retry window starts instead of being reported as one that cannot start.
    /// </summary>
    /// <remarks>
    /// This is the race behind the stub server that "could not be started" on Linux CI, made deterministic: a holder
    /// child opens the stub for writing, says so, and closes it 100 ms later, inside the 200 ms the retries wait.
    /// </remarks>
    [Fact]
    public void StdioSession_ExecutableBrieflyOpenForWriting_StartsOnceItIsFreed()
    {
        SkipUnlessLinux();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kw-etxtbsy-");
        try
        {
            string stub = WriteExecutable(directory.FullName, "#!/bin/sh\necho started\n");
            using (StdioSession holder = HoldOpenForWriting(stub, "0.1"))
            {
                using StdioSession session = new(stub, [], TimeSpan.FromSeconds(10), TimeProvider.System);

                Assert.Equal("started", session.ReadLine());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A file that stays open for writing past every retry still fails with the original ETXTBSY, so the retry never
    /// hides a start that cannot happen.
    /// </summary>
    [Fact]
    public void StdioSession_ExecutableHeldOpenForWriting_ThrowsTextFileBusy()
    {
        SkipUnlessLinux();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kw-etxtbsy-");
        try
        {
            string stub = WriteExecutable(directory.FullName, "#!/bin/sh\necho started\n");
            using StdioSession holder = HoldOpenForWriting(stub, "30");

            System.ComponentModel.Win32Exception failure = Assert.Throws<System.ComponentModel.Win32Exception>(
                () => new StdioSession(stub, [], TimeSpan.FromSeconds(10), TimeProvider.System));

            Assert.Equal(TextFileBusy, failure.NativeErrorCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>ETXTBSY, the same value on Linux and macOS.</summary>
    private const int TextFileBusy = 26;

    /// <summary>
    /// Linux refuses to exec a file open for writing, script or binary. macOS runs a script regardless, so there the
    /// window these tests force does not exist and the tests would prove nothing.
    /// </summary>
    private static void SkipUnlessLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Only Linux refuses to exec a script that is open for writing.");
        }
    }

    private static string WriteExecutable(string directory, string text)
    {
        string path = Path.Combine(directory, "stub");
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Starts a child that opens <paramref name="path"/> for writing, then returns once it says it has.</summary>
    private static StdioSession HoldOpenForWriting(string path, string seconds)
    {
        StdioSession holder = new(
            "/bin/sh",
            ["-c", "exec 3>>\"$1\"; echo open; sleep \"$2\"", "holder", path, seconds],
            TimeSpan.FromSeconds(60),
            TimeProvider.System);
        Assert.Equal("open", holder.ReadLine());
        return holder;
    }
}
