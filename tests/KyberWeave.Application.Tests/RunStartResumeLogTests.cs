using KyberWeave.Application.RunStart;
using KyberWeave.Cli.Commands.Run;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The resume log read in <c>kyber-weave run start</c>: a git log the step cannot read is a
/// warning on a resume that can still continue, while a programming error stays loud.
/// </summary>
/// <remarks>
/// These cases call the read itself rather than driving the CLI as a child process, because
/// the distinction under test — which exceptions become a warning — is unreachable through
/// the command's own guards: the refs it passes are always non-empty, so the argument
/// guards on <c>RunStartRunner.CommitMessagesAsync</c> never fire there. A future caller
/// passing an empty ref must hear about it instead of resuming with a warning.
/// </remarks>
public sealed class RunStartResumeLogTests
{
    /// <summary>
    /// A programming error while reading the resume log escapes instead of becoming a
    /// warning: an <c>ArgumentException</c> like the guards on
    /// <c>RunStartRunner.CommitMessagesAsync</c> throw is a defect, not a log that cannot be read.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AProgrammingErrorWhileReadingTheResumeLogEscapes()
    {
        ArgumentException failure = new("tip");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunStartService.TryReadResumeAsync(
                _ => throw failure,
                "origin/claude/fix/tighten-the-gate",
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cancelled wait while reading the resume log escapes instead of becoming a warning:
    /// cancellation is the caller's signal, not a log that cannot be read.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task ACancelledWaitWhileReadingTheResumeLogEscapes()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RunStartService.TryReadResumeAsync(
                _ => throw new OperationCanceledException(),
                "origin/claude/fix/tighten-the-gate",
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A missing log reader is a programming error, not an unreadable log: a null delegate
    /// throws instead of passing as a soft warning.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AMissingLogReaderWhileReadingTheResumeLogThrows()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RunStartService.TryReadResumeAsync(
                null!,
                "origin/claude/fix/tighten-the-gate",
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A git log that exits non-zero warns and continues with no finished tasks: a branch
    /// carrying none is a normal beginning, not a failure.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AGitLogExitFailureWhileReadingTheResumeLogWarnsAndContinues()
    {
        RunStartService.ResumeLogRead log = await RunStartService.TryReadResumeAsync(
            _ => Task.FromResult(new GitOutcome(1, string.Empty, "fatal: bad revision")),
            "origin/claude/fix/tighten-the-gate",
            TestContext.Current.CancellationToken);

        Assert.Empty(log.Completed);
        Assert.True(
            log.Warning is not null &&
            log.Warning.Contains("origin/claude/fix/tighten-the-gate", StringComparison.Ordinal),
            $"a failed git log must warn naming the ref, not '{log.Warning}'.");
    }

    /// <summary>
    /// A git log child that cannot start still warns and continues: a process error is a log
    /// that cannot be read, which the earlier exit-contract repair already established.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AGitLogProcessErrorWhileReadingTheResumeLogWarnsAndContinues()
    {
        RunStartService.ResumeLogRead log = await RunStartService.TryReadResumeAsync(
            _ => throw new IOException("Permission denied"),
            "origin/claude/fix/tighten-the-gate",
            TestContext.Current.CancellationToken);

        Assert.Empty(log.Completed);
        Assert.True(
            log.Warning is not null &&
            log.Warning.Contains("origin/claude/fix/tighten-the-gate", StringComparison.Ordinal),
            $"an unreadable git log must warn naming the ref, not '{log.Warning}'.");
    }

    /// <summary>A git log that reads cleanly reports its tasks with no warning.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task ASuccessfulResumeLogReadReportsNoWarning()
    {
        RunStartService.ResumeLogRead log = await RunStartService.TryReadResumeAsync(
            _ => Task.FromResult(new GitOutcome(0, string.Empty, string.Empty)),
            "origin/claude/fix/tighten-the-gate",
            TestContext.Current.CancellationToken);

        Assert.Empty(log.Completed);
        Assert.Null(log.Warning);
    }
}
