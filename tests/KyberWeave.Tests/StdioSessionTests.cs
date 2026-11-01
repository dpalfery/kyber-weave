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
